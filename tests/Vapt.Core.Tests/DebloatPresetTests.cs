using Vapt.Core.Debloat;
using Vapt.Core.Tweaks;

namespace Vapt.Core.Tests;

public class DebloatPresetTests
{
    [Fact]
    public async Task Recommended_plan_only_has_safe_items()
    {
        var plan = await DebloatPreset.PlanAsync();
        var tweaks = plan.Ads.Concat(plan.Privacy).Concat(plan.Ai).ToList();
        Assert.All(tweaks, t => Assert.Equal(TweakRisk.Safe, t.Risk));
        Assert.All(plan.Apps, a => Assert.True(a.App.Recommended && a.App.Selected));
        Assert.All(plan.Services, s => Assert.False(s.Caution));
        if (!Elevation.IsAdministrator)
        {
            Assert.All(tweaks, t => Assert.False(t.NeedsAdmin));
            Assert.Empty(plan.Tasks);
            Assert.Empty(plan.Services);
        }
    }

    [Fact]
    public void Every_group_has_items_and_names()
    {
        foreach (var g in Enum.GetValues<TweakGroup>())
        {
            Assert.Contains(TweakCatalog.All, t => t.Group == g);
            Assert.False(string.IsNullOrWhiteSpace(TweakCatalog.GroupName(g)));
        }

        Assert.Contains(DebloatCatalog.Services, s => s.Kind == ServiceKind.Telemetry);
        Assert.Contains(DebloatCatalog.Services, s => s.Kind == ServiceKind.AI);
        Assert.Contains(DebloatCatalog.Services, s => s.Kind == ServiceKind.Background);
    }
}
