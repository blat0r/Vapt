using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Vapt;

/// <summary>
/// Main próprio (DISABLE_XAML_GENERATED_MAIN): o instalador (Velopack) chama o exe com
/// argumentos dele na instalação/desinstalação, e isso precisa ser tratado antes do WinUI subir.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Velopack.VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => Installation.OnUninstall())
            .Run();

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(callback =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}

/// <summary>Limpeza ao desinstalar. Os dados (%LOCALAPPDATA%\Vapt) ficam: têm o backup das otimizações.</summary>
internal static class Installation
{
    public static void OnUninstall()
    {
        try
        {
            Core.Log.Initialize("vapt");
            Core.Log.Info("Vapt desinstalado. backup.json e reg-backups ficam em %LOCALAPPDATA%\\Vapt para desfazer depois, se precisar.");
        }
        catch (Exception)
        {
            // Desinstalar nunca pode falhar por causa disso.
        }
    }
}
