using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vapt.Core.Repair;

public sealed record PowerPlan(Guid Id, string Name, bool Active);

public sealed record DnsPreset(string Name, string Description, string[] Servers);

public sealed record RepairResult(bool Ok, string Message);

/// <summary>
/// Ferramentas de reparo que os otimizadores juntam num lugar só (Glary, Wise, Revo):
/// SFC, DISM, cache de ícones, rede, DNS e plano de energia. Tudo pelos comandos do próprio
/// Windows.
/// </summary>
public static partial class RepairTools
{
    // O powercfg escreve na página de código OEM (850 no Brasil), que o .NET só lê com este provedor.
    static RepairTools() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static string Sys(string exe) => Path.Combine(ProcessRunner.System32, exe);

    /// <summary>Verificador de arquivos do sistema. A saída do SFC é UTF-16.</summary>
    public static async Task<RepairResult> SfcAsync(Action<double>? percent, CancellationToken cancellation = default)
    {
        var r = await ProcessRunner.RunAsync(Sys("sfc.exe"), "/scannow", line => Percent(line, percent), cancellation, Encoding.Unicode);
        var output = r.Output.Replace("\0", "");
        Log.Info("SFC terminou com código " + r.ExitCode);
        // Códigos do SFC não são confiáveis; o texto final diz o resultado (traduzido).
        var last = output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 20 && !l.Contains('%')).LastOrDefault() ?? "";
        return new RepairResult(r.Ok, last.Length > 0 ? last : r.Ok ? "Nenhum problema encontrado." : "O SFC não conseguiu terminar.");
    }

    /// <summary>Repara a imagem do Windows baixando arquivos bons do Windows Update.</summary>
    public static async Task<RepairResult> DismRestoreAsync(Action<double>? percent, CancellationToken cancellation = default)
    {
        var r = await ProcessRunner.RunAsync(Sys("dism.exe"), "/Online /Cleanup-Image /RestoreHealth", line => Percent(line, percent), cancellation);
        Log.Info("DISM RestoreHealth terminou com código " + r.ExitCode);
        return new RepairResult(r.Ok, r.Ok ? "Imagem do Windows reparada (ou já estava íntegra)." : "O DISM falhou (código " + r.ExitCode + "). Veja se há internet.");
    }

    private static void Percent(string line, Action<double>? report)
    {
        var m = PercentRegex().Match(line.Replace("\0", ""));
        if (m.Success && double.TryParse(m.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            report?.Invoke(Math.Clamp(v, 0, 100));
        }
    }

    /// <summary>Apaga o cache de ícones e miniaturas e reinicia o Explorador (ícones brancos ou errados).</summary>
    public static async Task<RepairResult> RebuildIconCacheAsync()
    {
        await ProcessRunner.RunAsync(Sys("taskkill.exe"), "/f /im explorer.exe");
        await Task.Delay(800);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var deleted = 0;
        var targets = new List<string> { Path.Combine(local, "IconCache.db") };
        var explorer = Path.Combine(local, @"Microsoft\Windows\Explorer");
        if (Directory.Exists(explorer))
        {
            targets.AddRange(Directory.EnumerateFiles(explorer, "iconcache_*.db"));
            targets.AddRange(Directory.EnumerateFiles(explorer, "thumbcache_*.db"));
        }

        foreach (var file in targets.Where(File.Exists))
        {
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true });
        return new RepairResult(true, $"Cache refeito ({deleted} arquivos). Os ícones voltam a aparecer em alguns segundos.");
    }

    public static async Task<RepairResult> FlushDnsAsync()
    {
        var r = await ProcessRunner.RunAsync(Sys("ipconfig.exe"), "/flushdns");
        return new RepairResult(r.Ok, r.Ok ? "Cache de DNS limpo." : "Não deu para limpar o cache de DNS.");
    }

    /// <summary>Redefine Winsock e TCP/IP (o "redefinir rede" do Windows sem apagar o Wi-Fi salvo).</summary>
    public static async Task<RepairResult> ResetNetworkAsync()
    {
        var a = await ProcessRunner.RunAsync(Sys("netsh.exe"), "winsock reset");
        var b = await ProcessRunner.RunAsync(Sys("netsh.exe"), "int ip reset");
        await ProcessRunner.RunAsync(Sys("ipconfig.exe"), "/flushdns");
        var ok = a.Ok && b.Ok;
        Log.Info($"Rede redefinida: winsock={a.ExitCode} ip={b.ExitCode}");
        return new RepairResult(ok, ok ? "Rede redefinida. Reinicie o PC para terminar." : "Parte da redefinição falhou (precisa de administrador).");
    }

    // ── DNS ──────────────────────────────────────────────────────────────

    public static IReadOnlyList<DnsPreset> DnsPresets { get; } =
    [
        new("Automático", "O DNS do seu provedor (padrão).", []),
        new("Cloudflare", "1.1.1.1 · o mais rápido na maioria dos lugares.", ["1.1.1.1", "1.0.0.1"]),
        new("Google", "8.8.8.8 · estável e muito usado.", ["8.8.8.8", "8.8.4.4"]),
        new("Quad9", "9.9.9.9 · bloqueia sites de golpe e malware.", ["9.9.9.9", "149.112.112.112"]),
        new("AdGuard", "Bloqueia anúncios e rastreadores em todo o PC.", ["94.140.14.14", "94.140.15.15"]),
    ];

    /// <summary>Servidores DNS IPv4 das placas de rede ligadas (vazio = automático).</summary>
    public static async Task<string[]> CurrentDnsAsync()
    {
        const string script = """
            $idx = @(Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | ForEach-Object { $_.InterfaceIndex })
            $s = @(Get-DnsClientServerAddress -AddressFamily IPv4 | Where-Object { $idx -contains $_.InterfaceIndex } | ForEach-Object { $_.ServerAddresses } | Select-Object -Unique)
            ConvertTo-Json -InputObject $s -Compress
            """;
        var r = await ProcessRunner.PowerShellAsync(script);
        try
        {
            return r.Ok ? JsonSerializer.Deserialize<string[]>(r.Output.Trim()) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static async Task<RepairResult> SetDnsAsync(DnsPreset preset)
    {
        var action = preset.Servers.Length == 0
            ? "Set-DnsClientServerAddress -InterfaceIndex $_.InterfaceIndex -ResetServerAddresses"
            : $"Set-DnsClientServerAddress -InterfaceIndex $_.InterfaceIndex -ServerAddresses {string.Join(",", preset.Servers.Select(s => $"'{s}'"))}";
        var r = await ProcessRunner.PowerShellAsync($"Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | ForEach-Object {{ {action} }}; Clear-DnsClientCache");
        Log.Info($"DNS → {preset.Name}: {(r.Ok ? "ok" : r.Error.Trim())}");
        return new RepairResult(r.Ok, r.Ok ? $"DNS trocado para {preset.Name}." : "Não deu para trocar o DNS (precisa de administrador).");
    }

    // ── Energia ──────────────────────────────────────────────────────────

    private static readonly Guid UltimateTemplate = Guid.Parse("e9a42b02-d5df-448d-aa00-03f14749eb61");

    public static async Task<IReadOnlyList<PowerPlan>> PowerPlansAsync()
    {
        var r = await ProcessRunner.RunAsync(Sys("powercfg.exe"), "/list", encoding: Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage));
        return ParsePowerPlans(r.Output);
    }

    internal static List<PowerPlan> ParsePowerPlans(string output) =>
        PowerPlanRegex().Matches(output)
            .Select(m => new PowerPlan(Guid.Parse(m.Groups[1].Value), m.Groups[2].Value.Trim(), m.Groups[3].Success))
            .ToList();

    public static async Task<RepairResult> SetPowerPlanAsync(PowerPlan plan)
    {
        var r = await ProcessRunner.RunAsync(Sys("powercfg.exe"), $"/setactive {plan.Id}");
        return new RepairResult(r.Ok, r.Ok ? $"Plano \"{plan.Name}\" ativado." : "Não deu para trocar o plano de energia.");
    }

    /// <summary>Cria o plano "Desempenho Máximo" (escondido por padrão em desktops).</summary>
    public static async Task<RepairResult> CreateUltimatePlanAsync()
    {
        var r = await ProcessRunner.RunAsync(Sys("powercfg.exe"), $"-duplicatescheme {UltimateTemplate}");
        return new RepairResult(r.Ok, r.Ok ? "Plano Desempenho Máximo criado." : "Este Windows não tem o plano Desempenho Máximo.");
    }

    [GeneratedRegex(@"(\d{1,3}(?:[.,]\d+)?)\s*%")]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s+\(([^)]*)\)(\s*\*)?")]
    private static partial Regex PowerPlanRegex();
}
