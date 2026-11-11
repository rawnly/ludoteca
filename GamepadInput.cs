using Microsoft.UI.Dispatching;
using Windows.Gaming.Input;

namespace Ludoteca;

public enum PadButton { Up, Down, Left, Right, A, B, X, Y, Start, View, LeftShoulder, RightShoulder }

/// <summary>
/// Le app WinUI 3 per desktop non ricevono il controller come le app UWP su Xbox: qui si leggono
/// i controller compatibili Xbox (XInput) con Windows.Gaming.Input, 60 volte al secondo, e si
/// trasformano in pressioni di "tasti" (con la ripetizione quando tieni premuta una direzione).
/// </summary>
public sealed class GamepadInput
{
    private const double StickThreshold = 0.55;
    private static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(380);
    private static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(110);

    private readonly DispatcherQueueTimer _timer;
    private readonly Dictionary<PadButton, DateTime> _heldSince = [];
    private readonly Dictionary<PadButton, DateTime> _lastRepeat = [];

    /// <summary>Chiamato sul thread della UI per ogni pressione (e ripetizione, per le direzioni).</summary>
    public event Action<PadButton>? Pressed;

    /// <summary>Se false si ignora il controller (es. la finestra non è in primo piano e stai giocando).</summary>
    public bool Enabled { get; set; } = true;

    public bool IsConnected => Gamepad.Gamepads.Count > 0;

    public GamepadInput(DispatcherQueue dispatcher)
    {
        // Iscriversi agli eventi fa sì che Windows popoli subito Gamepad.Gamepads.
        Gamepad.GamepadAdded += (_, _) => { };
        Gamepad.GamepadRemoved += (_, _) => { };

        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    private bool _wasEnabled;

    private void Poll()
    {
        if (!Enabled)
        {
            _wasEnabled = false;
            _heldSince.Clear();
            return;
        }

        var pressed = new HashSet<PadButton>();
        foreach (var pad in Gamepad.Gamepads.ToArray())
        {
            GamepadReading r;
            try { r = pad.GetCurrentReading(); } catch { continue; }
            var b = r.Buttons;

            if (b.HasFlag(GamepadButtons.DPadUp) || r.LeftThumbstickY > StickThreshold) pressed.Add(PadButton.Up);
            if (b.HasFlag(GamepadButtons.DPadDown) || r.LeftThumbstickY < -StickThreshold) pressed.Add(PadButton.Down);
            if (b.HasFlag(GamepadButtons.DPadLeft) || r.LeftThumbstickX < -StickThreshold) pressed.Add(PadButton.Left);
            if (b.HasFlag(GamepadButtons.DPadRight) || r.LeftThumbstickX > StickThreshold) pressed.Add(PadButton.Right);
            if (b.HasFlag(GamepadButtons.A)) pressed.Add(PadButton.A);
            if (b.HasFlag(GamepadButtons.B)) pressed.Add(PadButton.B);
            if (b.HasFlag(GamepadButtons.X)) pressed.Add(PadButton.X);
            if (b.HasFlag(GamepadButtons.Y)) pressed.Add(PadButton.Y);
            if (b.HasFlag(GamepadButtons.Menu)) pressed.Add(PadButton.Start);
            if (b.HasFlag(GamepadButtons.View)) pressed.Add(PadButton.View);
            if (b.HasFlag(GamepadButtons.LeftShoulder)) pressed.Add(PadButton.LeftShoulder);
            if (b.HasFlag(GamepadButtons.RightShoulder)) pressed.Add(PadButton.RightShoulder);
        }

        var now = DateTime.UtcNow;

        // Appena la finestra torna in primo piano (o all'avvio), i tasti GIÀ premuti non contano:
        // valgono solo dopo essere stati rilasciati. Altrimenti, per esempio, A tenuto premuto
        // mentre torni su Ludoteca avvierebbe il gioco selezionato.
        if (!_wasEnabled)
        {
            _wasEnabled = true;
            foreach (var button in pressed)
            {
                _heldSince[button] = now.AddDays(1); // mai "tenuto abbastanza" per ripetere
                _lastRepeat[button] = now;
            }
            return;
        }

        foreach (var button in Enum.GetValues<PadButton>())
        {
            if (!pressed.Contains(button))
            {
                _heldSince.Remove(button);
                continue;
            }

            if (!_heldSince.TryGetValue(button, out var since))
            {
                // appena premuto
                _heldSince[button] = now;
                _lastRepeat[button] = now;
                Pressed?.Invoke(button);
            }
            else if (IsDirection(button) && now - since >= RepeatDelay && now - _lastRepeat[button] >= RepeatInterval)
            {
                // tenuto premuto: si ripete, come una freccia della tastiera
                _lastRepeat[button] = now;
                Pressed?.Invoke(button);
            }
        }
    }

    private static bool IsDirection(PadButton b) => b is PadButton.Up or PadButton.Down or PadButton.Left or PadButton.Right;
}
