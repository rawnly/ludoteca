using Microsoft.UI.Xaml;

namespace Ludoteca;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Ludoteca.exe --export giochi.csv  → scrive l'elenco ed esce senza aprire la finestra.
        var cli = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(cli, "--export");
        if (i >= 0 && i + 1 < cli.Length)
        {
            Export(cli[i + 1]);
            Exit();
            return;
        }

        _window = new MainWindow();
        _window.Activate();
    }

    private static void Export(string file)
    {
        var result = GameScanner.Scan();
        static string Csv(string s) => $"\"{s.Replace("\"", "\"\"")}\"";
        Parallel.ForEach(result.Games, new ParallelOptions { MaxDegreeOfParallelism = 4 },
            g => g.SizeBytes = Format.FolderSize(g.InstallPath));

        var lines = new List<string> { "Nome;Fonte;Disco;Dimensione;Byte;Ultima partita;Data;Stima;Minuti giocati;Cartella;Disinstallazione" };
        lines.AddRange(result.Games.Select(g =>
            string.Join(";", Csv(g.Name), g.Source, g.Drive, Csv(g.SizeText), g.SizeBytes,
                Csv(g.LastPlayedText), g.LastPlayed?.ToString("yyyy-MM-dd HH:mm"), g.LastPlayedIsEstimate ? "sì" : "",
                g.PlaytimeMinutes, Csv(g.InstallPath), Csv($"{g.UninstallKind}: {g.UninstallTarget}"))));
        lines.AddRange(result.Warnings.Select(w => "# avviso: " + w));
        File.WriteAllLines(file, lines, new System.Text.UTF8Encoding(true));
    }
}
