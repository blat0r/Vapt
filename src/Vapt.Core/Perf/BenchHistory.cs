using System.Text.Json;

namespace Vapt.Core.Perf;

public sealed record BenchEntry(string Test, double Score, string Detail, DateTime When);

/// <summary>Últimos resultados dos benchmarks (%LOCALAPPDATA%\Vapt\bench.json), para comparar antes e depois.</summary>
public static class BenchHistory
{
    private static string File => Path.Combine(AppPaths.DataDirectory, "bench.json");

    public static IReadOnlyList<BenchEntry> All()
    {
        try
        {
            return System.IO.File.Exists(File) ? JsonSerializer.Deserialize<List<BenchEntry>>(System.IO.File.ReadAllText(File)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return [];
        }
    }

    public static void Add(BenchEntry entry)
    {
        var list = All().Append(entry).OrderByDescending(e => e.When).Take(30).ToList();
        Directory.CreateDirectory(AppPaths.DataDirectory);
        System.IO.File.WriteAllText(File, JsonSerializer.Serialize(list));
    }

    public static BenchEntry? Best(string test) => All().Where(e => e.Test == test).MaxBy(e => e.Score);
}
