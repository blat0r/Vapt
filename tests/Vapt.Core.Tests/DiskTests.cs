using Vapt.Core.Disk;

namespace Vapt.Core.Tests;

public class DiskTests
{
    [Fact]
    public void Squarify_fills_area_without_overlap()
    {
        var areas = new double[] { 6, 6, 4, 3, 2, 2, 1 };
        var scale = 600.0 * 400 / areas.Sum();
        var rects = TreemapLayout.Squarify(areas.Select(a => a * scale).ToList(), new MapRect(0, 0, 600, 400));
        Assert.Equal(areas.Length, rects.Count);
        Assert.Equal(600 * 400, rects.Sum(r => r.W * r.H), 3);
        for (var i = 0; i < rects.Count; i++)
        {
            for (var j = i + 1; j < rects.Count; j++)
            {
                var a = rects[i];
                var b = rects[j];
                var overlapW = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
                var overlapH = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
                Assert.False(overlapW > 0.01 && overlapH > 0.01, $"{i} e {j} se sobrepõem");
            }
        }
    }

    [Fact]
    public async Task Scanner_counts_files_and_sizes()
    {
        var root = Path.Combine(Path.GetTempPath(), "vapt-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "a", "b"));
        File.WriteAllBytes(Path.Combine(root, "x.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(root, "a", "y.bin"), new byte[2000]);
        File.WriteAllBytes(Path.Combine(root, "a", "b", "z.mp4"), new byte[3000]);
        try
        {
            var scanner = new DiskScanner(root);
            await scanner.RunAsync();
            Assert.True(scanner.Finished);
            Assert.Equal(6000, scanner.Root.Size);
            Assert.Equal(3, scanner.Root.FileCount);
            var a = scanner.Root.Children.Single(c => c.Name == "a");
            Assert.Equal(5000, a.Size);
            Assert.Equal(Path.Combine(root, "a", "b", "z.mp4"), a.Children.Single(c => c.Name == "b").Children[0].FullPath);

            var items = TreemapLayout.Build(scanner.Root, new MapRect(0, 0, 800, 600), new TreemapOptions());
            Assert.Contains(items, i => i.Node.Name == "z.mp4");

            var stats = DiskStatsBuilder.Compute(scanner.Root);
            Assert.Equal("z.mp4", stats.Largest[0].Name);
            Assert.Equal(FileCategory.Video, stats.ByCategory.First(c => c.Bytes == 3000).Category);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Filter_supports_spacesniffer_syntax()
    {
        var now = new DateTime(2026, 11, 14);
        var dir = new DiskNode("C:\\", NodeKind.Directory, null);
        var video = new DiskNode("filme.mp4", NodeKind.File, dir, 2L << 30) { Modified = now.AddYears(-3) };
        var tmp = new DiskNode("lixo.tmp", NodeKind.File, dir, 10) { Modified = now.AddDays(-2) };

        Assert.True(DiskFilter.Parse("*.mp4", now).Matches(video));
        Assert.False(DiskFilter.Parse("*.mp4", now).Matches(tmp));
        Assert.True(DiskFilter.Parse(">1gb", now).Matches(video));
        Assert.False(DiskFilter.Parse(">1gb", now).Matches(tmp));
        Assert.True(DiskFilter.Parse(">2anos", now).Matches(video));
        Assert.True(DiskFilter.Parse("<30dias", now).Matches(tmp));
        Assert.False(DiskFilter.Parse("|*.tmp", now).Matches(tmp));
        Assert.True(DiskFilter.Parse("*.mp4,*.tmp; <1mb", now).Matches(tmp));
        Assert.True(DiskFilter.Parse("lixo", now).Matches(tmp));
        Assert.True(DiskFilter.Parse("*.zip", now).Matches(dir)); // pastas não são filtradas
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1536, "1,5 KB")]
    [InlineData(1073741824L, "1 GB")]
    [InlineData(1610612736L * 100, "150 GB")]
    public void Bytes_formats_in_ptbr(long bytes, string expected) => Assert.Equal(expected, Bytes.Format(bytes));
}
