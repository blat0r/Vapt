using System.Text.RegularExpressions;

namespace Vapt.Core.Apps;

public sealed record AppUpdate(string Name, string Id, string Version, string Available, string Source);

/// <summary>
/// Atualizações de programas pelo winget (como o UniGetUI). A tabela do winget é traduzida
/// ("Nome/ID/Versão/Disponível"), então a leitura é pela posição das colunas no cabeçalho.
/// </summary>
public static partial class WingetUpdates
{
    public static string? WingetPath
    {
        get
        {
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");
            return File.Exists(local) ? local : null;
        }
    }

    public static bool Available => WingetPath is not null;

    public static async Task<IReadOnlyList<AppUpdate>> ListAsync(CancellationToken cancellation = default)
    {
        if (WingetPath is not { } winget)
        {
            return [];
        }

        var r = await ProcessRunner.RunAsync(winget, "upgrade --accept-source-agreements --disable-interactivity", cancellation: cancellation);
        return Parse(r.Output);
    }

    /// <summary>Lê a tabela do "winget upgrade" (qualquer idioma).</summary>
    internal static List<AppUpdate> Parse(string output)
    {
        // Linhas de progresso usam \r: fica só o último trecho de cada linha.
        var lines = output.Replace("\r\n", "\n").Split('\n').Select(l => l.Split('\r').Last()).ToList();
        var dashes = lines.FindIndex(l => l.Length > 20 && l.Trim().All(c => c == '-'));
        if (dashes < 1)
        {
            return [];
        }

        var header = lines[dashes - 1];
        var starts = HeaderRegex().Matches(header).Select(m => m.Index).ToList();
        if (starts.Count < 4)
        {
            return [];
        }

        string Column(string line, int i)
        {
            var start = starts[i];
            if (start >= line.Length)
            {
                return "";
            }

            var end = i + 1 < starts.Count ? Math.Min(starts[i + 1], line.Length) : line.Length;
            return line[start..end].Trim();
        }

        var result = new List<AppUpdate>();
        for (var i = dashes + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                break; // fim da tabela (depois vem o resumo e a lista de versões desconhecidas)
            }

            if (line.Length < starts[3])
            {
                continue;
            }

            var id = Column(line, 1);
            var available = Column(line, 3);
            if (id.Length == 0 || available.Length == 0 || id.Contains(' '))
            {
                continue;
            }

            result.Add(new AppUpdate(Column(line, 0), id, Column(line, 2), available, starts.Count > 4 ? Column(line, 4) : "winget"));
        }

        return result;
    }

    public static async Task<bool> UpdateAsync(AppUpdate update, Action<string>? onLine = null, CancellationToken cancellation = default)
    {
        if (WingetPath is not { } winget)
        {
            return false;
        }

        var r = await ProcessRunner.RunAsync(
            winget,
            $"upgrade --id \"{update.Id}\" --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity",
            onLine,
            cancellation);
        Log.Info($"winget upgrade {update.Id}: código {r.ExitCode}");
        return r.Ok;
    }

    [GeneratedRegex(@"\S+")]
    private static partial Regex HeaderRegex();
}
