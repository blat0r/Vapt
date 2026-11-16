using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Vapt.Core;

namespace Vapt;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        Log.Initialize("vapt");
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Log.Error("Exceção não tratada na interface.", e.Exception);
            e.Handled = true;
        };
    }

    public static MainWindow? MainWindow => (Current as App)?._window;

    public static Settings Settings { get; } = Settings.Load();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var argv = Environment.GetCommandLineArgs();
        Log.Info($"Vapt iniciado (admin: {Elevation.IsAdministrator}) args: {string.Join(' ', argv.Skip(1))}");
        if (argv.Contains("--smoke"))
        {
            // Verificação sem janela visível: monta cada página e sai.
            _window = new MainWindow();
            var failures = _window.SmokeTest();
            Environment.Exit(failures);
            return;
        }

        if (FocusExistingInstance())
        {
            Exit();
            return;
        }

        // "Sempre abrir como administrador": se o usuário recusar o UAC, abre normal mesmo.
        if (Settings.AlwaysAdmin && !Elevation.IsAdministrator && !argv.Contains("--elevated"))
        {
            var page0 = argv.SkipWhile(a => a != "--page").Skip(1).FirstOrDefault() ?? "home";
            RestartAsAdmin(page0);
        }

        _window = new MainWindow();
        var page = argv.SkipWhile(a => a != "--page").Skip(1).FirstOrDefault();
        if (page is not null)
        {
            _window.NavigateTo(page);
        }

        _window.Activate();
        InstallPawnIoOnce();
    }

    /// <summary>
    /// Primeira vez que abre (instalação nova ou atualização): instala o driver PawnIO sozinho,
    /// para a temperatura e o consumo do processador aparecerem sem ninguém precisar clicar em nada.
    /// </summary>
    private static async void InstallPawnIoOnce()
    {
        if (Settings.PawnIoAutoTried || Core.Perf.SensorHub.PawnIoInstalled)
        {
            return;
        }

        Settings.PawnIoAutoTried = true;
        Settings.Save();
        await Task.Delay(TimeSpan.FromSeconds(2)); // deixa a janela aparecer antes do UAC
        var ok = await Core.Perf.SensorHub.InstallPawnIoAsync();
        Log.Info($"PawnIO automático: {(ok ? "instalado" : "não instalado")}");
    }

    /// <summary>
    /// Reabre o Vapt como administrador (UAC) na mesma página e fecha esta janela.
    /// Devolve falso se o usuário recusou.
    /// </summary>
    public static bool RestartAsAdmin(string page)
    {
        try
        {
            var exe = Environment.ProcessPath!;
            Process.Start(new ProcessStartInfo(exe, $"--page {page} --elevated")
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            Environment.Exit(0);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool FocusExistingInstance()
    {
        // Um Vapt elevado aberto a partir deste não deve ser trocado pelo antigo.
        if (Environment.GetCommandLineArgs().Contains("--elevated"))
        {
            return false;
        }

        using var current = Process.GetCurrentProcess();
        foreach (var other in Process.GetProcessesByName(current.ProcessName))
        {
            using (other)
            {
                if (other.Id == current.Id || other.MainWindowHandle == 0)
                {
                    continue;
                }

                if (IsIconic(other.MainWindowHandle))
                {
                    ShowWindow(other.MainWindowHandle, 9 /* SW_RESTORE */);
                }

                SetForegroundWindow(other.MainWindowHandle);
                return true;
            }
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hwnd);
}
