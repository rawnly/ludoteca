using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using VirtualKey = Windows.System.VirtualKey;

namespace Ludoteca;

/// <summary>
/// Modalità TV: schermo intero con una fila di copertine, da usare con tastiera o controller.
/// Si apre con il pulsante "Modalità TV", con F11, con Start sul controller o avviando con --tv.
/// </summary>
public sealed partial class MainWindow
{
    private bool _tvMode;
    private bool _windowActive = true;
    private GamepadInput? _pad;
    private DispatcherQueueTimer? _clock;
    private ListViewItem? _scaledItem;
    private bool _launching;

    private GameEntry? TvSelected => TvList.SelectedItem as GameEntry;

    private void InitTv()
    {
        _pad = new GamepadInput(DispatcherQueue);
        _pad.Pressed += OnPad;

        // Mentre giochi Ludoteca è in secondo piano: il controller non deve muovere niente qui.
        Activated += (_, e) =>
        {
            _windowActive = e.WindowActivationState != WindowActivationState.Deactivated;
            if (_pad is not null) _pad.Enabled = _windowActive;
        };

        _clock = DispatcherQueue.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(15);
        _clock.Tick += (_, _) => TvClock.Text = DateTime.Now.ToString("HH:mm");
        _clock.Start();

        if (Environment.GetCommandLineArgs().Contains("--tv", StringComparer.OrdinalIgnoreCase))
            DispatcherQueue.TryEnqueue(EnterTv);
    }

    // ------------------------------------------------------------ entrare e uscire

    private void EnterTv()
    {
        if (_tvMode) return;
        _tvMode = true;
        TvView.Visibility = Visibility.Visible;
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        TvClock.Text = DateTime.Now.ToString("HH:mm");
        ShowTvGames();
    }

    private void ExitTv()
    {
        if (!_tvMode) return;
        _tvMode = false;
        TvView.Visibility = Visibility.Collapsed;
        AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        TvButton.Focus(FocusState.Programmatic);
    }

    private void EnterTv_Click(object sender, RoutedEventArgs e) => EnterTv();

    private void ToggleTv_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_tvMode) ExitTv(); else EnterTv();
    }

    /// <summary>Riempie la fila: prima i giochi giocati più di recente, come nei launcher.</summary>
    private void ShowTvGames()
    {
        if (!_tvMode) return;
        var keep = TvSelected?.CoverKey;
        var ordered = _games
            .OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        TvList.ItemsSource = ordered;
        if (ordered.Count == 0)
        {
            TvTitle.Text = "Nessun gioco trovato";
            TvDetails.Text = "";
            TvHero.Source = null;
            return;
        }
        TvList.SelectedIndex = Math.Max(0, ordered.FindIndex(g => g.CoverKey == keep));
        FocusTvSelection();
    }

    private void FocusTvSelection()
    {
        // Dopo il layout, quando il contenitore della copertina esiste.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (TvList.SelectedItem is null) return;
            TvList.ScrollIntoView(TvList.SelectedItem);
            if (TvList.ContainerFromIndex(TvList.SelectedIndex) is ListViewItem item) item.Focus(FocusState.Keyboard);
            else TvList.Focus(FocusState.Keyboard);
            ScaleSelected();
        });
    }

    // ------------------------------------------------------------ gioco selezionato

    private void TvList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TvSelected is not { } game) return;

        TvTitle.Text = game.Name;
        var parts = new List<string> { game.Source, game.LastPlayedText };
        if (game.PlaytimeMinutes is { } minutes and > 0) parts.Add($"{Format.Playtime(minutes)} di gioco");
        if (game.SizeBytes is { } bytes) parts.Add(Format.Size(bytes));
        TvDetails.Text = string.Join("  ·  ", parts);

        TvHero.Source = game.SteamAppId is { } id && Covers.SteamHero(id) is { } hero
            ? new BitmapImage(new Uri(hero))
            : game.Cover;
        ScaleSelected();
    }

    /// <summary>La copertina selezionata si ingrandisce un po', con un'animazione.</summary>
    private void ScaleSelected()
    {
        if (_scaledItem is not null) _scaledItem.Scale = Vector3.One;
        if (TvList.ContainerFromItem(TvList.SelectedItem) is not ListViewItem item) return;

        item.ScaleTransition ??= new Vector3Transition { Duration = TimeSpan.FromMilliseconds(150) };
        item.CenterPoint = new Vector3((float)item.ActualWidth / 2, (float)item.ActualHeight, 0);
        item.Scale = new Vector3(1.08f, 1.08f, 1);
        _scaledItem = item;
    }

    private void MoveTvSelection(int delta)
    {
        if (TvList.Items.Count == 0) return;
        TvList.SelectedIndex = Math.Clamp(TvList.SelectedIndex + delta, 0, TvList.Items.Count - 1);
        FocusTvSelection();
    }

    // ------------------------------------------------------------ azioni

    private async void TvList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GameEntry game) await TvPlayAsync(game);
    }

    private async void TvPlay_Click(object sender, RoutedEventArgs e)
    {
        if (TvSelected is { } game) await TvPlayAsync(game);
    }

    private async void TvOptions_Click(object sender, RoutedEventArgs e)
    {
        if (TvSelected is { } game) await ShowTvOptionsAsync(game);
    }

    private async Task TvPlayAsync(GameEntry game)
    {
        if (_launching) return;
        _launching = true;
        TvLaunchingText.Text = $"Avvio di {game.Name}…";
        TvLaunchingSource.Text = $"tramite {game.Source}";
        TvLaunching.Visibility = Visibility.Visible;
        try
        {
            await Launcher.PlayAsync(game);
            await Task.Delay(TimeSpan.FromSeconds(6)); // il tempo che il launcher apra il gioco
        }
        catch (Exception ex)
        {
            TvLaunchingText.Text = $"Non riesco ad avviare {game.Name}";
            TvLaunchingSource.Text = ex.Message;
            await Task.Delay(TimeSpan.FromSeconds(4));
        }
        finally
        {
            TvLaunching.Visibility = Visibility.Collapsed;
            _launching = false;
            ShowTvGames(); // il gioco appena avviato passa in testa alla fila
        }
    }

    private async Task ShowTvOptionsAsync(GameEntry game)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Title = game.Name,
            Content = $"{game.Source}  ·  {game.LastPlayedText}  ·  {game.SizeText}\n{game.InstallPath}",
            PrimaryButtonText = "Gioca",
            SecondaryButtonText = "Disinstalla",
            CloseButtonText = "Indietro",
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary) await TvPlayAsync(game);
        else if (result == ContentDialogResult.Secondary) await UninstallAsync(game);
        FocusTvSelection();
    }

    // ------------------------------------------------------------ tastiera

    private async void TvView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                ExitTv();
                break;
            case VirtualKey.Application when TvSelected is { } game: // tasto "menu" della tastiera
                e.Handled = true;
                await ShowTvOptionsAsync(game);
                break;
        }
    }

    // ------------------------------------------------------------ controller

    private void OnPad(PadButton button)
    {
        if (!_windowActive || _launching) return;

        // Una finestra di dialogo aperta (Opzioni, conferma di disinstallazione) ha la precedenza.
        if (OpenDialog() is { } dialog)
        {
            switch (button)
            {
                case PadButton.B: dialog.Hide(); break;
                case PadButton.A: InvokeFocused(); break;
                case PadButton.Left or PadButton.Right or PadButton.Up or PadButton.Down:
                    FocusManager.TryMoveFocus(ToDirection(button), new FindNextElementOptions { SearchRoot = dialog });
                    break;
            }
            return;
        }

        if (!_tvMode)
        {
            if (button == PadButton.Start) EnterTv();
            return;
        }

        var inList = IsInside(FocusManager.GetFocusedElement(Content.XamlRoot) as DependencyObject, TvList);
        switch (button)
        {
            case PadButton.Left or PadButton.Right when inList:
                MoveTvSelection(button == PadButton.Left ? -1 : 1);
                break;
            case PadButton.Left or PadButton.Right:
                // tra i pulsanti Gioca e Opzioni
                (TvPlayButton.FocusState == FocusState.Unfocused ? TvPlayButton : TvOptionsButton).Focus(FocusState.Keyboard);
                break;
            case PadButton.Up when inList:
                TvPlayButton.Focus(FocusState.Keyboard);
                break;
            case PadButton.Down or PadButton.B when !inList:
                FocusTvSelection();
                break;
            case PadButton.LeftShoulder:
                MoveTvSelection(-5);
                break;
            case PadButton.RightShoulder:
                MoveTvSelection(5);
                break;
            case PadButton.A when inList:
                if (TvSelected is { } selected) _ = TvPlayAsync(selected);
                break;
            case PadButton.A:
                InvokeFocused();
                break;
            case PadButton.Y or PadButton.Start:
                if (TvSelected is { } game) _ = ShowTvOptionsAsync(game);
                break;
            case PadButton.View:
                ExitTv();
                break;
        }
    }

    private ContentDialog? OpenDialog() =>
        Content.XamlRoot is { } root
            ? VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Select(p => p.Child).OfType<ContentDialog>().FirstOrDefault()
            : null;

    /// <summary>"Preme" il pulsante che ha il focus, come farebbe Invio.</summary>
    private void InvokeFocused()
    {
        if (FocusManager.GetFocusedElement(Content.XamlRoot) is not ButtonBase button) return;
        var peer = FrameworkElementAutomationPeer.FromElement(button) ?? FrameworkElementAutomationPeer.CreatePeerForElement(button);
        (peer?.GetPattern(PatternInterface.Invoke) as IInvokeProvider)?.Invoke();
    }

    private static FocusNavigationDirection ToDirection(PadButton b) => b switch
    {
        PadButton.Left => FocusNavigationDirection.Left,
        PadButton.Right => FocusNavigationDirection.Right,
        PadButton.Up => FocusNavigationDirection.Up,
        _ => FocusNavigationDirection.Down,
    };

    private static bool IsInside(DependencyObject? element, DependencyObject container)
    {
        for (var e = element; e is not null; e = VisualTreeHelper.GetParent(e))
            if (ReferenceEquals(e, container)) return true;
        return false;
    }
}
