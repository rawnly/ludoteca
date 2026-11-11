using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Windows.Management.Deployment;

namespace Ludoteca;

/// <summary>
/// Avvia i giochi passando dal loro launcher, così funzionano login, DRM, overlay e aggiornamenti.
/// Quando il launcher non offre un modo per avviare un gioco da fuori, si avvia l'eseguibile del gioco.
/// </summary>
public static partial class Launcher
{
    /// <summary>
    /// Battle.net non salva da nessuna parte il codice per avviare un gioco ("Fen" per Diablo IV):
    /// questa tabella va dall'uid (quello di "--uid=" nel disinstallatore) al codice di avvio.
    /// </summary>
    private static readonly Dictionary<string, string> BattleNetCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fenris"] = "Fen",          // Diablo IV
        ["diablo3"] = "D3",          // Diablo III
        ["osi"] = "OSI",             // Diablo II: Resurrected
        ["anbs"] = "ANBS",           // Diablo Immortal
        ["prometheus"] = "Pro",      // Overwatch 2
        ["wow"] = "WoW",             // World of Warcraft
        ["wow_classic"] = "WoWC",    // WoW Classic
        ["hs_beta"] = "WTCG",        // Hearthstone
        ["heroes"] = "Hero",         // Heroes of the Storm
        ["s2"] = "S2",               // StarCraft II
        ["s1"] = "S1",               // StarCraft Remastered
        ["w3"] = "W3",               // Warcraft III: Reforged
        ["odin"] = "ODIN",           // Call of Duty (Modern Warfare / Warzone)
        ["viper"] = "VIPR",          // Call of Duty: Black Ops 4
        ["zeus"] = "ZEUS",           // Call of Duty: Black Ops Cold War
        ["fore"] = "FORE",           // Call of Duty: Vanguard
        ["rtro"] = "RTRO",           // Blizzard Arcade Collection
        ["wlby"] = "WLBY",           // Crash Bandicoot 4
    };

    /// <summary>Avvia il gioco e lo segna come appena giocato.</summary>
    public static async Task PlayAsync(GameEntry game)
    {
        var start = await Task.Run(() => StartInfoFor(game))
            ?? throw new InvalidOperationException($"Non so come avviare {game.Name}: non trovo né il launcher né l'eseguibile.");

        Process.Start(start);
        PlayLog.Record(game);
        game.LastPlayed = DateTime.Now;
        game.LastPlayedIsEstimate = false;
        game.NeverPlayed = false;
    }

    /// <summary>Il comando che verrebbe eseguito, per l'export e il debug.</summary>
    public static string Describe(GameEntry game) =>
        StartInfoFor(game) is { } s ? $"{s.FileName} {s.Arguments}".Trim() : "(non avviabile)";

    private static ProcessStartInfo? StartInfoFor(GameEntry game)
    {
        switch (game.Source)
        {
            case "Steam" when game.SteamAppId is { } appId:
                return Shell($"steam://rungameid/{appId}");

            case "Ubisoft" when Regex.Match(game.UninstallTarget, @"uplay://uninstall/(\d+)") is { Success: true } m:
                return Shell($"uplay://launch/{m.Groups[1].Value}/0");

            case "Epic" when game.LaunchId is { Length: > 0 } app:
                return Shell($"com.epicgames.launcher://apps/{Uri.EscapeDataString(app)}?action=launch&silent=true");

            case "Riot" when RiotLaunch(game) is { } riot:
                return riot;

            case "Battle.net" when BattleNetLaunch(game) is { } bnet:
                return bnet;

            case "Xbox" when game.UninstallKind == UninstallKind.Package && XboxAppId(game.UninstallTarget) is { } aumid:
                return new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{aumid}") { UseShellExecute = true };
        }

        // Ripiego: l'eseguibile principale, avviato dalla sua cartella.
        if (Covers.MainExecutable(game.InstallPath) is { } exe)
        {
            return new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
        }
        return null;
    }

    private static ProcessStartInfo Shell(string uri) => new(uri) { UseShellExecute = true };

    /// <summary>Dal disinstallatore "RiotClientServices.exe --uninstall-product=X --uninstall-patchline=Y".</summary>
    private static ProcessStartInfo? RiotLaunch(GameEntry game)
    {
        var (exe, args) = Uninstaller.SplitCommandLine(game.UninstallTarget);
        var product = Regex.Match(args, @"--uninstall-product=(\S+)").Groups[1].Value;
        var patchline = Regex.Match(args, @"--uninstall-patchline=(\S+)").Groups[1].Value;
        if (product.Length == 0 || !File.Exists(exe)) return null;
        return new ProcessStartInfo(exe, $"--launch-product={product} --launch-patchline={(patchline.Length > 0 ? patchline : "live")}")
        {
            UseShellExecute = true,
        };
    }

    private static ProcessStartInfo? BattleNetLaunch(GameEntry game)
    {
        var uid = Regex.Match(game.UninstallTarget, @"--uid=(\S+)").Groups[1].Value;
        if (!BattleNetCodes.TryGetValue(uid, out var code)) return null;

        var dir = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Battle.net",
            "InstallLocation", null) as string ?? @"C:\Program Files (x86)\Battle.net";
        var exe = Path.Combine(dir, "Battle.net.exe");
        return File.Exists(exe)
            ? new ProcessStartInfo(exe, $"--exec=\"launch {code}\"") { UseShellExecute = true }
            : null;
    }

    /// <summary>AppUserModelId del pacchetto (es. 38985CA0.COREBase_5bkah9njm3e9g!codShip).</summary>
    private static string? XboxAppId(string packageFullName)
    {
        try
        {
            var package = new PackageManager().FindPackageForUser(string.Empty, packageFullName);
            return package?.GetAppListEntries().FirstOrDefault()?.AppUserModelId;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Le partite avviate da Ludoteca, in %APPDATA%\Ludoteca\plays.json. Servono a sapere con certezza
/// l'ultima partita anche per i launcher che non la salvano (Xbox, Riot, Ubisoft...).
/// </summary>
public static class PlayLog
{
    private static string FilePath => Path.Combine(AppSettings.DataDir, "plays.json");
    private static readonly object Gate = new();

    public static Dictionary<string, DateTime> Load()
    {
        lock (Gate)
        {
            try
            {
                return File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(FilePath)) ?? []
                    : [];
            }
            catch
            {
                return [];
            }
        }
    }

    public static void Record(GameEntry game)
    {
        lock (Gate)
        {
            var plays = Load();
            plays[game.CoverKey] = DateTime.Now;
            try
            {
                Directory.CreateDirectory(AppSettings.DataDir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(plays, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // non poter salvare la data non deve impedire di giocare
            }
        }
    }
}
