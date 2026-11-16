using System.Diagnostics;

namespace Vapt.Core.Tweaks;

public sealed record BackgroundApp(string ProcessName, string Title, long MemoryBytes, int Count, bool HasWindow, bool Suggested);

/// <summary>
/// "Turbo para jogar" (como o RESET OTIMIZA do ResetExplode): lista os programas em segundo
/// plano, já marcando os seguros (OneDrive, Teams, Widgets, Adobe…), e fecha os escolhidos.
/// Programas com janela recebem o pedido normal de fechar (salvam o que precisam); só os sem
/// janela são encerrados. Não mexe em sistema, antivírus nem drivers.
/// </summary>
public static class GameBoost
{
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "svchost",
        "dwm", "explorer", "fontdrvhost", "sihost", "ctfmon", "taskhostw", "RuntimeBroker", "StartMenuExperienceHost",
        "ShellExperienceHost", "SearchHost", "SearchApp", "SearchIndexer", "TextInputHost", "SecurityHealthService", "SecurityHealthSystray",
        "MsMpEng", "NisSrv", "SgrmBroker", "audiodg", "conhost", "dllhost", "spoolsv", "WmiPrvSE", "LockApp", "ApplicationFrameHost",
        "SystemSettings", "smartscreen", "MemCompression", "Secure System", "WUDFHost", "dasHost", "nvcontainer", "NVDisplay.Container",
        "atiesrxx", "atieclxx", "amdfendrsr", "RadeonSoftware", "igfxEM", "Vapt", "cmd", "powershell", "pwsh", "WindowsTerminal",
        "OpenConsole", "devenv", "Code", "dotnet", "claude", "Update", "msedgewebview2",
    };

    /// <summary>Seguros de fechar durante o jogo (vêm marcados).</summary>
    private static readonly Dictionary<string, string> Suggested = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OneDrive"] = "Sincronização do OneDrive (usa disco e rede)",
        ["ms-teams"] = "Microsoft Teams",
        ["Teams"] = "Microsoft Teams (clássico)",
        ["Skype"] = "Skype",
        ["PhoneExperienceHost"] = "Vincular ao Celular",
        ["Widgets"] = "Widgets do Windows",
        ["WidgetService"] = "Serviço de Widgets",
        ["AdobeARM"] = "Atualizador da Adobe",
        ["CCXProcess"] = "Adobe Creative Cloud",
        ["Creative Cloud"] = "Adobe Creative Cloud",
        ["AdobeIPCBroker"] = "Adobe (segundo plano)",
        ["GoogleDriveFS"] = "Google Drive",
        ["Dropbox"] = "Dropbox",
        ["OfficeClickToRun"] = "Atualizador do Office",
        ["Copilot"] = "Copilot",
        ["msedge"] = "Microsoft Edge",
    };

    public static List<BackgroundApp> List()
    {
        var session = Process.GetCurrentProcess().SessionId;
        var groups = new Dictionary<string, List<Process>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.SessionId != session || Protected.Contains(p.ProcessName))
                {
                    p.Dispose();
                    continue;
                }

                if (!groups.TryGetValue(p.ProcessName, out var list))
                {
                    groups[p.ProcessName] = list = [];
                }

                list.Add(p);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                p.Dispose();
            }
        }

        var apps = new List<BackgroundApp>();
        foreach (var (name, procs) in groups)
        {
            long memory = 0;
            var hasWindow = false;
            string? title = null;
            foreach (var p in procs)
            {
                try
                {
                    memory += p.PrivateMemorySize64;
                    if (p.MainWindowHandle != 0)
                    {
                        hasWindow = true;
                    }

                    title ??= p.MainModule?.FileVersionInfo.FileDescription;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                }
                finally
                {
                    p.Dispose();
                }
            }

            var suggested = Suggested.TryGetValue(name, out var description);
            // Só aparece o que pesa ou que é conhecido: a lista fica curta e útil.
            if (!suggested && memory < 80L * 1024 * 1024)
            {
                continue;
            }

            apps.Add(new BackgroundApp(name, description ?? (string.IsNullOrWhiteSpace(title) ? name : title!.Trim()), memory, procs.Count, hasWindow, suggested && !hasWindow));
        }

        return apps.OrderByDescending(a => a.Suggested).ThenByDescending(a => a.MemoryBytes).ToList();
    }

    /// <summary>Fecha os programas. Com janela: pede para fechar; sem janela: encerra.</summary>
    public static (List<string> Closed, List<string> NotClosed) Close(IEnumerable<BackgroundApp> apps)
    {
        var closed = new List<string>();
        var failed = new List<string>();
        foreach (var app in apps)
        {
            var ok = true;
            foreach (var p in Process.GetProcessesByName(app.ProcessName))
            {
                using (p)
                {
                    try
                    {
                        if (p.MainWindowHandle != 0)
                        {
                            p.CloseMainWindow();
                            if (!p.WaitForExit(4000))
                            {
                                ok = false; // provavelmente perguntou "salvar?": o usuário decide
                            }
                        }
                        else
                        {
                            p.Kill();
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                    {
                        ok = false;
                    }
                }
            }

            (ok ? closed : failed).Add(app.Title);
        }

        Log.Info($"Turbo para jogar: fechou {closed.Count}, ficaram {failed.Count}.");
        return (closed, failed);
    }
}
