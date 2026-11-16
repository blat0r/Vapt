using Vapt.Core.Cleanup;
using Vapt.Core.Debloat;
using Vapt.Core.Startup;
using Vapt.Core.Tweaks;

namespace Vapt.Core;

public enum FindingLevel
{
    /// <summary>Vale corrigir (pesa mais na nota).</summary>
    Important,
    /// <summary>Melhoria recomendada.</summary>
    Suggestion,
    /// <summary>Só informação.</summary>
    Info,
}

/// <summary>Algo que o check-up encontrou, com o que fazer.</summary>
public sealed class Finding
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Detail { get; init; }

    public required string Glyph { get; init; }

    public FindingLevel Level { get; init; } = FindingLevel.Suggestion;

    /// <summary>Pontos que saem da nota de 100.</summary>
    public int Weight { get; init; }

    /// <summary>Correção automática (nula = só leva à página).</summary>
    public Func<CancellationToken, Task<string>>? Fix { get; init; }

    /// <summary>Entra no "Corrigir tudo" (só o que é seguro e reversível).</summary>
    public bool SafeForFixAll { get; init; }

    public bool NeedsAdmin { get; init; }

    /// <summary>Página para revisar com calma.</summary>
    public string? Page { get; init; }

    public string? FixLabel { get; init; }
}

public sealed record HealthReport(int Score, IReadOnlyList<Finding> Findings)
{
    public string Verdict => Score switch
    {
        >= 90 => "Seu PC está em ótima forma",
        >= 75 => "Seu PC está bem, com alguns ajustes possíveis",
        >= 55 => "Dá para deixar seu PC bem mais leve",
        _ => "Seu PC precisa de uma faxina",
    };
}

/// <summary>
/// Check-up do PC: olha lixo, bloatware, inicialização, privacidade, Recall, disco e
/// hibernação e dá uma nota de 0 a 100 com o que fazer em cada caso.
/// </summary>
public static class HealthCheck
{
    /// <summary>Ajustes da sua conta que entram no "Corrigir tudo" (não precisam de admin, desfazíveis).</summary>
    private static readonly string[] PrivacyBasics = ["ads-id", "tailored", "suggestions", "inking", "bing-search"];

    public static async Task<HealthReport> RunAsync(CancellationToken cancellation = default)
    {
        var findings = new List<Finding>();
        var admin = Elevation.IsAdministrator;

        // 1) Lixo seguro.
        var safeItems = CleanupCatalog.Build().Where(i => i.Risk == CleanupRisk.Safe && i.DefaultOn && (!i.NeedsAdmin || admin)).ToList();
        long junk = 0;
        foreach (var item in safeItems)
        {
            junk += (await CleanupEngine.ScanAsync(item, cancellation)).Bytes;
        }

        if (junk > 200L * 1024 * 1024)
        {
            findings.Add(new Finding
            {
                Id = "junk",
                Title = $"{Bytes.Format(junk)} de arquivos inúteis",
                Detail = "Temporários, cache dos navegadores, relatórios de erro e a Lixeira.",
                Glyph = "",
                Level = junk > 2L * 1024 * 1024 * 1024 ? FindingLevel.Important : FindingLevel.Suggestion,
                Weight = junk > 5L * 1024 * 1024 * 1024 ? 15 : junk > 1L * 1024 * 1024 * 1024 ? 10 : 5,
                SafeForFixAll = true,
                FixLabel = "Limpar",
                Page = "cleanup",
                Fix = async ct =>
                {
                    long freed = 0;
                    foreach (var item in safeItems)
                    {
                        freed += (await CleanupEngine.CleanAsync(item, ct)).FreedBytes;
                    }

                    return $"{Bytes.Format(freed)} liberados";
                },
            });
        }

        // 2) Bloatware recomendado.
        try
        {
            var bloat = (await DebloatEngine.DetectAsync(cancellation)).Where(b => b.App.Recommended && b.PackageFullName is not null).ToList();
            if (bloat.Count > 0)
            {
                findings.Add(new Finding
                {
                    Id = "bloat",
                    Title = $"{bloat.Count} {(bloat.Count == 1 ? "app inútil instalado" : "apps inúteis instalados")}",
                    Detail = string.Join(", ", bloat.Take(4).Select(b => b.App.Name)) + (bloat.Count > 4 ? "…" : "") + ". Saem sem efeito colateral e voltam pela Loja se precisar.",
                    Glyph = "",
                    Level = bloat.Count >= 5 ? FindingLevel.Important : FindingLevel.Suggestion,
                    Weight = Math.Min(15, 3 + bloat.Count * 2),
                    FixLabel = "Revisar",
                    Page = "debloat",
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("Check-up: debloat: " + ex.Message);
        }

        // 3) Inicialização cheia.
        var startup = StartupManager.List().Where(s => s.Enabled).ToList();
        if (startup.Count > 6)
        {
            findings.Add(new Finding
            {
                Id = "startup",
                Title = $"{startup.Count} programas abrem com o Windows",
                Detail = "Cada um atrasa o PC ficar pronto depois de ligar. Desligue os que você abre só de vez em quando.",
                Glyph = "",
                Level = startup.Count > 10 ? FindingLevel.Important : FindingLevel.Suggestion,
                Weight = Math.Min(12, (startup.Count - 6) * 2),
                FixLabel = "Revisar",
                Page = "startup",
            });
        }

        // 4) Privacidade básica da conta.
        var pending = new List<Tweak>();
        foreach (var id in PrivacyBasics)
        {
            if (TweakCatalog.Available.FirstOrDefault(t => t.Id == id) is { } t && !await TweakEngine.IsAppliedAsync(t))
            {
                pending.Add(t);
            }
        }

        if (pending.Count > 0)
        {
            findings.Add(new Finding
            {
                Id = "privacy",
                Title = $"{pending.Count} {(pending.Count == 1 ? "ajuste de privacidade desligado" : "ajustes de privacidade desligados")}",
                Detail = string.Join(" · ", pending.Select(p => p.Name)),
                Glyph = "",
                Weight = pending.Count * 2,
                SafeForFixAll = true,
                FixLabel = "Aplicar",
                Page = "debloat",
                Fix = async _ =>
                {
                    foreach (var t in pending)
                    {
                        await TweakEngine.ApplyAsync(t);
                    }

                    return $"{pending.Count} ajustes aplicados";
                },
            });
        }

        // 5) Recall.
        var recall = RecallInfo.Probe();
        var recallTweak = TweakCatalog.All.First(t => t.Id == "recall-snapshots");
        if (recall.HasSnapshots && !await TweakEngine.IsAppliedAsync(recallTweak))
        {
            findings.Add(new Finding
            {
                Id = "recall",
                Title = "O Recall está guardando capturas da sua tela",
                Detail = $"Ele fotografa a tela a cada poucos segundos. Já ocupa {Bytes.Format(recall.SnapshotBytes)}.",
                Glyph = "",
                Level = FindingLevel.Important,
                Weight = 10,
                FixLabel = "Desligar",
                Page = "debloat",
                NeedsAdmin = true,
                Fix = admin ? async _ => { await TweakEngine.ApplyAsync(recallTweak); return "Recall parou de capturar"; } : null,
            });
        }

        // 6) Disco do sistema cheio.
        try
        {
            var drive = SystemInfo.SystemDrive;
            var freeRatio = drive.TotalFreeSpace / (double)drive.TotalSize;
            if (freeRatio < 0.15)
            {
                findings.Add(new Finding
                {
                    Id = "disk",
                    Title = $"Só {Bytes.Format(drive.TotalFreeSpace)} livres no disco do sistema",
                    Detail = "Com pouco espaço o Windows fica lento e atualizações falham. Veja o que ocupa mais no mapa.",
                    Glyph = "",
                    Level = freeRatio < 0.08 ? FindingLevel.Important : FindingLevel.Suggestion,
                    Weight = freeRatio < 0.08 ? 15 : 8,
                    FixLabel = "Ver o mapa",
                    Page = "disk",
                });
            }
        }
        catch (IOException)
        {
        }

        // 7) Arquivo de hibernação grande em desktop.
        var hiberfil = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "hiberfil.sys");
        long hiberSize = 0;
        try
        {
            var fi = new FileInfo(hiberfil);
            hiberSize = fi.Exists ? fi.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        if (hiberSize > 4L * 1024 * 1024 * 1024 && !SystemInfo.HasBattery)
        {
            findings.Add(new Finding
            {
                Id = "hibernate",
                Title = $"Hibernação ocupa {Bytes.Format(hiberSize)}",
                Detail = "Em PC de mesa quase ninguém hiberna. Desligar libera esse espaço na hora (também desliga a Inicialização Rápida).",
                Glyph = "",
                Weight = 5,
                FixLabel = "Desligar",
                Page = "tweaks",
                NeedsAdmin = true,
                Fix = admin ? async _ => { await TweakEngine.ApplyAsync(TweakCatalog.All.First(t => t.Id == "hibernate")); return $"{Bytes.Format(hiberSize)} liberados"; } : null,
            });
        }

        // 8) Sensor de armazenamento.
        var storageSense = TweakCatalog.All.First(t => t.Id == "storage-sense");
        if (!await TweakEngine.IsAppliedAsync(storageSense))
        {
            findings.Add(new Finding
            {
                Id = "storage-sense",
                Title = "Sensor de Armazenamento desligado",
                Detail = "Ligado, o próprio Windows apaga temporários sozinho quando o disco enche.",
                Glyph = "",
                Weight = 3,
                SafeForFixAll = true,
                FixLabel = "Ligar",
                Page = "tweaks",
                Fix = async _ => { await TweakEngine.ApplyAsync(storageSense); return "Sensor ligado"; },
            });
        }

        // 9) Muito tempo sem reiniciar.
        var up = SystemInfo.Uptime;
        if (up.TotalDays >= 7)
        {
            findings.Add(new Finding
            {
                Id = "uptime",
                Title = $"Ligado há {(int)up.TotalDays} dias sem reiniciar",
                Detail = "Reiniciar aplica atualizações pendentes e libera memória presa por drivers.",
                Glyph = "",
                Level = FindingLevel.Info,
                Weight = 3,
            });
        }

        var score = Math.Clamp(100 - findings.Sum(f => f.Weight), 0, 100);
        return new HealthReport(score, findings.OrderBy(f => f.Level).ThenByDescending(f => f.Weight).ToList());
    }
}
