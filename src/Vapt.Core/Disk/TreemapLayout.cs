namespace Vapt.Core.Disk;

public readonly record struct MapRect(double X, double Y, double W, double H)
{
    public double Right => X + W;

    public double Bottom => Y + H;

    public bool Contains(double px, double py) => px >= X && py >= Y && px < X + W && py < Y + H;
}

/// <summary>Um bloco desenhável do mapa.</summary>
public sealed record TreemapItem(DiskNode Node, MapRect Bounds, int Depth, bool ShowsChildren);

public sealed class TreemapOptions
{
    /// <summary>Altura da faixa de título das pastas ("Nome - 12 GB"), como no SpaceSniffer.</summary>
    public double HeaderHeight { get; init; } = 18;

    /// <summary>Espaço entre a borda da pasta e os filhos.</summary>
    public double Padding { get; init; } = 2;

    /// <summary>Lado mínimo (px) para um bloco aparecer.</summary>
    public double MinSide { get; init; } = 3;

    /// <summary>Pastas menores que isso não abrem os filhos (só aparecem como bloco).</summary>
    public double MinOpenSide { get; init; } = 34;

    /// <summary>Profundidade máxima abaixo da raiz mostrada ("nível de detalhe").</summary>
    public int MaxDepth { get; init; } = 32;

    /// <summary>Máximo de filhos por pasta considerados (o resto vira espaço vazio).</summary>
    public int MaxChildren { get; init; } = 1500;

    /// <summary>Mostra o espaço livre do volume na raiz.</summary>
    public long FreeSpace { get; init; }
}

/// <summary>
/// Treemap "squarified" (Bruls, Huizing e van Wijk, 2000) com pastas aninhadas: cada pasta
/// ganha uma faixa de título e os filhos ficam dentro dela, como no SpaceSniffer.
/// </summary>
public static class TreemapLayout
{
    public static List<TreemapItem> Build(DiskNode root, MapRect bounds, TreemapOptions options)
    {
        var items = new List<TreemapItem>(4096);
        Place(root, bounds, 0, options, items, isRoot: true);
        return items;
    }

    private static void Place(DiskNode node, MapRect r, int depth, TreemapOptions o, List<TreemapItem> items, bool isRoot)
    {
        var open = node.IsDirectory
            && depth <= o.MaxDepth
            && r.W >= o.MinOpenSide
            && r.H >= o.HeaderHeight + o.MinOpenSide * 0.6;
        items.Add(new TreemapItem(node, r, depth, open));
        if (!open)
        {
            return;
        }

        var inner = new MapRect(
            r.X + o.Padding,
            r.Y + o.HeaderHeight,
            r.W - 2 * o.Padding,
            r.H - o.HeaderHeight - o.Padding);
        if (inner.W < o.MinSide || inner.H < o.MinSide)
        {
            return;
        }

        // Tamanhos lidos uma vez só: a varredura pode estar mudando a árvore agora.
        var entries = new List<(DiskNode Node, double Size)>();
        foreach (var child in node.Children)
        {
            var size = child.Size;
            if (size > 0)
            {
                entries.Add((child, size));
            }
        }

        if (isRoot && o.FreeSpace > 0)
        {
            entries.Add((new DiskNode("Espaço livre", NodeKind.FreeSpace, node, o.FreeSpace), o.FreeSpace));
        }

        if (entries.Count == 0)
        {
            return;
        }

        entries.Sort((a, b) => b.Size.CompareTo(a.Size));
        var total = 0d;
        foreach (var e in entries)
        {
            total += e.Size;
        }

        // Pasta ainda crescendo: o tamanho dela pode ser maior que a soma dos filhos lidos.
        total = Math.Max(total, node.Size + (isRoot ? o.FreeSpace : 0));
        if (entries.Count > o.MaxChildren)
        {
            entries.RemoveRange(o.MaxChildren, entries.Count - o.MaxChildren);
        }

        var scale = inner.W * inner.H / total;
        var areas = new double[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            areas[i] = entries[i].Size * scale;
        }

        var rects = Squarify(areas, inner);
        for (var i = 0; i < rects.Count; i++)
        {
            var cr = rects[i];
            if (cr.W < o.MinSide || cr.H < o.MinSide)
            {
                continue;
            }

            Place(entries[i].Node, cr, depth + 1, o, items, isRoot: false);
        }
    }

    /// <summary>
    /// Distribui áreas (já em px², ordenadas da maior para a menor) dentro de um retângulo.
    /// A soma pode ser menor que a área do retângulo: o que sobra fica vazio no fim.
    /// </summary>
    public static List<MapRect> Squarify(IReadOnlyList<double> areas, MapRect bounds)
    {
        var result = new List<MapRect>(areas.Count);
        var free = bounds;
        var i = 0;
        while (i < areas.Count && free.W > 0.5 && free.H > 0.5)
        {
            var side = Math.Min(free.W, free.H);
            var rowEnd = i + 1;
            var rowSum = areas[i];
            var worst = Worst(areas, i, rowEnd, rowSum, side);
            while (rowEnd < areas.Count)
            {
                var nextSum = rowSum + areas[rowEnd];
                var nextWorst = Worst(areas, i, rowEnd + 1, nextSum, side);
                if (nextWorst > worst)
                {
                    break;
                }

                worst = nextWorst;
                rowSum = nextSum;
                rowEnd++;
            }

            // A fileira ocupa uma faixa ao longo do lado menor.
            if (free.W >= free.H)
            {
                var stripW = Math.Min(free.W, rowSum / free.H);
                var y = free.Y;
                for (var k = i; k < rowEnd; k++)
                {
                    var h = stripW <= 0 ? 0 : areas[k] / stripW;
                    result.Add(new MapRect(free.X, y, stripW, h));
                    y += h;
                }

                free = new MapRect(free.X + stripW, free.Y, free.W - stripW, free.H);
            }
            else
            {
                var stripH = Math.Min(free.H, rowSum / free.W);
                var x = free.X;
                for (var k = i; k < rowEnd; k++)
                {
                    var w = stripH <= 0 ? 0 : areas[k] / stripH;
                    result.Add(new MapRect(x, free.Y, w, stripH));
                    x += w;
                }

                free = new MapRect(free.X, free.Y + stripH, free.W, free.H - stripH);
            }

            i = rowEnd;
        }

        return result;
    }

    private static double Worst(IReadOnlyList<double> areas, int start, int end, double sum, double side)
    {
        if (sum <= 0)
        {
            return double.MaxValue;
        }

        var max = areas[start];
        var min = areas[end - 1];
        var s2 = sum * sum;
        var w2 = side * side;
        return Math.Max(w2 * max / s2, s2 / (w2 * Math.Max(min, 1e-9)));
    }

    /// <summary>O bloco mais profundo sob o ponto (o último desenhado vence).</summary>
    public static TreemapItem? HitTest(List<TreemapItem> items, double x, double y)
    {
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (items[i].Bounds.Contains(x, y))
            {
                return items[i];
            }
        }

        return null;
    }
}
