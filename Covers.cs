using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Ludoteca;

public sealed class SteamGridDbAuthException(string message) : Exception(message);

/// <summary>
/// Trova la copertina verticale (600x900) di ogni gioco, provando nell'ordine:
/// 1. la cache locale del client Steam (nessuna rete, nessuna chiave);
/// 2. l'indirizzo pubblico di Steam per le copertine;
/// 3. SteamGridDB, se è impostata una chiave API (per Xbox, Battle.net, Riot, Ubisoft...).
/// I file scaricati restano in %LOCALAPPDATA%\Ludoteca\covers.
/// </summary>
public static class Covers
{
    private static readonly HttpClient Http = CreateHttp();
    private static readonly SemaphoreSlim Lock = new(1, 1);

    private static string Dir => Path.Combine(AppSettings.DataDir, "covers");
    private static string IndexPath => Path.Combine(Dir, "sources.json");

    // Valori speciali dell'indice: nessuna copertina trovata, con o senza SteamGridDB.
    // (Il suffisso -v2 fa ricercare i giochi segnati "non trovati" prima che esistessero Store e ricerca Steam.)
    private const string NotFound = "none-v2";
    private const string NotFoundWithoutKey = "none-nokey-v2";

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Ludoteca/1.0");
        return http;
    }

    /// <summary>
    /// Chiama <paramref name="onCover"/> con il file della copertina di ogni gioco che ne ha una
    /// (isIcon = true se è solo l'icona dell'eseguibile, quadrata).
    /// </summary>
    /// <returns>I giochi rimasti senza una copertina vera.</returns>
    public static async Task<List<GameEntry>> LoadAsync(IReadOnlyList<GameEntry> games, AppSettings settings,
        Action<GameEntry, string, bool> onCover, CancellationToken ct)
    {
        await Lock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Dir);
            var index = ReadIndex();
            var hasKey = settings.SteamGridDbKey.Length > 0;
            var sgdb = hasKey ? new SteamGridDb(settings.SteamGridDbKey) : null;
            var missing = new List<GameEntry>();

            try
            {
                foreach (var game in games)
                {
                    ct.ThrowIfCancellationRequested();
                    var file = await FindCoverAsync(game, index, sgdb, ct);
                    if (file is not null)
                    {
                        onCover(game, file, false);
                        continue;
                    }

                    missing.Add(game);
                    // Ultima risorsa: l'icona dell'eseguibile, meglio del controller generico.
                    if (await ExeIconAsync(game, ct) is { } icon) onCover(game, icon, true);
                }
            }
            finally
            {
                WriteIndex(index);
            }
            return missing;
        }
        finally
        {
            Lock.Release();
        }
    }

    private static async Task<string?> FindCoverAsync(GameEntry game, Dictionary<string, string> index,
        SteamGridDb? sgdb, CancellationToken ct)
    {
        // 1) Cache locale di Steam: sempre aggiornata, si legge ogni volta.
        if (game.SteamAppId is { } appId && SteamLibraryCache(appId) is { } local) return local;

        // Già cercato in passato?
        if (index.TryGetValue(game.CoverKey, out var known))
        {
            if (known == NotFound || (known == NotFoundWithoutKey && sgdb is null)) return null;
            if (known.StartsWith("http", StringComparison.Ordinal) && File.Exists(FileFor(known))) return FileFor(known);
        }

        // 2) Fonti in rete senza chiave, poi SteamGridDB.
        string? url = null;
        try
        {
            if (game.SteamAppId is { } id) url = await SteamCdnAsync(id, ct);
            if (url is null && game.Source == "Xbox") url = await MicrosoftStorePosterAsync(game, ct);
            if (url is null && game.SteamAppId is null) url = await SteamStoreSearchAsync(game, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException { CancellationToken.IsCancellationRequested: false } or JsonException)
        {
            // fonte non raggiungibile: si prova la prossima
        }
        if (url is null && sgdb is not null) url = await sgdb.FindCoverAsync(game, ct);

        if (url is null)
        {
            index[game.CoverKey] = sgdb is not null ? NotFound : NotFoundWithoutKey;
            return null;
        }

        var file = FileFor(url);
        if (!File.Exists(file)) await DownloadAsync(url, file, ct);
        index[game.CoverKey] = url;
        return file;
    }

    // ------------------------------------------------------------ Microsoft Store (giochi Xbox)

    /// <summary>Poster verticale dal catalogo pubblico del Microsoft Store, tramite lo StoreId del gioco.</summary>
    private static async Task<string?> MicrosoftStorePosterAsync(GameEntry game, CancellationToken ct)
    {
        var storeId = XboxStoreId(game.InstallPath);
        if (storeId is null) return null;

        var region = System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName;
        var language = System.Globalization.CultureInfo.CurrentUICulture.Name;
        var json = await Http.GetStringAsync(
            $"https://displaycatalog.mp.microsoft.com/v7.0/products?bigIds={storeId}&market={region}&languages={language},en-us", ct);

        using var doc = JsonDocument.Parse(json);
        var images = doc.RootElement.GetProperty("Products").EnumerateArray()
            .SelectMany(p => p.GetProperty("LocalizedProperties").EnumerateArray())
            .SelectMany(l => l.GetProperty("Images").EnumerateArray())
            .Select(i => (Purpose: i.GetProperty("ImagePurpose").GetString(), Uri: i.GetProperty("Uri").GetString()))
            .ToList();

        // Poster è 2:3 come le copertine Steam; BoxArt (quadrato) come ripiego.
        var uri = images.FirstOrDefault(i => i.Purpose == "Poster").Uri ?? images.FirstOrDefault(i => i.Purpose == "BoxArt").Uri;
        if (string.IsNullOrEmpty(uri)) return null;
        if (uri.StartsWith("//", StringComparison.Ordinal)) uri = "https:" + uri;
        return uri + "?w=600&h=900&format=jpg"; // il server ridimensiona: ~150 KB invece di ~650
    }

    private static string? XboxStoreId(string installPath)
    {
        foreach (var config in new[]
                 {
                     Path.Combine(installPath, "Content", "MicrosoftGame.Config"),
                     Path.Combine(installPath, "MicrosoftGame.Config"),
                 })
        {
            try
            {
                if (!File.Exists(config)) continue;
                var id = System.Xml.Linq.XDocument.Load(config).Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "StoreId")?.Value.Trim();
                if (!string.IsNullOrEmpty(id)) return id;
            }
            catch
            {
                // config illeggibile
            }
        }
        return null;
    }

    // ------------------------------------------------------------ negozio Steam (giochi venduti anche lì)

    /// <summary>
    /// Molti giochi di altri launcher sono anche su Steam (es. Diablo IV): si cerca nel negozio
    /// e si accetta solo un risultato con lo stesso nome, per non prendere la copertina di un altro gioco.
    /// </summary>
    private static async Task<string?> SteamStoreSearchAsync(GameEntry game, CancellationToken ct)
    {
        var json = await Http.GetStringAsync(
            $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(game.Name)}&cc=us&l=english", ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items)) return null;

        var wanted = PlayHistory.Normalize(game.Name);
        foreach (var item in items.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (PlayHistory.Normalize(name) != wanted) continue;
            return await SteamCdnAsync(item.GetProperty("id").GetInt64().ToString(), ct);
        }
        return null;
    }

    // ------------------------------------------------------------ icona dell'eseguibile

    private static async Task<string?> ExeIconAsync(GameEntry game, CancellationToken ct)
    {
        var exe = MainExecutable(game.InstallPath);
        if (exe is null) return null;

        var file = Path.Combine(Dir, $"icon-{PlayHistory.Normalize(game.Name)}.png");
        if (File.Exists(file) && File.GetLastWriteTimeUtc(file) >= File.GetLastWriteTimeUtc(exe)) return file;

        try
        {
            var storageFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(exe);
            using var thumb = await storageFile.GetThumbnailAsync(
                Windows.Storage.FileProperties.ThumbnailMode.SingleItem, 256,
                Windows.Storage.FileProperties.ThumbnailOptions.ResizeThumbnail);
            if (thumb is null || thumb.Size == 0) return null;

            await using var output = File.Create(file);
            await thumb.AsStreamForRead().CopyToAsync(output, ct);
            return file;
        }
        catch
        {
            return null;
        }
    }

    private static readonly string[] HelperExeWords =
        ["unins", "crash", "setup", "install", "update", "redist", "vc_", "dxsetup", "helper", "report", "launcher", "bootstrap", "ux"];

    /// <summary>L'exe più grande nella cartella (fino a 2 livelli), escludendo installer e strumenti.</summary>
    internal static string? MainExecutable(string dir)
    {
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true };
            return new DirectoryInfo(dir).EnumerateFiles("*.exe", options)
                .Where(f => !HelperExeWords.Any(w => f.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(f => f.Length)
                .FirstOrDefault()?.FullName;
        }
        catch
        {
            return null;
        }
    }

    public static void Clear()
    {
        if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
    }

    // ------------------------------------------------------------ Steam

    /// <summary>
    /// Il client Steam salva le immagini della libreria in appcache\librarycache\&lt;appid&gt;.
    /// La copertina verticale si chiama library_600x900 (giochi più vecchi) o library_capsule
    /// (più recenti), a volte in una sottocartella e con il suffisso della lingua.
    /// </summary>
    private static string? SteamLibraryCache(string appId)
    {
        var steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (string.IsNullOrEmpty(steamPath)) return null;
        var dir = Path.Combine(Path.GetFullPath(steamPath), "appcache", "librarycache", appId);
        if (!Directory.Exists(dir)) return null;

        return Directory.EnumerateFiles(dir, "library_*.jpg", SearchOption.AllDirectories)
            .Where(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                return name.StartsWith("library_600x900", StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith("library_capsule", StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>Lo sfondo largo della pagina del gioco (library_hero), dalla cache del client Steam.</summary>
    public static string? SteamHero(string appId)
    {
        try
        {
            var steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            if (string.IsNullOrEmpty(steamPath)) return null;
            var dir = Path.Combine(Path.GetFullPath(steamPath), "appcache", "librarycache", appId);
            if (!Directory.Exists(dir)) return null;
            return Directory.EnumerateFiles(dir, "library_hero*.jpg", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).Contains("blur", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> SteamCdnAsync(string appId, CancellationToken ct)
    {
        var url = $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg";
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        try
        {
            using var response = await Http.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? url : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------ file e indice

    private static string FileFor(string url)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..20].ToLowerInvariant();
        // Gli indirizzi del Microsoft Store non hanno estensione (e contengono punti): si usa .jpg.
        var ext = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
        return Path.Combine(Dir, hash + (ext is ".jpg" or ".jpeg" or ".png" or ".webp" ? ext : ".jpg"));
    }

    internal static async Task DownloadAsync(string url, string file, CancellationToken ct)
    {
        var bytes = await Http.GetByteArrayAsync(url, ct);
        var tmp = file + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes, ct);
        File.Move(tmp, file, overwrite: true);
    }

    private static Dictionary<string, string> ReadIndex()
    {
        try
        {
            return File.Exists(IndexPath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(IndexPath)) ?? []
                : [];
        }
        catch
        {
            return [];
        }
    }

    private static void WriteIndex(Dictionary<string, string> index) =>
        File.WriteAllText(IndexPath, JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));

    internal static HttpClient Client => Http;
}

/// <summary>Client minimo per l'API v2 di SteamGridDB (https://www.steamgriddb.com/api/v2).</summary>
public sealed class SteamGridDb(string apiKey)
{
    private const string Api = "https://www.steamgriddb.com/api/v2";

    private async Task<JsonElement?> GetAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Api + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await Covers.Client.SendAsync(request, ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new SteamGridDbAuthException("La chiave API di SteamGridDB non è valida.");
        if (!response.IsSuccessStatusCode) return null; // 404: gioco non presente

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("data", out var data) ? data.Clone() : null;
    }

    /// <summary>Verifica la chiave con una ricerca qualsiasi.</summary>
    public async Task TestAsync(CancellationToken ct = default) => await GetAsync("/search/autocomplete/doom", ct);

    public async Task<string?> FindCoverAsync(GameEntry game, CancellationToken ct)
    {
        if (game.SteamAppId is { } appId && await GridAsync($"/grids/steam/{appId}", ct) is { } steamGrid)
            return steamGrid;

        // Ricerca per nome: si preferisce il titolo identico, altrimenti il primo risultato.
        var name = new string(game.Name.Where(c => c is not ('®' or '™' or '©')).ToArray()).Trim();
        var results = await GetAsync($"/search/autocomplete/{Uri.EscapeDataString(name)}", ct);
        if (results is not { ValueKind: JsonValueKind.Array } list) return null;

        var wanted = PlayHistory.Normalize(name);
        var candidates = list.EnumerateArray()
            .Select(r => (Id: r.GetProperty("id").GetInt64(), Name: r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""))
            .ToList();
        var ordered = candidates.Where(c => PlayHistory.Normalize(c.Name) == wanted)
            .Concat(candidates.Take(1))
            .Select(c => c.Id)
            .Distinct();

        foreach (var id in ordered)
        {
            if (await GridAsync($"/grids/game/{id}", ct) is { } url) return url;
        }
        return null;
    }

    /// <summary>Prima copertina statica 600x900 (la più votata), o null.</summary>
    private async Task<string?> GridAsync(string path, CancellationToken ct)
    {
        var grids = await GetAsync($"{path}?dimensions=600x900&types=static&nsfw=false&humor=false", ct);
        if (grids is not { ValueKind: JsonValueKind.Array } list) return null;
        return list.EnumerateArray()
            .Select(g => g.TryGetProperty("url", out var u) ? u.GetString() : null)
            .FirstOrDefault(u => !string.IsNullOrEmpty(u));
    }
}
