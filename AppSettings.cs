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
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ludoteca");

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
