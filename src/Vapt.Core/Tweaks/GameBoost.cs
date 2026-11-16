using System.Diagnostics;

namespace Vapt.Core.Tweaks;

public sealed record BackgroundApp(string ProcessName, string Title, long MemoryBytes, int Count, bool HasWindow, bool Suggested);

/// <summary>
/// "Turbo para jogar": mostra o que está rodando em segundo plano e gastando memória, já
/// marcando o que dá para fechar sem perder nada (sincronização de nuvem, chat, atualizadores),
/// e fecha o que você escolher. Quem tem janela recebe o pedido normal de fechar e pode salvar;
/// só o que roda escondido é encerrado. Processos do Windows, do antivírus e dos drivers de
/// vídeo nunca aparecem.
/// </summary>
public static class GameBoost
{
    /// <summary>Nunca aparecem na lista.</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        // Núcleo do Windows
        "Idle", "System", "Secure System", "Registry", "MemCompression", "smss", "csrss", "wininit", "winlogon",
        "services", "lsass", "lsaiso", "svchost", "dwm", "fontdrvhost", "WUDFHost", "dasHost", "spoolsv", "WmiPrvSE", "audiodg",
        // Área de trabalho, Iniciar e pesquisa
        "explorer", "sihost", "taskhostw", "ctfmon", "RuntimeBroker", "ApplicationFrameHost", "StartMenuExperienceHost",
        "ShellExperienceHost", "SearchHost", "SearchApp", "SearchIndexer", "TextInputHost", "LockApp", "SystemSettings", "dllhost", "conhost",
        // Segurança
        "MsMpEng", "NisSrv", "SecurityHealthService", "SecurityHealthSystray", "SgrmBroker", "smartscreen",
        // Drivers de vídeo
        "nvcontainer", "NVDisplay.Container", "atiesrxx", "atieclxx", "amdfendrsr", "RadeonSoftware", "igfxEM",
        // Terminais, ferramentas de desenvolvimento e o próprio Vapt
        "Vapt", "Update", "cmd", "powershell", "pwsh", "WindowsTerminal", "OpenConsole", "devenv", "Code", "dotnet", "claude", "msedgewebview2",
    };

    /// <summary>Vêm marcados: fechar não perde nada e libera memória, disco ou rede durante o jogo.</summary>
    private static readonly Dictionary<string, string> Suggested = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OneDrive"] = "OneDrive · sincroniza arquivos e usa a internet",
        ["GoogleDriveFS"] = "Google Drive · sincronização",
        ["Dropbox"] = "Dropbox · sincronização",
        ["ms-teams"] = "Teams · chat do trabalho",
        ["Teams"] = "Teams clássico",
        ["Skype"] = "Skype",
        ["PhoneExperienceHost"] = "Vincular ao Celular",
        ["Widgets"] = "Widgets · notícias e clima",
        ["WidgetService"] = "Widgets · serviço",
        ["Copilot"] = "Copilot",
        ["msedge"] = "Microsoft Edge",
        ["AdobeARM"] = "Adobe · procura atualizações",
        ["AdobeIPCBroker"] = "Adobe · processo auxiliar",
        ["CCXProcess"] = "Adobe Creative Cloud",
        ["Creative Cloud"] = "Adobe Creative Cloud",
        ["OfficeClickToRun"] = "Office · procura atualizações",
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
