using System.Security.Cryptography;

namespace Vapt.Core.Files;

public sealed record DuplicateGroup(long Size, string Hash, IReadOnlyList<FileInfo> Files)
{
    /// <summary>Espaço que volta se ficar só uma cópia.</summary>
    public long Wasted => Size * (Files.Count - 1);
}

public sealed record DuplicateProgress(string Stage, long Files, double Fraction);

/// <summary>
/// Acha arquivos duplicados (como o CCleaner e o dupeGuru) em três passos baratos→caros:
/// mesmo tamanho → mesmo começo (64 KB) → mesmo SHA-256 do arquivo inteiro. Nunca segue
/// junções e pula pastas do sistema.
/// </summary>
public static class DuplicateFinder
{
    private const int PrefixBytes = 64 * 1024;

    private static readonly string[] SkipFolders =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"),
    ];

    public static async Task<IReadOnlyList<DuplicateGroup>> FindAsync(
        string root,
        long minSize,
        IProgress<DuplicateProgress>? progress = null,
        CancellationToken cancellation = default) => await Task.Run(() => Find(root, minSize, progress, cancellation), cancellation);

    private static List<DuplicateGroup> Find(string root, long minSize, IProgress<DuplicateProgress>? progress, CancellationToken cancellation)
    {
        // 1) Agrupa por tamanho.
        var bySize = new Dictionary<long, List<FileInfo>>();
        long count = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // Arquivos só na nuvem (OneDrive) ficam de fora: ler o conteúdo faria o Windows baixá-los.
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Offline
                | (FileAttributes)0x40000 /* RECALL_ON_OPEN */ | (FileAttributes)0x400000 /* RECALL_ON_DATA_ACCESS */,
        };
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
        {
            cancellation.ThrowIfCancellationRequested();
            if (SkipFolders.Any(s => file.FullName.StartsWith(s + "\\", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            long length;
            try
            {
                length = file.Length;
            }
            catch (IOException)
            {
                continue;
            }

            if (length < minSize)
            {
                continue;
            }

            if (!bySize.TryGetValue(length, out var list))
            {
                bySize[length] = list = [];
            }

            list.Add(file);
            if (++count % 2000 == 0)
            {
                progress?.Report(new DuplicateProgress("Listando arquivos…", count, 0));
            }
        }

        var candidates = bySize.Where(kv => kv.Value.Count > 1).ToList();
        var totalBytes = Math.Max(1, candidates.Sum(kv => kv.Key * kv.Value.Count));
        long hashedBytes = 0;
        var groups = new List<DuplicateGroup>();

        foreach (var (size, files) in candidates.OrderByDescending(kv => kv.Key))
        {
            cancellation.ThrowIfCancellationRequested();

            // 2) Mesmo começo.
            var byPrefix = files.GroupBy(f => SafeHash(f, PrefixBytes)).Where(g => g.Key is not null && g.Count() > 1);
            foreach (var prefixGroup in byPrefix)
            {
                // 3) Arquivo inteiro (arquivos pequenos já foram lidos por inteiro no passo 2).
                List<IGrouping<string?, FileInfo>> full = size <= PrefixBytes
                    ? [prefixGroup]
                    : prefixGroup.GroupBy(f => SafeHash(f, long.MaxValue)).Where(g => g.Key is not null && g.Count() > 1).ToList();
                foreach (var g in full)
                {
                    groups.Add(new DuplicateGroup(size, g.Key!, g.OrderBy(f => f.FullName.Length).ThenBy(f => f.LastWriteTimeUtc).ToList()));
                }
            }

            hashedBytes += size * files.Count;
            progress?.Report(new DuplicateProgress("Comparando conteúdo…", count, hashedBytes / (double)totalBytes));
        }

        return groups.OrderByDescending(g => g.Wasted).ToList();
    }

    private static string? SafeHash(FileInfo file, long maxBytes)
    {
        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
            if (maxBytes >= file.Length)
            {
                return Convert.ToHexString(SHA256.HashData(stream));
            }

            var buffer = new byte[maxBytes];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
