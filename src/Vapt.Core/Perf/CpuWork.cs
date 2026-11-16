using System.Diagnostics;
using System.Numerics;
using System.Runtime.Intrinsics;

namespace Vapt.Core.Perf;

public sealed record CpuBenchResult(bool MultiCore, int Threads, double Score, double Seconds, long Samples, DateTime When);

/// <summary>
/// Carga de CPU: stress test (todas as threads em contas de ponto flutuante e inteiros, que é o
/// que mais esquenta) e benchmark (um ray tracer próprio, no princípio do Cinebench: a nota é
/// amostras por segundo, determinística porque as sementes são fixas por pixel).
/// </summary>
public static class CpuWork
{
    // ── Stress ───────────────────────────────────────────────────────────

    /// <summary>Roda até cancelar. Devolve quantas threads foram usadas.</summary>
    public static int StartStress(CancellationToken ct, int threads = 0)
    {
        threads = threads > 0 ? threads : Environment.ProcessorCount;
        for (var t = 0; t < threads; t++)
        {
            var seed = t;
            new Thread(() => Burn(seed, ct)) { IsBackground = true, Name = $"Vapt.Stress.{t}", Priority = ThreadPriority.Normal }.Start();
        }

        Log.Info($"Stress de CPU: {threads} threads.");
        return threads;
    }

    private static void Burn(int seed, CancellationToken ct)
    {
        var a = Vector256.Create(1.0001 + seed * 1e-6);
        var b = Vector256.Create(0.9999);
        var c = Vector256.Create(1e-9);
        var x = Vector256.Create(1.0);
        var y = Vector256.Create(2.0);
        ulong h = (ulong)seed * 0x9E3779B97F4A7C15UL;
        while (!ct.IsCancellationRequested)
        {
            for (var i = 0; i < 1_000_000; i++)
            {
                // Cadeias independentes de FMA ocupam as unidades vetoriais (AVX).
                x = Vector256.FusedMultiplyAdd(x, a, c);
                y = Vector256.FusedMultiplyAdd(y, b, c);
                h ^= h << 13;
                h ^= h >> 7;
                h ^= h << 17;
            }

            // Mantém os valores numa faixa estável (sem virar infinito ou zero).
            if (x[0] > 1e6 || y[0] < 1e-6)
            {
                x = Vector256.Create(1.0);
                y = Vector256.Create(2.0);
            }
        }

        GC.KeepAlive(h);
    }

    // ── Benchmark (ray tracer) ───────────────────────────────────────────

    private const int Spp = 16; // amostras por pixel

    /// <summary>Multi-core: 960×540 em todas as threads. Single-core: 240×135 numa thread.</summary>
    public static Task<CpuBenchResult> BenchAsync(bool multiCore, IProgress<double>? progress, CancellationToken ct) =>
        Task.Run(() => Bench(multiCore, progress, ct), ct);

    private static CpuBenchResult Bench(bool multi, IProgress<double>? progress, CancellationToken ct)
    {
        var (w, h) = multi ? (960, 540) : (240, 135);
        var threads = multi ? Environment.ProcessorCount : 1;
        const int tile = 16;
        var tilesX = (w + tile - 1) / tile;
        var tilesY = (h + tile - 1) / tile;
        var total = tilesX * tilesY;
        var next = -1;
        var done = 0;
        long samples = 0;
        var sw = Stopwatch.StartNew();
        var workers = Enumerable.Range(0, threads).Select(worker => new Thread(() =>
        {
            long local = 0;
            int t;
            while ((t = Interlocked.Increment(ref next)) < total && !ct.IsCancellationRequested)
            {
                var tx = t % tilesX * tile;
                var ty = t / tilesX * tile;
                for (var py = ty; py < Math.Min(ty + tile, h); py++)
                {
                    for (var px = tx; px < Math.Min(tx + tile, w); px++)
                    {
                        var rng = (uint)(py * 9781 + px * 6271 + 1) | 1u;
                        for (var s = 0; s < Spp; s++)
                        {
                            var u = (px + Rand(ref rng)) / w * 2 - 1;
                            var v = (py + Rand(ref rng)) / h * 2 - 1;
                            _ = Trace(new Vector3(0, 1, -4.5f), Vector3.Normalize(new Vector3(u * 1.7778f, -v, 1.6f)), ref rng);
                            local++;
                        }
                    }
                }

                progress?.Report(Interlocked.Increment(ref done) / (double)total);
            }

            Interlocked.Add(ref samples, local);
        }) { IsBackground = true, Priority = ThreadPriority.AboveNormal }).ToList();
        workers.ForEach(x => x.Start());
        workers.ForEach(x => x.Join());
        ct.ThrowIfCancellationRequested();
        var seconds = sw.Elapsed.TotalSeconds;
        var score = samples / seconds / 1000.0;
        Log.Info($"Benchmark de CPU ({(multi ? "multi" : "single")}): {score:0} pontos em {seconds:0.0} s.");
        return new CpuBenchResult(multi, threads, score, seconds, samples, DateTime.Now);
    }

    private static float Rand(ref uint s)
    {
        s ^= s << 13;
        s ^= s >> 17;
        s ^= s << 5;
        return (s & 0xFFFFFF) / 16777216f;
    }

    private readonly record struct Sphere(Vector3 C, float R, Vector3 Albedo, float Metal, float Emit);

    private static readonly Sphere[] Scene =
    [
        new(new Vector3(0, -1000, 0), 1000, new Vector3(0.75f), 0, 0),
        new(new Vector3(0, 1, 0), 1, new Vector3(0.9f, 0.9f, 0.9f), 0.95f, 0),
        new(new Vector3(-2.2f, 0.8f, 0.6f), 0.8f, new Vector3(0.8f, 0.3f, 0.2f), 0, 0),
        new(new Vector3(2.1f, 0.7f, 0.2f), 0.7f, new Vector3(0.2f, 0.4f, 0.85f), 0.4f, 0),
        new(new Vector3(-0.6f, 0.35f, -1.6f), 0.35f, new Vector3(0.9f, 0.8f, 0.3f), 0, 0),
        new(new Vector3(1.2f, 0.3f, -1.5f), 0.3f, new Vector3(0.3f, 0.8f, 0.4f), 0.8f, 0),
        new(new Vector3(0, 7, -2), 2.5f, Vector3.One, 0, 6),
    ];

    /// <summary>Path tracing simples: difuso com luz, metal com reflexo, até 5 rebatidas.</summary>
    private static float Trace(Vector3 origin, Vector3 dir, ref uint rng)
    {
        var throughput = 1f;
        var light = 0f;
        for (var depth = 0; depth < 5; depth++)
        {
            var best = float.MaxValue;
            var hit = -1;
            for (var i = 0; i < Scene.Length; i++)
            {
                var oc = origin - Scene[i].C;
                var b = Vector3.Dot(oc, dir);
                var c = oc.LengthSquared() - Scene[i].R * Scene[i].R;
                var disc = b * b - c;
                if (disc <= 0)
                {
                    continue;
                }

                var t = -b - MathF.Sqrt(disc);
                if (t > 1e-3f && t < best)
                {
                    best = t;
                    hit = i;
                }
            }

            if (hit < 0)
            {
                return light + throughput * (0.35f + 0.4f * MathF.Max(0, dir.Y)); // céu
            }

            var s = Scene[hit];
            if (s.Emit > 0)
            {
                return light + throughput * s.Emit;
            }

            var p = origin + dir * best;
            var n = Vector3.Normalize(p - s.C);
            var albedo = (s.Albedo.X + s.Albedo.Y + s.Albedo.Z) / 3;
            if (Rand(ref rng) < s.Metal)
            {
                dir = Vector3.Normalize(Vector3.Reflect(dir, n) + 0.05f * RandomUnit(ref rng));
            }
            else
            {
                dir = Vector3.Normalize(n + RandomUnit(ref rng));
            }

            throughput *= albedo;
            origin = p;
        }

        return light;
    }

    private static Vector3 RandomUnit(ref uint rng)
    {
        var z = Rand(ref rng) * 2 - 1;
        var a = Rand(ref rng) * MathF.Tau;
        var r = MathF.Sqrt(1 - z * z);
        return new Vector3(r * MathF.Cos(a), r * MathF.Sin(a), z);
    }
}
