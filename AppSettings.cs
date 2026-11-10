using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ludoteca;

/// <summary>
/// Impostazioni salvate in %LOCALAPPDATA%\Ludoteca\settings.json.
/// La chiave API di SteamGridDB è cifrata con DPAPI: si può leggere solo con il tuo account Windows.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// %APPDATA%\Ludoteca. Non %LOCALAPPDATA%\Ludoteca: quella è la cartella in cui Velopack installa l'app,
    /// e viene cancellata quando la disinstalli.
    /// </summary>
    public static string DataDir { get; } = MigrateDataDir();

    private static string MigrateDataDir()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ludoteca");
        // Fino alla 1.3 i dati stavano in %LOCALAPPDATA%\Ludoteca: si spostano una volta sola.
        var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ludoteca");
        try
        {
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                var oldSettings = Path.Combine(old, "settings.json");
                if (File.Exists(oldSettings)) File.Move(oldSettings, Path.Combine(dir, "settings.json"));
                var oldCovers = Path.Combine(old, "covers");
                if (Directory.Exists(oldCovers)) Directory.Move(oldCovers, Path.Combine(dir, "covers"));
            }
        }
        catch
        {
            // se lo spostamento non riesce si riparte da zero: le copertine si riscaricano
        }
        return dir;
    }

    private static string FilePath => Path.Combine(DataDir, "settings.json");

    public string SteamGridDbKey { get; set; } = "";

    private sealed record Stored(string? SteamGridDbKeyProtected);

    public static AppSettings Load()
    {
        var settings = new AppSettings();
        try
        {
            if (!File.Exists(FilePath)) return settings;
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath));
            if (!string.IsNullOrEmpty(stored?.SteamGridDbKeyProtected))
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored.SteamGridDbKeyProtected), null, DataProtectionScope.CurrentUser);
                settings.SteamGridDbKey = Encoding.UTF8.GetString(bytes);
            }
        }
        catch
        {
            // file rovinato o di un altro utente: si riparte da impostazioni vuote
        }
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        var key = SteamGridDbKey.Length == 0
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(SteamGridDbKey), null, DataProtectionScope.CurrentUser));
        File.WriteAllText(FilePath, JsonSerializer.Serialize(new Stored(key), new JsonSerializerOptions { WriteIndented = true }));
    }
}
