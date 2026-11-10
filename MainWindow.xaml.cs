using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;

namespace Ludoteca;

public sealed partial class MainWindow : Window
{
    /// <summary>Larghezza minima della griglia consigli per passare da 2x2 a 3x3.</summary>
    private const double ThreeColumnWidth = 760;

    private List<GameEntry> _games = [];
    private List<GameEntry> _visible = [];
    private List<GameEntry> _recommended = [];
    private int _recColumns = 3;
    private bool _sizesDone;
    private AppSettings _settings = AppSettings.Load();

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 820));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Ludoteca.ico"));

        UpdateSecretPlaceholder();
        Nav.SelectedItem = Nav.MenuItems[0];
        Nav.Loaded += (_, _) =>
        {
            if (Nav.SettingsItem is NavigationViewItem settingsItem)
            {
                settingsItem.Content = "Impostazioni";
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(settingsItem, "Impostazioni");
            }
        };
        _ = RefreshAsync();
    }

    // ------------------------------------------------------------ scansione

    private async Task RefreshAsync()
    {
        Busy.IsActive = true;
        RefreshButton.IsEnabled = false;
        CountText.Text = "Sto cercando i giochi…";
        try
        {
            var result = await Task.Run(GameScanner.Scan);
            _games = result.Games;
            _sizesDone = false;
            ApplyFilter();
            UpdateRecommendations();
            _ = ComputeSizesAsync(_games);
            _ = LoadCoversAsync(_games);

            if (result.Warnings.Count > 0)
                ShowInfo("Alcune fonti non sono state lette", string.Join("\n", result.Warnings), InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowInfo("Errore durante la ricerca", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            Busy.IsActive = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private void ApplyFilter()
    {
        var query = Search.Text?.Trim();
        var visible = string.IsNullOrEmpty(query)
            ? _games
            : _games.Where(g => g.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();

        AllList.ItemsSource = visible;
        DriveGroups.Source = visible
            .GroupBy(g => g.Drive)
            .OrderBy(g => g.Key)
            .Select(g => new DriveGroup(g.Key, g))
            .ToList();
        DriveList.ItemsSource = DriveGroups.View;
        _visible = visible;
        UpdateCountText();
        UpdateRecPanelVisibility();
    }

    private void UpdateCountText()
    {
        var drives = _visible.Select(g => g.Drive).Distinct().Count();
        var text = _visible.Count == _games.Count
            ? $"{_games.Count} giochi su {drives} {(drives == 1 ? "disco" : "dischi")}"
            : $"{_visible.Count} di {_games.Count} giochi";
        if (_visible.Count > 0 && _visible.All(g => g.SizeBytes is not null))
            text += $"  ·  {Format.Size(_visible.Sum(g => g.SizeBytes!.Value))} in totale";
        CountText.Text = text;
    }

    // ------------------------------------------------------------ dimensioni

    private CancellationTokenSource? _sizeCts;

    /// <summary>Calcola le dimensioni in background, al massimo 4 cartelle alla volta.</summary>
    private async Task ComputeSizesAsync(List<GameEntry> games)
    {
        _sizeCts?.Cancel();
        var cts = _sizeCts = new CancellationTokenSource();
        var ui = DispatcherQueue;

        try
        {
            await Task.Run(() => Parallel.ForEach(games,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cts.Token },
                game =>
                {
                    var size = Format.FolderSize(game.InstallPath, cts.Token);
                    ui.TryEnqueue(() =>
                    {
                        game.SizeBytes = size;
                        UpdateCountText();
                        // Quando tutte le dimensioni sono note, i consigli si ricalcolano pesando lo spazio vero.
                        if (!_sizesDone && ReferenceEquals(games, _games) && games.All(g => g.SizeBytes is not null))
                        {
                            _sizesDone = true;
                            UpdateRecommendations();
                        }
                    });
                }));
        }
        catch (OperationCanceledException)
        {
            // è partita una nuova ricerca: questi risultati non servono più
        }
    }

    // ------------------------------------------------------------ copertine

    private CancellationTokenSource? _coverCts;

    private async Task LoadCoversAsync(List<GameEntry> games)
    {
        _coverCts?.Cancel();
        var cts = _coverCts = new CancellationTokenSource();
        var ui = DispatcherQueue;
        var settings = _settings;

        try
        {
            var missing = await Task.Run(() => Covers.LoadAsync(games, settings,
                (game, file, isIcon) => ui.TryEnqueue(() =>
                {
                    var image = new BitmapImage(new Uri(file)) { DecodePixelWidth = 168 };
                    if (isIcon) game.Icon = image; else game.Cover = image;
                }),
                cts.Token));

            var names = string.Join(", ", missing.Select(g => g.Name));
            SettingsStatus.Text = missing.Count == 0
                ? "Tutti i giochi hanno la copertina."
                : settings.SteamGridDbKey.Length == 0
                    ? $"Senza copertina (al suo posto c'è l'icona del gioco): {names}. Con la chiave di SteamGridDB si possono cercare anche lì."
                    : $"Nessuna fonte ha la copertina di: {names}. Al suo posto c'è l'icona del gioco.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (SteamGridDbAuthException ex)
        {
            ShowInfo("Copertine non disponibili", ex.Message + " Puoi correggerla in Impostazioni.", InfoBarSeverity.Warning);
            SettingsStatus.Text = ex.Message;
        }
        catch (Exception ex)
        {
            // Senza rete o con IGDB irraggiungibile l'app funziona lo stesso, solo senza copertine nuove.
            SettingsStatus.Text = $"Non riesco a scaricare le copertine: {ex.Message}";
        }
    }

    // ------------------------------------------------------------ griglia "Da disinstallare"

    private void UpdateRecommendations()
    {
        _recommended = Recommender.Pick(_games, 9);
        var freeable = _recommended.Where(g => g.SizeBytes is not null).Sum(g => g.SizeBytes!.Value);
        RecSubtitle.Text = $"Giochi a cui non giochi da almeno un mese, i più grandi e dimenticati per primi." +
                           (freeable > 0 ? $" Disinstallandoli tutti liberi {Format.Size(freeable)}." : "");
        ShowRecommendations();
        UpdateRecPanelVisibility();
    }

    private void ShowRecommendations()
    {
        RecLayout.MaximumRowsOrColumns = _recColumns;
        RecRepeater.ItemsSource = _recommended.Take(_recColumns * _recColumns).ToList();
    }

    private void RecPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var columns = e.NewSize.Width >= ThreeColumnWidth ? 3 : 2;
        if (columns == _recColumns) return;
        _recColumns = columns;
        ShowRecommendations();
    }

    /// <summary>La griglia si nasconde durante una ricerca o se non c'è niente da consigliare.</summary>
    private void UpdateRecPanelVisibility() =>
        RecPanel.Visibility = _recommended.Count > 0 && string.IsNullOrWhiteSpace(Search.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

    // ------------------------------------------------------------ navigazione e ricerca

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var view = args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string;
        AllList.Visibility = view == "all" ? Visibility.Visible : Visibility.Collapsed;
        DriveList.Visibility = view == "drives" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = view == "settings" ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.Visibility = view == "settings" ? Visibility.Collapsed : Visibility.Visible;
        CountText.Visibility = RefreshButton.Visibility;
        PageTitle.Text = view switch
        {
            "drives" => "Per disco",
            "settings" => "Impostazioni",
            _ => "Tutti i giochi",
        };
    }

    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyFilter();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    // ------------------------------------------------------------ impostazioni

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var key = SecretBox.Password.Trim();
        if (key.Length == 0) key = _settings.SteamGridDbKey;
        if (key.Length == 0)
        {
            SettingsStatus.Text = "Incolla la chiave API di SteamGridDB.";
            return;
        }

        SaveSettingsButton.IsEnabled = false;
        SettingsStatus.Text = "Controllo la chiave…";
        try
        {
            await new SteamGridDb(key).TestAsync();
            _settings = new AppSettings { SteamGridDbKey = key };
            _settings.Save();
            SecretBox.Password = "";
            UpdateSecretPlaceholder();
            SettingsStatus.Text = "Chiave valida e salvata. Scarico le copertine…";
            await LoadCoversAsync(_games);
        }
        catch (SteamGridDbAuthException ex)
        {
            SettingsStatus.Text = ex.Message;
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = $"Non riesco a contattare SteamGridDB: {ex.Message}";
        }
        finally
        {
            SaveSettingsButton.IsEnabled = true;
        }
    }

    private void UpdateSecretPlaceholder() =>
        SecretBox.PlaceholderText = _settings.SteamGridDbKey.Length > 0
            ? "Già salvato: lascia vuoto per mantenerlo"
            : "";

    private async void ClearCovers_Click(object sender, RoutedEventArgs e)
    {
        _coverCts?.Cancel();
        foreach (var game in _games)
        {
            game.Cover = null;
            game.Icon = null;
        }
        try
        {
            Covers.Clear();
            SettingsStatus.Text = "Cache svuotata. Scarico di nuovo le copertine…";
            await LoadCoversAsync(_games);
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = $"Non riesco a svuotare la cache: {ex.Message}";
        }
    }

    // ------------------------------------------------------------ azioni sul gioco

    private static GameEntry? GameOf(object sender) => (sender as FrameworkElement)?.DataContext as GameEntry;

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (GameOf(sender) is { } game) Uninstaller.OpenFolder(game);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (GameOf(sender) is not { } game) return;
        var data = new DataPackage();
        data.SetText(game.InstallPath);
        Clipboard.SetContent(data);
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (GameOf(sender) is not { } game) return;

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"Disinstallare {game.Name}?",
            Content = $"{game.UninstallDescription}\n\n{game.LastPlayedText}  ·  {game.SizeText}\nCartella: {game.InstallPath}",
            PrimaryButtonText = game.UninstallKind == UninstallKind.LauncherOnly ? $"Apri {game.Source}" : "Disinstalla",
            CloseButtonText = "Annulla",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            Busy.IsActive = true;
            var removed = await Uninstaller.RunAsync(game);
            if (removed)
            {
                ShowInfo($"{game.Name} è stato disinstallato", "", InfoBarSeverity.Success);
                await RefreshAsync();
            }
            else
            {
                ShowInfo($"Disinstallazione di {game.Name} avviata",
                    $"Completa i passaggi in {game.Source}, poi premi Aggiorna.", InfoBarSeverity.Informational);
            }
        }
        catch (Exception ex)
        {
            ShowInfo($"Non riesco a disinstallare {game.Name}", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            Busy.IsActive = false;
        }
    }

    private void ShowInfo(string title, string message, InfoBarSeverity severity)
    {
        Info.Title = title;
        Info.Message = message;
        Info.Severity = severity;
        Info.IsOpen = true;
    }
}
