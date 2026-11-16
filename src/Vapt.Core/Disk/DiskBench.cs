using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Vapt.Core.Disk;

public sealed record DiskBenchResult(double SeqReadMBs, double SeqWriteMBs, double Rand4kQ1MBs, double Rand4kQ32MBs, string Drive, long FileBytes, DateTime When)
{
    public double Rand4kQ1Iops => Rand4kQ1MBs * 1024 * 1024 / 4096;

    public double Rand4kQ32Iops => Rand4kQ32MBs * 1024 * 1024 / 4096;
}

/// <summary>
/// Teste de disco no estilo CrystalDiskMark: sequencial de 1 MB e
/// aleatório de 4 KB com fila 1 e 32, sem o cache do Windows (FILE_FLAG_NO_BUFFERING), num arquivo
/// temporário que é apagado no fim.
/// </summary>
public static class DiskBench
{
    private const FileOptions NoBuffering = (FileOptions)0x20000000;
    private const int Seq = 1 << 20;
    private const int Rand = 4096;

    public static Task<DiskBenchResult> RunAsync(string driveRoot, long fileBytes, IProgress<(string Stage, double Fraction)>? progress, CancellationToken cancellation) =>
        Task.Run(() => Run(driveRoot, fileBytes, progress, cancellation), cancellation);

    private static unsafe DiskBenchResult Run(string root, long fileBytes, IProgress<(string Stage, double Fraction)>? progress, CancellationToken ct)
    {
        var dir = Path.Combine(root, "VaptBench");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"teste-{Guid.NewGuid():N}.tmp");
        var buffer = (byte*)NativeMemory.AlignedAlloc(Seq, 4096);
        try
        {
            new Span<byte>(buffer, Seq).Fill(0);
            Random.Shared.NextBytes(new Span<byte>(buffer, Seq)); // dado aleatório: SSDs com compressão não trapaceiam

            // 1) Escrita sequencial (também cria o arquivo de teste).
            double seqWrite;
            using (var h = File.OpenHandle(file, FileMode.Create, FileAccess.ReadWrite, FileShare.None, NoBuffering | FileOptions.WriteThrough, fileBytes))
            {
                var sw = Stopwatch.StartNew();
                for (long off = 0; off < fileBytes; off += Seq)
                {
                    ct.ThrowIfCancellationRequested();
                    RandomAccess.Write(h, new ReadOnlySpan<byte>(buffer, Seq), off);
                    progress?.Report(("Escrita sequencial", 0.25 * off / fileBytes));
                }

                seqWrite = fileBytes / sw.Elapsed.TotalSeconds / (1 << 20);
            }

            // 2) Leitura sequencial.
            double seqRead;
            using (var h = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.Read, NoBuffering))
            {
                var sw = Stopwatch.StartNew();
                for (long off = 0; off < fileBytes; off += Seq)
                {
                    ct.ThrowIfCancellationRequested();
                    RandomAccess.Read(h, new Span<byte>(buffer, Seq), off);
                    progress?.Report(("Leitura sequencial", 0.25 + 0.25 * off / fileBytes));
                }

                seqRead = fileBytes / sw.Elapsed.TotalSeconds / (1 << 20);
            }

            // 3) Aleatório 4K, fila 1 (uma leitura por vez, como abrir programas).
            var blocks = fileBytes / Rand;
            double q1;
            using (var h = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.Read, NoBuffering | FileOptions.RandomAccess))
            {
                var rng = new Random(42);
                var sw = Stopwatch.StartNew();
                long ops = 0;
                while (sw.Elapsed.TotalSeconds < 5)
                {
                    ct.ThrowIfCancellationRequested();
                    RandomAccess.Read(h, new Span<byte>(buffer, Rand), rng.NextInt64(blocks) * Rand);
                    ops++;
                    if ((ops & 255) == 0)
                    {
                        progress?.Report(("Aleatório 4K (fila 1)", 0.5 + 0.25 * sw.Elapsed.TotalSeconds / 5));
                    }
                }

                q1 = ops * Rand / sw.Elapsed.TotalSeconds / (1 << 20);
            }

            // 4) Aleatório 4K, fila 32 (32 leituras ao mesmo tempo, onde SSD NVMe brilha).
            double q32;
            using (var h = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.Read, NoBuffering | FileOptions.RandomAccess))
            {
                long ops = 0;
                var sw = Stopwatch.StartNew();
                var threads = Enumerable.Range(0, 32).Select(t => new Thread(() =>
                {
                    var local = (byte*)NativeMemory.AlignedAlloc(Rand, 4096);
                    try
                    {
                        var rng = new Random(t);
                        while (sw.Elapsed.TotalSeconds < 5 && !ct.IsCancellationRequested)
                        {
                            RandomAccess.Read(h, new Span<byte>(local, Rand), rng.NextInt64(blocks) * Rand);
                            Interlocked.Increment(ref ops);
                        }
                    }
                    finally
                    {
                        NativeMemory.AlignedFree(local);
                    }
                }) { IsBackground = true }).ToList();
                threads.ForEach(t => t.Start());
                while (threads.Any(t => t.IsAlive))
                {
                    Thread.Sleep(100);
                    progress?.Report(("Aleatório 4K (fila 32)", 0.75 + 0.25 * Math.Min(1, sw.Elapsed.TotalSeconds / 5)));
                }

                ct.ThrowIfCancellationRequested();
                q32 = Interlocked.Read(ref ops) * Rand / sw.Elapsed.TotalSeconds / (1 << 20);
            }

            progress?.Report(("Pronto", 1));
            var result = new DiskBenchResult(seqRead, seqWrite, q1, q32, root, fileBytes, DateTime.Now);
            Log.Info($"Teste de disco {root}: seq {seqRead:0}/{seqWrite:0} MB/s, 4K Q1 {q1:0.0} MB/s, Q32 {q32:0} MB/s");
            return result;
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
            try
            {
                File.Delete(file);
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Como ler o resultado: comparação com tipos comuns de disco.</summary>
    public static string Verdict(DiskBenchResult r) => r.SeqReadMBs switch
    {
        > 4500 => "Nível de SSD NVMe PCIe 4.0/5.0: o mais rápido que existe para jogos e programas.",
        > 1800 => "Nível de SSD NVMe PCIe 3.0: muito rápido.",
        > 400 => "Nível de SSD SATA: ótimo para o Windows e jogos.",
        > 120 => "Nível de HD ou pendrive rápido: bom para guardar arquivos, lento para o Windows.",
        _ => "Lento: típico de HD antigo, pendrive ou cartão. Evite instalar o Windows ou jogos aqui.",
    };
}
