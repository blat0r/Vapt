namespace Vapt.Core.Disk;

public sealed record DiskStats(IReadOnlyList<(FileCategory Category, long Bytes)> ByCategory, IReadOnlyList<DiskNode> Largest);

public static class DiskStatsBuilder
{
    /// <summary>Soma por tipo e os N maiores arquivos abaixo de uma pasta (sem recursão na pilha).</summary>
    public static DiskStats Compute(DiskNode root, int top = 20, CancellationToken cancellation = default)
    {
        var totals = new long[Enum.GetValues<FileCategory>().Length];
        var heap = new PriorityQueue<DiskNode, long>();
        var stack = new Stack<DiskNode>();
        stack.Push(root);
        var visited = 0;
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            foreach (var child in node.Children)
            {
                if (child.IsDirectory)
                {
                    stack.Push(child);
                    continue;
                }

                var size = child.Size;
                totals[(int)FileCategories.Of(child)] += size;
                if (heap.Count < top)
                {
                    heap.Enqueue(child, size);
                }
                else if (heap.TryPeek(out _, out var smallest) && size > smallest)
                {
                    heap.DequeueEnqueue(child, size);
                }
            }

            if ((++visited & 0xFFF) == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }
        }

        var largest = new List<DiskNode>(heap.Count);
        while (heap.Count > 0)
        {
            largest.Add(heap.Dequeue());
        }

        largest.Reverse();
        var byCategory = Enum.GetValues<FileCategory>()
            .Select(c => (c, totals[(int)c]))
            .Where(t => t.Item2 > 0)
            .OrderByDescending(t => t.Item2)
            .ToList();
        return new DiskStats(byCategory, largest);
    }
}
