using System.Runtime.InteropServices;

namespace Vapt.Core.Cleanup;

public enum CleanupRisk
{
    /// <summary>Só lixo de verdade: o Windows e os apps recriam sozinhos.</summary>
    Safe,
    /// <summary>Seguro, mas com efeito colateral (ex.: jogos recompilam shaders na primeira vez).</summary>
    Caution,
}

public sealed record CleanupScan(long Bytes, long Files);

public sealed record CleanupResult(long FreedBytes, long Deleted, long Skipped);

public sealed class CleanupItem
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string Glyph { get; init; }

    public CleanupRisk Risk { get; init; }

    public bool NeedsAdmin { get; init; }

    public bool DefaultOn { get; init; } = true;

    /// <summary>Pastas a esvaziar (o conteúdo, não a pasta).</summary>
    public IReadOnlyList<string> Folders { get; init; } = [];

    /// <summary>Arquivos soltos (aceita curinga no nome).</summary>
    public IReadOnlyList<string> FilePatterns { get; init; } = [];

    /// <summary>Só apaga o que não foi modificado nesse tempo (evita arquivos em uso agora).</summary>
    public TimeSpan MinAge { get; init; } = TimeSpan.Zero;

    /// <summary>Medição própria (Lixeira, Otimização de Entrega).</summary>
    public Func<CancellationToken, Task<CleanupScan>>? CustomScan { get; init; }

    public Func<CancellationToken, Task<CleanupResult>>? CustomClean { get; init; }

    /// <summary>Roda antes/depois de limpar (ex.: parar e voltar o Windows Update).</summary>
    public Func<Task>? Before { get; init; }

    public Func<Task>? After { get; init; }
}

/// <summary>
/// O que o Vapt limpa. Lista conservadora baseada no BleachBit e na Limpeza de Disco: só
/// cache e temporários. Fica de fora de propósito: limpeza de registro, Prefetch, cookies e
/// senhas, WinSxS na mão e "liberar RAM" (não ajudam ou fazem mal).
/// </summary>
public static class CleanupCatalog
{
    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private static string Windows => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    public static IReadOnlyList<CleanupItem> Build() =>
    [
        new()
        {
            Id = "user-temp",
            Name = "Arquivos temporários",
            Description = "Sobras de instaladores e programas na sua pasta Temp. Só o que tem mais de um dia.",
            Glyph = "\uE7C3",
            Folders = [Path.GetTempPath()],
            MinAge = TimeSpan.FromDays(1),
        },
        new()
        {
            Id = "recycle",
            Name = "Lixeira",
            Description = "Esvazia a Lixeira de todas as unidades.",
            Glyph = "\uE74D",
            CustomScan = _ => Task.FromResult(RecycleBin.Query()),
            CustomClean = _ => Task.FromResult(RecycleBin.Empty()),
        },
        new()
        {
            Id = "browser-cache",
            Name = "Cache dos navegadores",
            Description = "Chrome, Edge, Brave, Opera, Vivaldi e Firefox. Só o cache: logins, cookies, senhas e histórico ficam.",
            Glyph = "\uE774",
            Folders = BrowserCacheFolders(),
        },
        new()
        {
            Id = "thumbnails",
            Name = "Miniaturas do Explorador",
            Description = "Cache de miniaturas de fotos e vídeos. O Explorador refaz quando você abrir as pastas.",
            Glyph = "\uE91B",
            Risk = CleanupRisk.Caution,
            DefaultOn = false,
            FilePatterns = [Path.Combine(Local, @"Microsoft\Windows\Explorer\thumbcache_*.db")],
        },
        new()
        {
            Id = "crash",
            Name = "Relatórios de erro e despejos",
            Description = "Relatórios do Windows (WER) e arquivos de despejo de travamentos (.dmp).",
            Glyph = "\uEBE8",
            NeedsAdmin = false,
            Folders =
            [
                Path.Combine(Local, @"Microsoft\Windows\WER\ReportArchive"),
                Path.Combine(Local, @"Microsoft\Windows\WER\ReportQueue"),
                Path.Combine(Local, "CrashDumps"),
            ],
        },
        new()
        {
            Id = "shader-cache",
            Name = "Cache de shaders",
            Description = "DirectX, NVIDIA e AMD. Libera espaço, mas jogos podem engasgar na primeira vez que abrirem.",
            Glyph = "\uE7FC",
            Risk = CleanupRisk.Caution,
            DefaultOn = false,
            Folders =
            [
                Path.Combine(Local, "D3DSCache"),
                Path.Combine(Local, @"NVIDIA\DXCache"),
                Path.Combine(Local, @"NVIDIA\GLCache"),
                Path.Combine(Local, @"AMD\DxCache"),
                Path.Combine(Local, @"AMD\DxcCache"),
                Path.Combine(Local, @"AMD\GLCache"),
            ],
        },
        new()
        {
            Id = "win-temp",
            Name = "Temporários do Windows",
            Description = "A pasta Temp do sistema (C:\\Windows\\Temp).",
            Glyph = "\uE7C3",
            NeedsAdmin = true,
            Folders = [Path.Combine(Windows, "Temp")],
            MinAge = TimeSpan.FromDays(1),
        },
        new()
        {
            Id = "wer-system",
            Name = "Relatórios de erro do sistema",
            Description = "Relatórios e despejos de memória gravados pelo Windows.",
            Glyph = "\uEBE8",
            NeedsAdmin = true,
            Folders =
            [
                Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportArchive"),
                Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportQueue"),
                Path.Combine(Windows, "Minidump"),
                Path.Combine(Windows, "LiveKernelReports"),
            ],
            FilePatterns = [Path.Combine(Windows, "MEMORY.DMP")],
        },
        new()
        {
            Id = "windows-update",
            Name = "Downloads do Windows Update",
            Description = "Atualizações já instaladas que ficaram baixadas. O serviço para durante a limpeza e volta em seguida.",
            Glyph = "\uE895",
            NeedsAdmin = true,
            Folders = [Path.Combine(Windows, @"SoftwareDistribution\Download")],
            Before = () => ProcessRunner.PowerShellAsync("Stop-Service wuauserv,bits -Force -ErrorAction SilentlyContinue"),
            After = () => ProcessRunner.PowerShellAsync("Start-Service wuauserv,bits -ErrorAction SilentlyContinue"),
        },
        new()
        {
            Id = "delivery",
            Name = "Otimização de Entrega",
            Description = "Cache que o Windows usa para compartilhar atualizações com outros PCs.",
            Glyph = "\uE753",
            NeedsAdmin = true,
            CustomScan = DeliveryOptimization.ScanAsync,
            CustomClean = DeliveryOptimization.CleanAsync,
        },
        new()
        {
            Id = "old-logs",
            Name = "Logs antigos do Windows",
            Description = "Arquivos .log e .etl com mais de 15 dias em C:\\Windows\\Logs.",
            Glyph = "\uE9F9",
            NeedsAdmin = true,
            Risk = CleanupRisk.Caution,
            DefaultOn = false,
            FilePatterns =
            [
                Path.Combine(Windows, @"Logs\*.log"),
                Path.Combine(Windows, @"Logs\*.etl"),
                Path.Combine(Windows, @"Logs\CBS\*.log"),
                Path.Combine(Windows, @"Logs\DISM\*.log"),
            ],
            MinAge = TimeSpan.FromDays(15),
        },
    ];

    /// <summary>Pastas de cache de cada perfil dos navegadores (nunca cookies, senhas ou histórico).</summary>
    private static List<string> BrowserCacheFolders()
    {
        var folders = new List<string>();
        var chromium = new[]
        {
            Path.Combine(Local, @"Google\Chrome\User Data"),
            Path.Combine(Local, @"Microsoft\Edge\User Data"),
            Path.Combine(Local, @"BraveSoftware\Brave-Browser\User Data"),
            Path.Combine(Local, @"Vivaldi\User Data"),
            Path.Combine(Roaming, @"Opera Software\Opera Stable"),
            Path.Combine(Roaming, @"Opera Software\Opera GX Stable"),
            Path.Combine(Local, @"Opera Software\Opera Stable"),
            Path.Combine(Local, @"Opera Software\Opera GX Stable"),
        };
        foreach (var root in chromium.Where(Directory.Exists))
        {
            var profiles = new List<string> { root };
            try
            {
                profiles.AddRange(Directory.EnumerateDirectories(root).Where(d =>
                {
                    var n = Path.GetFileName(d);
                    return n == "Default" || n.StartsWith("Profile ", StringComparison.Ordinal);
                }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            foreach (var p in profiles)
            {
                folders.Add(Path.Combine(p, @"Cache\Cache_Data"));
                folders.Add(Path.Combine(p, "Code Cache"));
                folders.Add(Path.Combine(p, "GPUCache"));
                folders.Add(Path.Combine(p, "DawnGraphiteCache"));
                folders.Add(Path.Combine(p, "DawnWebGPUCache"));
            }

            folders.Add(Path.Combine(root, "ShaderCache"));
            folders.Add(Path.Combine(root, "GrShaderCache"));
        }

        var firefox = Path.Combine(Local, @"Mozilla\Firefox\Profiles");
        if (Directory.Exists(firefox))
        {
            try
            {
                folders.AddRange(Directory.EnumerateDirectories(firefox).Select(p => Path.Combine(p, "cache2")));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return folders;
    }
}

/// <summary>Lixeira via SHQueryRecycleBin / SHEmptyRecycleBin (sem diálogo nem som).</summary>
internal static class RecycleBin
{
    public static CleanupScan Query()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBinW(null, ref info) == 0 ? new CleanupScan(info.i64Size, info.i64NumItems) : new CleanupScan(0, 0);
    }

    public static CleanupResult Empty()
    {
        var before = Query();
        const uint SHERB_NOCONFIRMATION = 1, SHERB_NOPROGRESSUI = 2, SHERB_NOSOUND = 4;
        var hr = SHEmptyRecycleBinW(0, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
        var after = Query();
        return hr is 0 or unchecked((int)0x8000FFFF) // E_UNEXPECTED = já estava vazia
            ? new CleanupResult(before.Bytes - after.Bytes, before.Files - after.Files, after.Files)
            : new CleanupResult(0, 0, before.Files);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBinW(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBinW(nint hwnd, string? pszRootPath, uint dwFlags);
}

internal static class DeliveryOptimization
{
    public static async Task<CleanupScan> ScanAsync(CancellationToken cancellation)
    {
        var r = await ProcessRunner.PowerShellAsync("(Get-DeliveryOptimizationPerfSnap).CacheSizeBytes", cancellation: cancellation);
        return long.TryParse(r.Output.Trim(), out var bytes) ? new CleanupScan(bytes, bytes > 0 ? 1 : 0) : new CleanupScan(0, 0);
    }

    public static async Task<CleanupResult> CleanAsync(CancellationToken cancellation)
    {
        var before = await ScanAsync(cancellation);
        var r = await ProcessRunner.PowerShellAsync("Delete-DeliveryOptimizationCache -Force", cancellation: cancellation);
        var after = await ScanAsync(cancellation);
        return new CleanupResult(Math.Max(0, before.Bytes - after.Bytes), r.Ok ? 1 : 0, r.Ok ? 0 : 1);
    }
}
