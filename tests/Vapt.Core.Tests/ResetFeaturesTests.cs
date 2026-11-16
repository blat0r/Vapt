using Vapt.Core.Apps;
using Vapt.Core.Hardware;
using Vapt.Core.Net;
using Vapt.Core.Tweaks;

namespace Vapt.Core.Tests;

public class ResetFeaturesTests
{
    private static PingStats Ping(string name, double avg, double jitter = 1, int lost = 0) => new(name, "1.1.1.1", 30, lost, avg, avg - 1, avg + 2, jitter);

    [Fact]
    public void Network_verdict_blames_wifi_when_router_is_unstable()
    {
        var r = NetworkTest.Judge(Ping("Roteador", 35, jitter: 25, lost: 2), [Ping("Cloudflare", 40, jitter: 22)], 20, 300, 50, 45, "Wi-Fi · Wi-Fi");
        Assert.NotEqual(Health.Good, r.Verdict);
        Assert.Contains(r.Reasons, s => s.Contains("Wi-Fi"));
    }

    [Fact]
    public void Network_verdict_blames_provider_when_lan_is_fine()
    {
        var r = NetworkTest.Judge(Ping("Roteador", 2), [Ping("Cloudflare", 30, lost: 3)], 20, 300, 50, 35, "Cabo · Ethernet");
        Assert.Equal(Health.Critical, r.Verdict);
        Assert.Contains(r.Reasons, s => s.Contains("provedor"));
    }

    [Fact]
    public void Network_verdict_detects_bufferbloat()
    {
        var r = NetworkTest.Judge(Ping("Roteador", 2), [Ping("Cloudflare", 15)], 20, 300, 50, 160, "Cabo");
        Assert.Contains(r.Reasons, s => s.Contains("Bufferbloat"));
    }

    [Fact]
    public void Good_connection_is_stable()
    {
        var r = NetworkTest.Judge(Ping("Roteador", 1), [Ping("Cloudflare", 12), Ping("Google", 14)], 18, 500, 100, 20, "Cabo");
        Assert.Equal(Health.Good, r.Verdict);
    }

    [Fact]
    public async Task System_report_reads_this_pc()
    {
        var r = await SystemReportBuilder.BuildAsync();
        Assert.NotEmpty(r.Cpu);
        Assert.NotEmpty(r.Disks);
        Assert.Contains(r.RamSummary, i => i.Label == "Total");
        var html = ReportHtml.Build(r);
        Assert.Contains("<html", html);
        Assert.Contains("Saúde dos discos", html);
    }

    [Fact]
    public async Task Disk_bench_runs_without_cache_and_cleans_up()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var r = await Core.Disk.DiskBench.RunAsync(root, 64L << 20, null, CancellationToken.None);
        Assert.True(r.SeqReadMBs > 0 && r.SeqWriteMBs > 0 && r.Rand4kQ1MBs > 0 && r.Rand4kQ32MBs > 0);
        Assert.False(Directory.Exists(Path.Combine(root, "VaptBench")) && Directory.EnumerateFiles(Path.Combine(root, "VaptBench")).Any());
    }

    [Fact]
    public void Essential_catalog_has_unique_winget_ids() =>
        Assert.Equal(EssentialApps.Catalog.Count, EssentialApps.Catalog.Select(a => a.WingetId).Distinct().Count());

    [Fact]
    public void Game_boost_never_lists_system_processes()
    {
        var apps = GameBoost.List();
        Assert.DoesNotContain(apps, a => a.ProcessName is "explorer" or "svchost" or "csrss" or "MsMpEng" or "dwm");
    }

    [Fact]
    public void Windows11_only_tweaks_are_hidden_on_older_builds()
    {
        var recall = TweakCatalog.All.First(t => t.Id == "recall-snapshots");
        Assert.Equal(22000, recall.MinBuild);
        Assert.Equal(SystemInfo.IsWindows11, TweakCatalog.Available.Contains(recall));
        Assert.All(TweakCatalog.All.Where(t => t.MinBuild == 0), t => Assert.Contains(t, TweakCatalog.Available));
    }
}
