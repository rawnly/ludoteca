using Velopack;
using Velopack.Sources;

namespace Ludoteca;

/// <summary>
/// Aggiornamenti automatici con Velopack, dalle GitHub Releases del progetto.
/// Funzionano solo nella versione installata con il Setup di Velopack: avviando l'app con
/// dotnet run o dalla cartella bin non c'è niente da aggiornare.
/// </summary>
public sealed class Updates
{
    public const string RepoUrl = "https://github.com/rawnly/ludoteca";

    private readonly UpdateManager _manager = new(CreateSource());
    private UpdateInfo? _available;

    /// <summary>
    /// Per provare gli aggiornamenti senza pubblicare: LUDOTECA_UPDATE_SOURCE=cartella con i file
    /// generati da vpk pack (releases.win.json, .nupkg).
    /// </summary>
    private static IUpdateSource CreateSource() =>
        Environment.GetEnvironmentVariable("LUDOTECA_UPDATE_SOURCE") is { Length: > 0 } local
            ? new SimpleFileSource(new DirectoryInfo(local))
            : new GithubSource(RepoUrl, null, false, null);

    public bool IsInstalled => _manager.IsInstalled;

    public string CurrentVersion => _manager.CurrentVersion?.ToString()
        ?? typeof(Updates).Assembly.GetName().Version?.ToString(3) ?? "?";

    public string? AvailableVersion => _available?.TargetFullRelease.Version.ToString();

    /// <returns>La nuova versione, o null se sei già aggiornato (o l'app non è installata).</returns>
    public async Task<string?> CheckAsync()
    {
        if (!_manager.IsInstalled) return null;
        _available = await _manager.CheckForUpdatesAsync();
        return AvailableVersion;
    }

    /// <summary>Scarica l'aggiornamento (solo le differenze, quando possibile), lo installa e riavvia l'app.</summary>
    public async Task DownloadAndRestartAsync(Action<int> progress)
    {
        if (_available is null) return;
        await _manager.DownloadUpdatesAsync(_available, progress);
        _manager.ApplyUpdatesAndRestart(_available.TargetFullRelease);
    }
}
