using System.Globalization;
using System.Text.RegularExpressions;

namespace Vapt.Core.Disk;

/// <summary>
/// Filtro no estilo do SpaceSniffer. Termos separados por ";" (todos precisam bater):
/// <list type="bullet">
/// <item><c>*.mp4</c>, <c>foto*</c>: nome com curingas (* e ?). Vários com "," = qualquer um.</item>
/// <item><c>&gt;1gb</c>, <c>&lt;500mb</c>: tamanho (b, kb, mb, gb, tb).</item>
/// <item><c>&gt;2anos</c>, <c>&lt;30dias</c>: idade pela data de modificação (dias, semanas, meses, anos; também em inglês).</item>
/// <item><c>|termo</c>: exclui o que bate com o termo.</item>
/// </list>
/// Só arquivos são filtrados; pastas ficam visíveis como contêineres.
/// </summary>
public sealed partial class DiskFilter
{
    private readonly List<Func<DiskNode, bool>> _terms = [];

    private DiskFilter()
    {
    }

    public bool IsEmpty => _terms.Count == 0;

    public bool Matches(DiskNode node) => node.Kind != NodeKind.File || _terms.All(t => t(node));

    public static DiskFilter Parse(string? text, DateTime? now = null)
    {
        var filter = new DiskFilter();
        if (string.IsNullOrWhiteSpace(text))
        {
            return filter;
        }

        var today = now ?? DateTime.UtcNow;
        foreach (var raw in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var negate = raw.StartsWith('|');
            var term = negate ? raw[1..].Trim() : raw;
            if (term.Length == 0)
            {
                continue;
            }

            var predicate = ParseTerm(term, today);
            if (predicate is null)
            {
                continue;
            }

            filter._terms.Add(negate ? n => !predicate(n) : predicate);
        }

        return filter;
    }

    private static Func<DiskNode, bool>? ParseTerm(string term, DateTime now)
    {
        if (term[0] is '>' or '<')
        {
            var greater = term[0] == '>';
            var m = QuantityRegex().Match(term[1..].Trim());
            if (!m.Success)
            {
                return null;
            }

            var number = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            var unit = m.Groups[2].Value.ToLowerInvariant();
            if (SizeUnit(unit) is { } multiplier)
            {
                var bytes = (long)(number * multiplier);
                return greater ? n => n.Size > bytes : n => n.Size < bytes;
            }

            if (AgeUnit(unit) is { } days)
            {
                var limit = now.AddDays(-number * days);
                // ">2anos" = mais velho que 2 anos (modificado antes do limite).
                return greater ? n => n.Modified < limit : n => n.Modified > limit;
            }

            return null;
        }

        var patterns = term.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(WildcardToRegex)
            .ToArray();
        return n => patterns.Any(p => p.IsMatch(n.Name));
    }

    private static long? SizeUnit(string unit) => unit switch
    {
        "" or "b" => 1,
        "k" or "kb" => 1L << 10,
        "m" or "mb" => 1L << 20,
        "g" or "gb" => 1L << 30,
        "t" or "tb" => 1L << 40,
        _ => null,
    };

    private static double? AgeUnit(string unit) => unit switch
    {
        "d" or "dia" or "dias" or "day" or "days" => 1,
        "sem" or "semana" or "semanas" or "w" or "week" or "weeks" => 7,
        "mes" or "mês" or "meses" or "month" or "months" => 30.44,
        "a" or "ano" or "anos" or "y" or "year" or "years" => 365.25,
        _ => null,
    };

    private static Regex WildcardToRegex(string pattern)
    {
        // Sem curinga: procura o texto em qualquer parte do nome.
        if (!pattern.Contains('*') && !pattern.Contains('?'))
        {
            pattern = "*" + pattern + "*";
        }

        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    [GeneratedRegex(@"^([0-9]+(?:[.,][0-9]+)?)\s*([a-zA-Zçê]*)$")]
    private static partial Regex QuantityRegex();
}
