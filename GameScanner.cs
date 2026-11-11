using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using Windows.Management.Deployment;

namespace Ludoteca;

public sealed record ScanResult(List<GameEntry> Games, List<string> Warnings);

/// <summary>
/// Trova i giochi installati leggendo i dati dei launcher (Steam, Xbox, Epic, Ubisoft,
/// Battle.net, Riot, GOG, EA) e, per il resto, le voci di "Programmi installati" la cui
/// cartella contiene file tipici dei giochi.
/// </summary>
public static partial class GameScanner
{
    public static ScanResult Scan()
    {
        var warnings = new List<string>();
        var byPath = new Dictionary<string, GameEntry>();

        // L'ordine conta: a parità di cartella vince la prima fonte (la più precisa).
        Run("Steam", ScanSteam);
        Run("Xbox", ScanXbox);
        Run("Epic", ScanEpic);
        Run("Programmi installati", ScanRegistry);

        var games = byPath.Values
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        try { PlayHistory.Fill(games); }
        catch (Exception ex) { warnings.Add($"Ultima partita: {ex.Message}"); }

        return new ScanResult(games, warnings);

        void Run(string label, Func<IEnumerable<GameEntry>> source)
        {
            try
            {
                foreach (var game in source())
                {
                    byPath.TryAdd(PathUtil.Key(game.InstallPath), game);
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"{label}: {ex.Message}");
            }
        }
    }

    // ---------------------------------------------------------------- Steam

    private static readonly HashSet<string> SteamIgnoredAppIds = ["228980"]; // Steamworks Common Redistributables

    private static IEnumerable<GameEntry> ScanSteam()
    {
        var steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (string.IsNullOrEmpty(steamPath)) yield break;
        steamPath = Path.GetFullPath(steamPath);

        var libraries = new List<string> { steamPath };
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            foreach (Match m in VdfPathRegex().Matches(File.ReadAllText(vdf)))
            {
                libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }
        }

        foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var steamapps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamapps)) continue;

            foreach (var manifest in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
            {
                var fields = ParseAcf(File.ReadAllText(manifest));
                if (!fields.TryGetValue("appid", out var appId) ||
                    !fields.TryGetValue("name", out var name) ||
                    !fields.TryGetValue("installdir", out var installDir)) continue;
                if (SteamIgnoredAppIds.Contains(appId) || name.StartsWith("Proton", StringComparison.Ordinal) ||
                    name.StartsWith("Steam Linux Runtime", StringComparison.Ordinal)) continue;

                var path = Path.Combine(steamapps, "common", installDir);
                if (!Directory.Exists(path)) continue;

                yield return new GameEntry
                {
                    Name = name,
                    Source = "Steam",
                    InstallPath = PathUtil.Resolve(path),
                    UninstallKind = UninstallKind.Uri,
                    UninstallTarget = $"steam://uninstall/{appId}",
                };
            }
        }
    }

    private static Dictionary<string, string> ParseAcf(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in VdfPairRegex().Matches(text))
        {
            result.TryAdd(m.Groups[1].Value, m.Groups[2].Value.Replace(@"\\", @"\"));
        }
        return result;
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex VdfPathRegex();

    [GeneratedRegex("\"(\\w+)\"\\s+\"([^\"]*)\"")]
    private static partial Regex VdfPairRegex();

    // ---------------------------------------------------------------- Xbox / Game Pass

    private static IEnumerable<GameEntry> ScanXbox()
    {
        // 1) Cartelle dei giochi Xbox indicate dal file .GamingRoot di ogni disco
        //    (es. R:\xbox\Call of Duty). Le DLC hanno un config anche loro: si saltano.
        var folders = new Dictionary<string, (string Dir, string? Name)>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in GamingRoots())
        {
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root); } catch { continue; }

            foreach (var dir in dirs)
            {
                var config = Path.Combine(dir, "Content", "MicrosoftGame.Config");
                if (!File.Exists(config)) continue;

                var info = ReadGameConfig(config);
                if (info.Identity is null || info.IsDlc) continue;
                folders.TryAdd(info.Identity, (dir, info.DisplayName));
            }
        }

        // 2) Pacchetti dell'utente: un pacchetto è un gioco GDK se contiene MicrosoftGame.Config
        //    (oppure se è uno di quelli trovati al passo 1).
        var found = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in new PackageManager().FindPackagesForUser(string.Empty))
        {
            if (package.IsFramework || package.IsResourcePackage || package.IsBundle || package.IsOptional) continue;

            var id = package.Id.Name;
            folders.TryGetValue(id, out var folder);

            string? location = null;
            try { location = package.InstalledPath; } catch { /* pacchetto non leggibile */ }

            if (folder.Dir is null)
            {
                if (string.IsNullOrEmpty(location)) continue;
                bool isGame;
                try { isGame = File.Exists(Path.Combine(location, "MicrosoftGame.Config")); } catch { isGame = false; }
                if (!isGame) continue;
            }

            string displayName;
            try { displayName = package.DisplayName; } catch { displayName = folder.Name ?? id; }

            found[id] = new GameEntry
            {
                Name = displayName,
                Source = "Xbox",
                InstallPath = folder.Dir ?? XboxFolderFor(PathUtil.Resolve(location!)),
                UninstallKind = UninstallKind.Package,
                UninstallTarget = package.Id.FullName,
            };
        }

        // 3) Cartelle senza pacchetto registrato (es. installazione interrotta): si mostrano lo stesso.
        foreach (var (id, folder) in folders)
        {
            if (found.ContainsKey(id)) continue;
            found[id] = new GameEntry
            {
                Name = folder.Name ?? Path.GetFileName(folder.Dir),
                Source = "Xbox",
                InstallPath = folder.Dir,
                UninstallKind = UninstallKind.LauncherOnly,
                UninstallTarget = "xbox:",
            };
        }

        return found.Values;
    }

    /// <summary>R:\xbox\Call of Duty\Content → R:\xbox\Call of Duty (la cartella che l'utente vede).</summary>
    private static string XboxFolderFor(string contentPath) =>
        string.Equals(Path.GetFileName(contentPath), "Content", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(contentPath) ?? contentPath
            : contentPath;

    private static IEnumerable<string> GamingRoots()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            var file = Path.Combine(drive.RootDirectory.FullName, ".GamingRoot");
            byte[] bytes;
            try { bytes = File.ReadAllBytes(file); } catch { continue; }

            // Formato: "RGBX" + uint32 + percorsi UTF-16 relativi al disco, separati da \0.
            if (bytes.Length <= 8 || bytes[0] != 'R' || bytes[1] != 'G' || bytes[2] != 'B' || bytes[3] != 'X') continue;
            var text = System.Text.Encoding.Unicode.GetString(bytes, 8, bytes.Length - 8);
            foreach (var rel in text.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return Path.Combine(drive.RootDirectory.FullName, rel);
            }
        }
    }

    private static (string? Identity, string? DisplayName, bool IsDlc) ReadGameConfig(string path)
    {
        try
        {
            var doc = XDocument.Load(path);
            XElement? First(string name) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name);

            var identity = First("Identity")?.Attribute("Name")?.Value;
            var name = First("ShellVisuals")?.Attribute("DefaultDisplayName")?.Value;
            // Le DLC dipendono da un pacchetto principale (es. COD: BO6/MW3/MWII pack).
            var isDlc = First("MainPackageDependency") is not null || First("TargetDeviceFamilyForDLC") is not null;
            return (identity, name, isDlc);
        }
        catch
        {
            return (null, null, false);
        }
    }

    // ---------------------------------------------------------------- Epic

    private static IEnumerable<GameEntry> ScanEpic()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) yield break;

        foreach (var file in Directory.EnumerateFiles(dir, "*.item"))
        {
            JsonElement root;
            try { root = JsonDocument.Parse(File.ReadAllText(file)).RootElement; } catch { continue; }

            var name = root.TryGetProperty("DisplayName", out var n) ? n.GetString() : null;
            var location = root.TryGetProperty("InstallLocation", out var l) ? l.GetString() : null;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(location) || !Directory.Exists(location)) continue;

            // Le "app" di Epic che non sono giochi (es. Unreal Engine) hanno AppCategories senza "games".
            if (root.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array &&
                !cats.EnumerateArray().Any(c => c.GetString() == "games")) continue;

            yield return new GameEntry
            {
                Name = name,
                Source = "Epic",
                InstallPath = PathUtil.Resolve(location),
                UninstallKind = UninstallKind.LauncherOnly,
                UninstallTarget = "com.epicgames.launcher://store/library",
                LaunchId = root.TryGetProperty("AppName", out var app) ? app.GetString() : null,
            };
        }
    }

    // ---------------------------------------------------------------- Registro di Windows

    private static readonly string[] UninstallKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    private static readonly HashSet<string> LauncherNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Steam", "Battle.net", "Epic Games Launcher", "Ubisoft Connect", "Riot Client", "Riot Vanguard",
        "EA app", "GOG GALAXY", "Playnite", "Xbox", "Amazon Games",
    };

    private static IEnumerable<GameEntry> ScanRegistry()
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        foreach (var keyPath in UninstallKeys)
        {
            using var parent = hive.OpenSubKey(keyPath);
            if (parent is null) continue;

            foreach (var subName in parent.GetSubKeyNames())
            {
                using var key = parent.OpenSubKey(subName);
                if (key is null) continue;
                var entry = FromUninstallKey(subName, key);
                if (entry is not null) yield return entry;
            }
        }
    }

    private static GameEntry? FromUninstallKey(string keyName, RegistryKey key)
    {
        var name = (key.GetValue("DisplayName") as string)?.Trim();
        var uninstall = key.GetValue("UninstallString") as string;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(uninstall)) return null;
        if (key.GetValue("SystemComponent") is int sc && sc == 1) return null;
        if (key.GetValue("ParentKeyName") is not null) return null;
        if (LauncherNames.Contains(name)) return null;
        // Le voci "Steam App N" sono già coperte dai manifest di Steam.
        if (keyName.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase)) return null;

        var location = InstallLocationOf(key);
        if (location is null) return null;

        var publisher = key.GetValue("Publisher") as string ?? "";
        string? source = null;

        if (keyName.StartsWith("Uplay Install ", StringComparison.OrdinalIgnoreCase)) source = "Ubisoft";
        else if (keyName.StartsWith("Riot Game ", StringComparison.OrdinalIgnoreCase)) source = "Riot";
        else if (uninstall.Contains("Blizzard Uninstaller", StringComparison.OrdinalIgnoreCase)) source = "Battle.net";
        else if (publisher.Equals("GOG.com", StringComparison.OrdinalIgnoreCase)) source = "GOG";
        else if (LooksLikeGameFolder(location))
            source = publisher.Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase) ? "EA" : "Altro";

        if (source is null) return null;

        return new GameEntry
        {
            Name = name.Trim(),
            Source = source,
            InstallPath = location,
            UninstallKind = UninstallKind.Command,
            UninstallTarget = uninstall,
        };
    }

    private static string? InstallLocationOf(RegistryKey key)
    {
        var location = key.GetValue("InstallLocation") as string;
        if (string.IsNullOrWhiteSpace(location))
        {
            // Ripiego: la cartella dell'icona (spesso l'eseguibile del gioco).
            var icon = (key.GetValue("DisplayIcon") as string)?.Split(',')[0].Trim('"', ' ');
            if (string.IsNullOrEmpty(icon)) return null;
            try { location = Path.GetDirectoryName(icon); } catch { return null; }
        }

        location = location?.Trim('"', ' ');
        if (string.IsNullOrEmpty(location) || !Directory.Exists(location)) return null;
        return PathUtil.Resolve(location);
    }

    private static readonly HashSet<string> GameMarkerFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "UnityPlayer.dll", "GameAssembly.dll", "steam_api64.dll", "steam_api.dll",
        "EOSSDK-Win64-Shipping.dll", "bink2w64.dll", "binkw64.dll", "binkw32.dll",
        // amd_ags_x64.dll non c'è: lo includono anche editor e tool NVIDIA.
        "nvngx_dlss.dll", "MicrosoftGame.Config", "GameOverlayRenderer64.dll",
    };

    /// <summary>Cerca, fino a 2 livelli di profondità, file tipici di motori e SDK di gioco.</summary>
    private static bool LooksLikeGameFolder(string dir)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 2,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", options))
            {
                var name = Path.GetFileName(file);
                if (GameMarkerFiles.Contains(name) || name.EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // cartella non leggibile: non è un gioco per quanto ne sappiamo
        }
        return false;
    }
}
