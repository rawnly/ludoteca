using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Ludoteca;

/// <summary>
/// Trova quando hai giocato l'ultima volta a ogni gioco.
/// Dati esatti: Steam (localconfig.vdf, con le ore giocate) e Battle.net (Battle.net.config).
/// Stime: ultima modifica di salvataggi o log (Ubisoft, Riot, Xbox e altri).
/// </summary>
public static partial class PlayHistory
{
    public static void Fill(IReadOnlyList<GameEntry> games)
    {
        var steam = LoadSteam();
        var battleNet = LoadBattleNet();

        foreach (var game in games)
        {
            try
            {
                switch (game.Source)
                {
                    case "Steam" when game.SteamAppId is { } appId:
                        if (steam.TryGetValue(appId, out var s))
                        {
                            game.LastPlayed = s.LastPlayed;
                            game.PlaytimeMinutes = s.Minutes;
                        }
                        // Steam lo sa con certezza: se non c'è una data, non l'hai mai avviato.
                        game.NeverPlayed = game.LastPlayed is null;
                        continue;

                    case "Battle.net":
                        var uid = Regex.Match(game.UninstallTarget, @"--uid=(\S+)").Groups[1].Value;
                        if (battleNet.TryGetValue(uid, out var b)) { game.LastPlayed = b; continue; }
                        break;

                    case "Ubisoft":
                        var id = Regex.Match(game.UninstallTarget, @"uplay://uninstall/(\d+)").Groups[1].Value;
                        if (UbisoftSaves(id) is { } dir) Estimate(game, NewestWrite(dir));
                        break;

                    case "Riot":
                        var logs = Path.Combine(game.InstallPath, "Logs", "GameLogs");
                        Estimate(game, NewestWrite(Directory.Exists(logs) ? logs : Path.Combine(game.InstallPath, "Logs")));
                        break;
                }

                // Ripiego per tutti: cartelle di salvataggi/configurazione con lo stesso nome del gioco.
                if (game.LastPlayed is null) Estimate(game, NewestWriteInUserFolders(game.Name));
            }
            catch
            {
                // un gioco senza dati non deve fermare gli altri
            }
        }

        // Le partite avviate da Ludoteca sono esatte: vincono su stime e date più vecchie.
        var ownPlays = PlayLog.Load();
        foreach (var game in games)
        {
            if (ownPlays.TryGetValue(game.CoverKey, out var when) && when > (game.LastPlayed ?? DateTime.MinValue))
            {
                game.LastPlayed = when;
                game.LastPlayedIsEstimate = false;
                game.NeverPlayed = false;
            }
        }
    }

    private static void Estimate(GameEntry game, DateTime? when)
    {
        if (when is null || game.LastPlayed is not null) return;
        game.LastPlayed = when;
        game.LastPlayedIsEstimate = true;
    }

    // ------------------------------------------------------------ Steam

    private static Dictionary<string, (DateTime? LastPlayed, int? Minutes)> LoadSteam()
    {
        var result = new Dictionary<string, (DateTime?, int?)>();
        var steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (string.IsNullOrEmpty(steamPath)) return result;

        var userdata = Path.Combine(Path.GetFullPath(steamPath), "userdata");
        if (!Directory.Exists(userdata)) return result;

        // Un file per ogni account Steam usato sul PC: si tiene la data più recente.
        foreach (var file in Directory.EnumerateFiles(userdata, "localconfig.vdf", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2 }))
        {
            string text;
            try { text = File.ReadAllText(file); } catch { continue; }

            foreach (Match block in SteamAppBlockRegex().Matches(text))
            {
                var body = block.Groups[2].Value;
                var last = SteamFieldRegex("LastPlayed").Match(body);
                if (!last.Success) continue;

                var appId = block.Groups[1].Value;
                DateTime? when = long.TryParse(last.Groups[1].Value, out var ts) && ts > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime : null;
                var playtime = SteamFieldRegex("Playtime").Match(body);
                int? minutes = playtime.Success && int.TryParse(playtime.Groups[1].Value, out var m) ? m : null;

                if (!result.TryGetValue(appId, out var prev) || (when ?? DateTime.MinValue) > (prev.Item1 ?? DateTime.MinValue))
                    result[appId] = (when, minutes ?? prev.Item2);
            }
        }
        return result;
    }

    // Prende l'inizio del blocco di ogni app, fino alla prima sotto-sezione: LastPlayed e Playtime stanno lì.
    [GeneratedRegex("\"(\\d+)\"\\s*\\{([^{}]*)")]
    private static partial Regex SteamAppBlockRegex();

    private static Regex SteamFieldRegex(string field) => new($"\"{field}\"\\s+\"(\\d+)\"", RegexOptions.IgnoreCase);

    // ------------------------------------------------------------ Battle.net

    private static Dictionary<string, DateTime> LoadBattleNet()
    {
        var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Battle.net", "Battle.net.config");
        if (!File.Exists(file)) return result;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("Games", out var games)) return result;
            foreach (var game in games.EnumerateObject())
            {
                if (game.Value.ValueKind == JsonValueKind.Object &&
                    game.Value.TryGetProperty("LastPlayed", out var lp) &&
                    long.TryParse(lp.GetString(), out var ts) && ts > 0)
                {
                    result[game.Name] = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;
                }
            }
        }
        catch
        {
            // file illeggibile o in un formato diverso
        }
        return result;
    }

    // ------------------------------------------------------------ Ubisoft

    private static string? UbisoftSaves(string gameId)
    {
        if (gameId.Length == 0) return null;
        var launcher = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Ubisoft\Launcher", "InstallDir", null) as string
                       ?? @"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\";
        var saves = Path.Combine(launcher, "savegames");
        if (!Directory.Exists(saves)) return null;

        // savegames\<utente>\<id gioco>
        return Directory.EnumerateDirectories(saves)
            .Select(user => Path.Combine(user, gameId))
            .Where(Directory.Exists)
            .OrderByDescending(d => NewestWrite(d) ?? DateTime.MinValue)
            .FirstOrDefault();
    }

    // ------------------------------------------------------------ stime da file

    private static readonly string[] UserFolders =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games"),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow"),
    ];

    private static DateTime? NewestWriteInUserFolders(string gameName)
    {
        var wanted = Normalize(gameName);
        if (wanted.Length < 4) return null;

        DateTime? newest = null;
        foreach (var root in UserFolders)
        {
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root); } catch { continue; }
            foreach (var dir in dirs)
            {
                if (Normalize(Path.GetFileName(dir)) != wanted) continue;
                var when = NewestWrite(dir);
                if (when > (newest ?? DateTime.MinValue)) newest = when;
            }
        }
        return newest;
    }

    /// <summary>"Call of Duty®" → "callofduty"</summary>
    public static string Normalize(string name) =>
        new(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static DateTime? NewestWrite(string? dir)
    {
        if (dir is null || !Directory.Exists(dir)) return null;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 4,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        DateTime? newest = null;
        var now = DateTime.Now.AddMinutes(5);
        var count = 0;
        foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", options))
        {
            var t = file.LastWriteTime;
            if (t <= now && t > (newest ?? DateTime.MinValue)) newest = t;
            if (++count > 5000) break; // basta un campione: sono cartelle di salvataggi, non di gioco
        }
        return newest;
    }
}
