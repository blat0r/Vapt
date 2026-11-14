using Vapt.Core.Tweaks;

namespace Vapt.Core.Tests;

public class HealthCheckTests
{
    [Fact]
    public async Task Checkup_runs_on_this_machine_and_scores_consistently()
    {
        var report = await HealthCheck.RunAsync();
        Assert.InRange(report.Score, 0, 100);
        Assert.Equal(Math.Clamp(100 - report.Findings.Sum(f => f.Weight), 0, 100), report.Score);
        Assert.Equal(report.Findings.Count, report.Findings.Select(f => f.Id).Distinct().Count());
        // "Corrigir tudo" só leva o que não precisa de administrador e tem correção.
        Assert.All(report.Findings.Where(f => f.SafeForFixAll), f =>
        {
            Assert.NotNull(f.Fix);
            Assert.False(f.NeedsAdmin);
        });
        Assert.False(string.IsNullOrWhiteSpace(report.Verdict));
    }

    [Fact]
    public void Ai_group_has_recall_tweaks() =>
        Assert.Contains(TweakCatalog.All, t => t.Group == TweakGroup.AI && t.Id == "recall-snapshots");
}
