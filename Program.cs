using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace Ludoteca;

public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Deve essere la prima cosa: durante installazione, aggiornamento e disinstallazione
        // Velopack avvia l'exe con argomenti speciali, li gestisce qui ed esce subito.
        VelopackApp.Build().Run();

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}
