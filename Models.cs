using System.ComponentModel;

namespace Ludoteca;

public enum UninstallKind
{
    /// <summary>Apre un URI del launcher (steam://, uplay://, ...).</summary>
    Uri,
    /// <summary>Esegue la riga di comando di disinstallazione dal registro.</summary>
    Command,
    /// <summary>Rimuove un pacchetto Store/Xbox con PackageManager.</summary>
    Package,
    /// <summary>Non si può disinstallare da qui: si apre solo il launcher.</summary>
    LauncherOnly,
}

public sealed class GameEntry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private long? _sizeBytes;

    /// <summary>Dimensione della cartella; null finché il calcolo in background non finisce.</summary>
    public long? SizeBytes
    {
        get => _sizeBytes;
        set
        {
            if (_sizeBytes == value) return;
            _sizeBytes = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeBytes)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FreeableText)));
        }
    }

    public string SizeText => SizeBytes is { } bytes ? Format.Size(bytes) : "Calcolo…";

    public required string Name { get; init; }
    public required string Source { get; init; }
    public required string InstallPath { get; init; }
    public required UninstallKind UninstallKind { get; init; }
    /// <summary>URI, riga di comando, nome completo del pacchetto o URI del launcher.</summary>
    public required string UninstallTarget { get; init; }

    /// <summary>Id del gioco nel launcher, quando non si ricava da UninstallTarget (es. AppName di Epic).</summary>
    public string? LaunchId { get; init; }

    public string Drive => PathUtil.DriveOf(InstallPath);

    public string Subtitle => $"{Source}  ·  {LastPlayedText}  ·  {InstallPath}";

    /// <summary>Id Steam ricavato dall'URI di disinstallazione (steam://uninstall/123).</summary>
    public string? SteamAppId => UninstallTarget.StartsWith("steam://uninstall/", StringComparison.Ordinal)
        ? UninstallTarget["steam://uninstall/".Length..]
        : null;

    // ------------------------------------------------------------ ultima partita

    public DateTime? LastPlayed { get; set; }
    /// <summary>true se la data è dedotta da salvataggi o log, non dal launcher.</summary>
    public bool LastPlayedIsEstimate { get; set; }
    /// <summary>true solo quando il launcher (Steam) dice con certezza che non è mai stato avviato.</summary>
    public bool NeverPlayed { get; set; }
    public int? PlaytimeMinutes { get; set; }

    public int? DaysSinceLastPlayed => LastPlayed is { } d ? Math.Max(0, (DateTime.Today - d.Date).Days) : null;

    public string LastPlayedText => NeverPlayed
        ? "Mai avviato"
        : DaysSinceLastPlayed is { } days
            ? $"Ultima partita {(LastPlayedIsEstimate ? "~" : "")}{Format.Ago(days)}"
            : "Ultima partita sconosciuta";

    public string LastPlayedTooltip => LastPlayed is { } d
        ? d.ToString("dddd d MMMM yyyy, HH:mm", System.Globalization.CultureInfo.GetCultureInfo("it-IT")) +
          (LastPlayedIsEstimate ? " (stima da salvataggi o log)" : "") +
          (PlaytimeMinutes is { } m and > 0 ? $"\nTempo di gioco: {Format.Playtime(m)}" : "")
        : LastPlayedText;

    // ------------------------------------------------------------ copertina

    private Microsoft.UI.Xaml.Media.ImageSource? _cover;

    /// <summary>Copertina da IGDB; null finché non è scaricata (o se non c'è).</summary>
    public Microsoft.UI.Xaml.Media.ImageSource? Cover
    {
        get => _cover;
        set
        {
            if (_cover == value) return;
            _cover = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Cover)));
        }
    }

    private Microsoft.UI.Xaml.Media.ImageSource? _icon;

    /// <summary>Icona dell'eseguibile, usata solo quando non c'è una copertina.</summary>
    public Microsoft.UI.Xaml.Media.ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (_icon == value) return;
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    /// <summary>Nome accessibile dei pulsanti (lettori di schermo, UI Automation).</summary>
    public string UninstallLabel => $"Disinstalla {Name}";
    public string PlayLabel => $"Gioca a {Name}";

    /// <summary>Motivo mostrato nella griglia "Da disinstallare".</summary>
    public string RecommendReason => Recommender.Reason(this);
    public string FreeableText => Recommender.Freeable(this);

    /// <summary>Chiave stabile per la cache delle copertine.</summary>
    public string CoverKey => SteamAppId is { } id ? $"steam-{id}" : $"name-{PlayHistory.Normalize(Name)}";

    public string Glyph => Source switch
    {
        "Xbox" => "",
        "Steam" or "Epic" or "Ubisoft" or "Battle.net" or "Riot" or "GOG" or "EA" => "",
        _ => "",
    };

    public string UninstallDescription => UninstallKind switch
    {
        UninstallKind.Uri or UninstallKind.Command =>
            $"Verrà avviata la disinstallazione tramite {Source}. Potrebbe chiederti una conferma.",
        UninstallKind.Package =>
            "Il pacchetto Xbox verrà rimosso subito, insieme ai file del gioco.",
        _ => $"{Source} non permette di disinstallare da fuori: si aprirà il launcher e dovrai farlo da lì.",
    };
}

public sealed class DriveGroup : List<GameEntry>, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly string _space = "";

    public DriveGroup(string drive, IEnumerable<GameEntry> games) : base(games)
    {
        Drive = drive;
        try
        {
            var info = new DriveInfo(drive);
            var label = string.IsNullOrWhiteSpace(info.VolumeLabel) ? "Disco locale" : info.VolumeLabel;
            Header = $"{label} ({drive.TrimEnd('\\')})";
            _space = $"{Format.Size(info.AvailableFreeSpace)} liberi su {Format.Size(info.TotalSize)}";
        }
        catch
        {
            Header = drive;
        }

        // Il totale dei giochi si aggiorna man mano che arrivano le dimensioni.
        foreach (var game in this)
        {
            game.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(GameEntry.SizeBytes))
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Details)));
            };
        }
    }

    public string Drive { get; }
    public string Header { get; }

    public string Details
    {
        get
        {
            var parts = new List<string> { $"{Count} {(Count == 1 ? "gioco" : "giochi")}" };
            parts.Add(this.All(g => g.SizeBytes is not null)
                ? $"{Format.Size(this.Sum(g => g.SizeBytes!.Value))} occupati dai giochi"
                : "calcolo dimensioni…");
            if (_space.Length > 0) parts.Add(_space);
            return string.Join("  ·  ", parts);
        }
    }
}

public static class Format
{
    /// <summary>Dimensione leggibile ("48.2 GB"), con il separatore decimale delle impostazioni di Windows.</summary>
    public static string Size(long bytes)
    {
        const double KB = 1024, MB = KB * 1024, GB = MB * 1024, TB = GB * 1024;
        return bytes switch
        {
            >= (long)TB => $"{bytes / TB:0.##} TB",
            >= (long)GB => $"{bytes / GB:0.#} GB",
            >= (long)MB => $"{bytes / MB:0} MB",
            _ => $"{bytes / KB:0} KB",
        };
    }

    /// <summary>"oggi", "ieri", "5 giorni fa", "3 settimane fa", "4 mesi fa", "2 anni fa".</summary>
    public static string Ago(int days) => days switch
    {
        0 => "oggi",
        1 => "ieri",
        _ => $"{Span(days)} fa",
    };

    /// <summary>Durata leggibile, per frasi come "non ci giochi da 4 mesi".</summary>
    public static string Span(int days)
    {
        if (days < 7) return days == 1 ? "1 giorno" : $"{days} giorni";
        if (days < 30) return days / 7 == 1 ? "1 settimana" : $"{days / 7} settimane";
        var months = (int)Math.Round(days / 30.44);
        if (months < 24) return months <= 1 ? "1 mese" : $"{months} mesi";
        return $"{days / 365} anni";
    }

    public static string Playtime(int minutes) => minutes < 60 ? $"{minutes} min" : $"{minutes / 60} ore";

    /// <summary>Somma le dimensioni dei file di una cartella (senza seguire giunzioni, per non contare due volte).</summary>
    public static long FolderSize(string dir, CancellationToken token = default)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        long total = 0;
        try
        {
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", options))
            {
                token.ThrowIfCancellationRequested();
                total += file.Length;
            }
        }
        catch (DirectoryNotFoundException)
        {
            // cartella sparita nel frattempo (es. gioco appena disinstallato)
        }
        return total;
    }
}

public static class PathUtil
{
    /// <summary>Normalizza il percorso e segue giunzioni/symlink fino alla cartella reale.</summary>
    public static string Resolve(string path)
    {
        try
        {
            var full = Path.GetFullPath(path).TrimEnd('\\', '/');
            if (full.Length == 2 && full[1] == ':') full += "\\";
            var dir = new DirectoryInfo(full);
            if (dir.Exists && dir.LinkTarget is not null)
            {
                var target = dir.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null) return target.FullName.TrimEnd('\\');
            }
            return full;
        }
        catch
        {
            return path;
        }
    }

    public static string DriveOf(string path)
    {
        var root = Path.GetPathRoot(path);
        return string.IsNullOrEmpty(root) ? "?" : root.ToUpperInvariant();
    }

    public static string Key(string path) => Resolve(path).ToLowerInvariant();
}
