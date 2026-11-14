using Vapt.Core.Debloat;
using Vapt.Core.Tweaks;

namespace Vapt.Core.Tests;

public class DebloatTests
{
    [Fact]
    public void Catalog_loads_with_portuguese_names()
    {
        var apps = DebloatCatalog.Apps;
        Assert.True(apps.Count > 100);
        Assert.Equal(apps.Count, apps.Select(a => a.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(apps, a => Assert.False(string.IsNullOrWhiteSpace(a.Name)));
        Assert.Contains(apps, a => a.Id == "Microsoft.BingNews" && a.Recommended && a.Selected);
        // Itens opcionais nunca vêm marcados.
        Assert.All(apps.Where(a => !a.Recommended), a => Assert.False(a.Selected));
    }

    [Theory]
    [InlineData("Microsoft.WindowsStore")]
    [InlineData("Microsoft.Edge")]
    [InlineData("Microsoft.XboxIdentityProvider")]
    [InlineData("Microsoft.WindowsTerminal")]
    [InlineData("Microsoft.DesktopAppInstaller")]
    [InlineData("Microsoft.SecHealthUI")]
    public void Catalog_never_offers_system_critical_apps(string id) =>
        Assert.DoesNotContain(DebloatCatalog.Apps, a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    [Theory]
    [InlineData("wuauserv")]
    [InlineData("WSearch")]
    [InlineData("SysMain")]
    [InlineData("WinDefend")]
    [InlineData("BITS")]
    [InlineData("Audiosrv")]
    public void Services_never_touch_core_windows(string name) =>
        Assert.DoesNotContain(DebloatCatalog.Services, s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Services_and_tasks_are_well_formed()
    {
        Assert.All(DebloatCatalog.Services, s => Assert.InRange(s.Target, 3, 4));
        Assert.Equal(DebloatCatalog.Services.Count, DebloatCatalog.Services.Select(s => s.Name).Distinct().Count());
        Assert.All(DebloatCatalog.Tasks, t => Assert.StartsWith(@"\Microsoft\Windows\", t.Path));
    }

    [Fact]
    public void Tweak_ids_are_unique() =>
        Assert.Equal(TweakCatalog.All.Count, TweakCatalog.All.Select(t => t.Id).Distinct().Count());
}
