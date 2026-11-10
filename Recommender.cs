namespace Ludoteca;

/// <summary>
/// Sceglie i giochi da consigliare per la disinstallazione: quelli a cui non giochi da più tempo,
/// pesati per lo spazio che occupano (un gioco da 150 GB fermo da 6 mesi batte uno da 2 GB fermo da 8).
/// </summary>
public static class Recommender
{
    /// <summary>Sotto questa soglia un gioco è "in uso" e non viene mai consigliato.</summary>
    public const int MinDaysIdle = 30;

    /// <summary>Peso dato a un gioco mai avviato (Steam lo sa con certezza): come uno fermo da un anno.</summary>
    private const int NeverPlayedDays = 365;

    public static List<GameEntry> Pick(IEnumerable<GameEntry> games, int max) => games
        .Select(g => (Game: g, Score: Score(g)))
        .Where(x => x.Score > 0)
        .OrderByDescending(x => x.Score)
        .Take(max)
        .Select(x => x.Game)
        .ToList();

    private static double Score(GameEntry game)
    {
        int days;
        if (game.NeverPlayed) days = NeverPlayedDays;
        else if (game.DaysSinceLastPlayed is { } d && d >= MinDaysIdle) days = d;
        else return 0;

        // Finché la dimensione non è calcolata si ipotizzano 10 GB.
        var gb = (game.SizeBytes ?? 10L << 30) / (double)(1L << 30);
        return days * (1 + Math.Log2(1 + gb));
    }

    public static string Reason(GameEntry game) => game.NeverPlayed
        ? "Mai avviato"
        : game.DaysSinceLastPlayed is { } d
            ? $"Fermo da {(game.LastPlayedIsEstimate ? "~" : "")}{Format.Span(d)}"
            : "";

    public static string Freeable(GameEntry game) =>
        game.SizeBytes is { } bytes ? $"Liberi {Format.Size(bytes)}" : "Calcolo dimensione…";
}
