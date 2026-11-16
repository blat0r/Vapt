using Microsoft.Win32;

namespace Vapt.Core.Apps;

public sealed record EssentialApp(string Name, string Description, string WingetId, string DisplayPrefix, string Category, bool PreSelect = true);

/// <summary>
/// Apps para instalar depois de formatar, pelo winget (baixa do site oficial e confere o hash),
/// no estilo do Ninite: marca o que quer e instala tudo de uma vez, em silêncio.
/// </summary>
public static class EssentialApps
{
    public static IReadOnlyList<EssentialApp> Catalog { get; } =
    [
        new("Google Chrome", "O navegador mais usado; sincroniza com a conta Google.", "Google.Chrome", "Google Chrome", "Navegadores"),
        new("Firefox", "Navegador da Mozilla, focado em privacidade.", "Mozilla.Firefox", "Mozilla Firefox", "Navegadores", false),
        new("Brave", "Navegador com bloqueador de anúncios embutido.", "Brave.Brave", "Brave", "Navegadores", false),
        new("Discord", "Voz, vídeo e chat em servidores, o padrão para jogar em grupo.", "Discord.Discord", "Discord", "Comunicação"),
        new("WhatsApp", "WhatsApp para computador.", "9NKSQGP7F2NH", "WhatsApp", "Comunicação", false),
        new("Steam", "A maior loja de jogos de PC, com a sua biblioteca e os amigos.", "Valve.Steam", "Steam", "Jogos"),
        new("Epic Games Launcher", "Loja da Epic (Fortnite e jogos grátis toda semana).", "EpicGames.EpicGamesLauncher", "Epic Games Launcher", "Jogos", false),
        new("7-Zip", "Abre e cria .zip, .rar e .7z. Gratuito e de código aberto.", "7zip.7zip", "7-Zip", "Utilitários"),
        new("WinRAR", "Alternativa ao 7-Zip para .rar; depois de 40 dias pede licença.", "RARLab.WinRAR", "WinRAR", "Utilitários", false),
        new("Lightshot", "Recorta um pedaço da tela e já gera um link para compartilhar.", "Skillbrains.Lightshot", "Lightshot", "Utilitários", false),
        new("ShareX", "Prints e gravação de tela completos, grátis.", "ShareX.ShareX", "ShareX", "Utilitários", false),
        new("Notepad++", "Editor de texto leve com abas.", "Notepad++.Notepad++", "Notepad++", "Utilitários", false),
        new("PowerToys", "Ferramentas extras da Microsoft para o Windows.", "Microsoft.PowerToys", "PowerToys", "Utilitários", false),
        new("VLC", "Toca praticamente qualquer formato de vídeo e áudio, sem codecs extras.", "VideoLAN.VLC", "VLC media player", "Mídia"),
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
