using Microsoft.Win32;

namespace Vapt.Core.Apps;

public sealed record EssentialApp(string Name, string Description, string WingetId, string DisplayPrefix, string Category, bool PreSelect = true);

/// <summary>
/// Apps para instalar depois de formatar, pelo winget (baixa do site oficial e confere o hash),
/// como o "Instalar apps" do ResetExplode e o Ninite.
/// </summary>
public static class EssentialApps
{
    public static IReadOnlyList<EssentialApp> Catalog { get; } =
    [
        new("Google Chrome", "Navegador do Google.", "Google.Chrome", "Google Chrome", "Navegadores"),
        new("Firefox", "Navegador da Mozilla, focado em privacidade.", "Mozilla.Firefox", "Mozilla Firefox", "Navegadores", false),
        new("Brave", "Navegador com bloqueador de anúncios embutido.", "Brave.Brave", "Brave", "Navegadores", false),
        new("Discord", "Chat de voz e texto para jogar com os amigos.", "Discord.Discord", "Discord", "Comunicação"),
        new("WhatsApp", "WhatsApp para computador.", "9NKSQGP7F2NH", "WhatsApp", "Comunicação", false),
        new("Steam", "Loja e biblioteca de jogos da Valve.", "Valve.Steam", "Steam", "Jogos"),
        new("Epic Games Launcher", "Loja da Epic (Fortnite e jogos grátis toda semana).", "EpicGames.EpicGamesLauncher", "Epic Games Launcher", "Jogos", false),
        new("7-Zip", "Compactador grátis: abre zip, rar e 7z.", "7zip.7zip", "7-Zip", "Utilitários"),
        new("WinRAR", "Compactador clássico (pago depois do teste). Escolha ele ou o 7-Zip.", "RARLab.WinRAR", "WinRAR", "Utilitários", false),
        new("Lightshot", "Print de uma parte da tela com a tecla Print Screen.", "Skillbrains.Lightshot", "Lightshot", "Utilitários", false),
        new("ShareX", "Prints e gravação de tela completos, grátis.", "ShareX.ShareX", "ShareX", "Utilitários", false),
        new("Notepad++", "Editor de texto leve com abas.", "Notepad++.Notepad++", "Notepad++", "Utilitários", false),
        new("PowerToys", "Ferramentas extras da Microsoft para o Windows.", "Microsoft.PowerToys", "PowerToys", "Utilitários", false),
        new("VLC", "Player que abre qualquer vídeo e música.", "VideoLAN.VLC", "VLC media player", "Mídia"),
        new("Spotify", "Música e podcasts.", "Spotify.Spotify", "Spotify", "Mídia", false),
        new("OBS Studio", "Gravar a tela e fazer live.", "OBSProject.OBSStudio", "OBS Studio", "Mídia", false),
        new("Visual Studio Code", "Editor de código da Microsoft.", "Microsoft.VisualStudioCode", "Microsoft Visual Studio Code", "Desenvolvimento", false),
        new("Git", "Controle de versão.", "Git.Git", "Git", "Desenvolvimento", false),
    ];

    /// <summary>Nomes da lista "Aplicativos instalados" (máquina 64/32 bits e conta).</summary>
    public static HashSet<string> InstalledNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (var sub in root?.GetSubKeyNames() ?? [])
                {
                    using var k = root!.OpenSubKey(sub);
                    if (k?.GetValue("DisplayName") is string n)
                    {
                        names.Add(n.Trim());
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
        }

        return names;
    }

    public static bool IsInstalled(EssentialApp app, HashSet<string> installed) =>
        installed.Any(n => n.StartsWith(app.DisplayPrefix, StringComparison.OrdinalIgnoreCase));

    public static async Task<bool> InstallAsync(EssentialApp app, CancellationToken ct = default)
    {
        if (WingetUpdates.WingetPath is not { } winget)
        {
            return false;
        }

        var source = app.WingetId.All(char.IsLetterOrDigit) && app.WingetId.Length == 12 ? "msstore" : "winget";
        var r = await ProcessRunner.RunAsync(winget,
            $"install --id \"{app.WingetId}\" --exact --source {source} --silent --accept-package-agreements --accept-source-agreements --disable-interactivity",
            cancellation: ct);
        // 0 = instalado; -1978335189 (0x8A15002B) = já estava instalado e atualizado.
        var ok = r.ExitCode is 0 or -1978335189;
        Log.Info($"winget install {app.WingetId}: código {r.ExitCode}");
        return ok;
    }
}
