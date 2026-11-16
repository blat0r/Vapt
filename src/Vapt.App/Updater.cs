using Velopack;
using Velopack.Sources;
using Vapt.Core;

namespace Vapt;

public enum UpdateState
{
    NotInstalled,
    UpToDate,
    Ready,
    Failed,
}

/// <summary>
/// Atualização automática pelas Releases do GitHub (Velopack). Toda versão nova publicada em
/// github.com/blat0r/Vapt/releases é baixada em segundo plano; o usuário escolhe reiniciar agora
/// ou ela é aplicada quando o Vapt fechar. Fora de uma instalação (build de desenvolvimento) não faz nada.
/// </summary>
internal static class Updater
{
    public const string RepoUrl = "https://github.com/blat0r/Vapt";

    private static readonly Lazy<UpdateManager> Manager = new(() => new UpdateManager(new GithubSource(RepoUrl, null, false)));
    private static UpdateInfo? _ready;
    private static bool _applyingNow;

    public static bool IsInstalled
    {
        get
        {
            try
            {
                return Manager.Value.IsInstalled;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public static string CurrentVersion =>
        (IsInstalled ? Manager.Value.CurrentVersion?.ToString() : null)
        ?? typeof(Updater).Assembly.GetName().Version?.ToString(3)
        ?? "?";

    public static string? ReadyVersion => _ready?.TargetFullRelease.Version.ToString();

    /// <summary>Procura e já baixa a versão nova. Devolve o estado para a interface.</summary>
    public static async Task<UpdateState> CheckAndDownloadAsync()
    {
        if (!IsInstalled)
        {
            return UpdateState.NotInstalled;
        }

        if (_ready is not null)
        {
            return UpdateState.Ready;
        }

        try
        {
            var info = await Manager.Value.CheckForUpdatesAsync();
            if (info is null)
            {
                Log.Info($"Atualização: {CurrentVersion} é a mais nova.");
                return UpdateState.UpToDate;
            }

            Log.Info($"Atualização: baixando a {info.TargetFullRelease.Version}.");
            await Manager.Value.DownloadUpdatesAsync(info);
            _ready = info;
            return UpdateState.Ready;
        }
        catch (Exception ex)
        {
            Log.Warn("Atualização: não deu para procurar: " + ex.Message);
            return UpdateState.Failed;
        }
    }

    /// <summary>Fecha, atualiza e abre de novo.</summary>
    public static void ApplyAndRestart()
    {
        if (_ready is null)
        {
            return;
        }

        _applyingNow = true;
        Log.Info($"Atualização: aplicando a {ReadyVersion} e reiniciando.");
        Manager.Value.ApplyUpdatesAndRestart(_ready);
    }

    /// <summary>Chamado ao fechar a janela: se há versão baixada, ela entra quando o Vapt sair.</summary>
    public static void ApplyOnExitIfReady()
    {
        if (_ready is null || _applyingNow)
        {
            return;
        }

        try
        {
            Manager.Value.WaitExitThenApplyUpdates(_ready, silent: true, restart: false);
            Log.Info($"Atualização: a {ReadyVersion} será aplicada ao fechar.");
        }
        catch (Exception ex)
        {
            Log.Warn("Atualização ao fechar: " + ex.Message);
        }
    }
}
