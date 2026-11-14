using Vapt.Core.Apps;
using Vapt.Core.Files;
using Vapt.Core.Repair;

namespace Vapt.Core.Tests;

public class AppsToolsTests
{
    [Theory]
    [InlineData("Cheat Engine 7.6", "Cheat Engine")]
    [InlineData("7-Zip 24.08 (x64)", "7-Zip")]
    [InlineData("Microsoft Visual C++ 2013 Redistributable (x64) - 12.0.30501", "Microsoft Visual C++ Redistributable")]
    [InlineData("Discord", "Discord")]
    public void CleanName_drops_versions_and_architecture(string input, string expected) =>
        Assert.Equal(expected, Leftovers.CleanName(input));

    [Theory]
    [InlineData("Cheat Engine", "Cheat Engine 7.6", true)]
    [InlineData("CheatEngine", "Cheat Engine", true)]
    [InlineData("Cheat", "Cheat Engine", false)] // curto demais / genérico demais
    [InlineData("Microsoft", "Microsoft Teams", false)]
    [InlineData("Git", "Git", false)] // nome curto: nunca adivinha
    [InlineData("Discord", "Discord", true)]
    public void NameMatches_is_conservative(string folder, string app, bool expected) =>
        Assert.Equal(expected, Leftovers.NameMatches(folder, app));

    [Fact]
    public void Leftovers_find_the_folder_of_an_uninstalled_app()
    {
        var name = "VaptTesteSobra" + Guid.NewGuid().ToString("N")[..6];
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.ini"), "x");
        try
        {
            var app = new InstalledApp(name, null, "1.0", 0, null, null, null, null, null, false, null);
            var found = Leftovers.Find(app);
            Assert.Contains(found, l => l.Kind == LeftoverKind.Folder && l.Path.Equals(dir, StringComparison.OrdinalIgnoreCase));

            // Pasta de um programa ainda instalado nunca entra.
            var installed = new InstalledApp("Outro", null, null, 0, null, null, null, dir, null, false, null);
            Assert.DoesNotContain(Leftovers.Find(app, [app, installed]), l => l.Path.Equals(dir, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Winget_table_is_parsed_in_portuguese()
    {
        const string output = "   - \r  \\ \r\nNome                 ID                    Versão   Disponível Origem\n" +
            "-----------------------------------------------------------------------\n" +
            "AutoHotkey           AutoHotkey.AutoHotkey 2.0.26   2.0.31     winget\n" +
            "Git                  Git.Git               2.56.0.3 2.56.0.4   winget\n" +
            "\n2 atualizações disponíveis.\n";
        var list = WingetUpdates.Parse(output);
        Assert.Equal(2, list.Count);
        Assert.Equal("AutoHotkey.AutoHotkey", list[0].Id);
        Assert.Equal("2.0.31", list[0].Available);
        Assert.Equal("Git", list[1].Name);
    }

    [Fact]
    public void Power_plans_are_parsed_with_active_flag()
    {
        const string output = "GUID do Esquema de Energia: 381b4222-f694-41f0-9685-ff5bb260df2e  (Equilibrado)\n" +
            "GUID do Esquema de Energia: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (Alto desempenho) *\n";
        var plans = RepairTools.ParsePowerPlans(output);
        Assert.Equal(2, plans.Count);
        Assert.False(plans[0].Active);
        Assert.True(plans[1].Active);
        Assert.Equal("Alto desempenho", plans[1].Name);
    }

    [Fact]
    public async Task Duplicates_are_found_by_content_not_name()
    {
        var root = Path.Combine(Path.GetTempPath(), "vapt-dupes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "a"));
        var data = new byte[200_000];
        Random.Shared.NextBytes(data);
        File.WriteAllBytes(Path.Combine(root, "foto.jpg"), data);
        File.WriteAllBytes(Path.Combine(root, "a", "copia.jpg"), data);
        var other = (byte[])data.Clone();
        other[^1] ^= 0xFF; // mesmo tamanho e começo, final diferente
        File.WriteAllBytes(Path.Combine(root, "a", "quase.jpg"), other);
        try
        {
            var groups = await DuplicateFinder.FindAsync(root, 1024);
            var g = Assert.Single(groups);
            Assert.Equal(2, g.Files.Count);
            Assert.Equal(200_000, g.Wasted);
            Assert.DoesNotContain(g.Files, f => f.Name == "quase.jpg");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
