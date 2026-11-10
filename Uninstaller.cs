using System.Diagnostics;
using Windows.Management.Deployment;

namespace Ludoteca;

public static class Uninstaller
{
    /// <returns>true se il gioco è già stato rimosso; false se è partito un disinstallatore esterno.</returns>
    public static async Task<bool> RunAsync(GameEntry game)
    {
        switch (game.UninstallKind)
        {
            case UninstallKind.Package:
                var result = await new PackageManager().RemovePackageAsync(game.UninstallTarget);
                if (result.ExtendedErrorCode is not null)
                    throw new InvalidOperationException(result.ErrorText, result.ExtendedErrorCode);
                return true;

            case UninstallKind.Command:
                var (exe, args) = SplitCommandLine(game.UninstallTarget);
                Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
                return false;

            default: // Uri e LauncherOnly
                Process.Start(new ProcessStartInfo(game.UninstallTarget) { UseShellExecute = true });
                return false;
        }
    }

    public static void OpenFolder(GameEntry game) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{game.InstallPath}\"") { UseShellExecute = true });

    /// <summary>Separa l'eseguibile dagli argomenti in una UninstallString del registro.</summary>
    internal static (string Exe, string Args) SplitCommandLine(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end > 0) return (command[1..end], command[(end + 1)..].Trim());
        }

        var exeEnd = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeEnd > 0)
        {
            exeEnd += 4;
            return (command[..exeEnd], command[exeEnd..].Trim());
        }

        var space = command.IndexOf(' ');
        return space < 0 ? (command, "") : (command[..space], command[(space + 1)..]);
    }
}
