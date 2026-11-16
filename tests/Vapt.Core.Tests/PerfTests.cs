using Vapt.Core.Perf;

namespace Vapt.Core.Tests;

public class PerfTests
{
    [Fact]
    public void Summary_picks_dedicated_gpu_and_cpu_package()
    {
        var r = new List<SensorReading>
        {
            new("AMD Ryzen 5 5600G", "Processador", "Core (Tctl/Tdie)", SensorKind.Temperature, 61, 40, 70),
            new("AMD Ryzen 5 5600G", "Processador", "CPU Total", SensorKind.Load, 23, 0, 100),
            new("AMD Ryzen 5 5600G", "Processador", "Core #1", SensorKind.Clock, 4400, 0, 0),
            new("AMD Ryzen 5 5600G", "Processador", "Core #2", SensorKind.Clock, 4200, 0, 0),
            new("AMD Radeon(TM) Graphics", "Placa de vídeo", "GPU Core", SensorKind.Temperature, 50, 0, 0),
            new("AMD Radeon(TM) Graphics", "Placa de vídeo", "GPU Memory Total", SensorKind.Data, 0.5f, 0, 0),
            new("NVIDIA GeForce RTX 3060", "Placa de vídeo", "GPU Core", SensorKind.Temperature, 66, 0, 0),
            new("NVIDIA GeForce RTX 3060", "Placa de vídeo", "GPU Core", SensorKind.Load, 97, 0, 0),
            new("NVIDIA GeForce RTX 3060", "Placa de vídeo", "GPU Memory Total", SensorKind.Data, 12, 0, 0),
            new("NVIDIA GeForce RTX 3060", "Placa de vídeo", "GPU Package", SensorKind.Power, 165, 0, 0),
        };
        var s = SensorHub.Summarize(r);
        Assert.Equal("NVIDIA GeForce RTX 3060", s.GpuName);
        Assert.Equal(66, s.GpuTemp);
        Assert.Equal(165, s.GpuPower);
        Assert.Equal(61, s.CpuTemp);
        Assert.Equal(4300, s.CpuClock);
    }

    [Fact]
    public void Overlay_lines_follow_options()
    {
        var o = new OverlayOptions { Fps = true, FrameTime = true, OnePercentLow = false, Gpu = true, GpuPower = false, Cpu = false };
        var f = new FrameStats(1, "jogo", 143.6, 6.96, 98, [6.9, 7.0]);
        var s = new SensorSummary(60, 30, 4300, 65, "CPU", 66, 75, 97, 1900, 7500, 165, 6, 12, "GPU", 50);
        var lines = GameOverlay.BuildLines(o, f, s, null);
        Assert.Equal(2, lines.Count);
        Assert.Equal("144  7,0 ms", lines[0].Value.Replace(".", ","));
        Assert.Equal("97%  66°", lines[1].Value);
    }

    [Fact]
    public void Nvml_reads_nvidia_card_when_present()
    {
        if (!Nvml.Available)
        {
            return; // sem placa NVIDIA neste PC
        }

        var gpu = Assert.Single(Nvml.Read());
        Assert.Contains("NVIDIA", gpu.Name);
        Assert.True(gpu.TempC is > 10 and < 110);
        Assert.True(gpu.PowerMaxW >= gpu.PowerDefaultW && gpu.PowerDefaultW >= gpu.PowerMinW);
    }

    [Fact]
    public async Task Cpu_bench_single_core_gives_a_score()
    {
        var r = await CpuWork.BenchAsync(multiCore: false, null, CancellationToken.None);
        Assert.True(r.Score > 0);
        Assert.Equal(240 * 135 * 16, r.Samples);
    }

    /// <summary>Abre uma janela por 3 s: só roda com VAPT_GPU_TEST=1.</summary>
    [Fact]
    public async Task Gpu_renderer_draws_frames()
    {
        if (Environment.GetEnvironmentVariable("VAPT_GPU_TEST") != "1")
        {
            return;
        }

        var r = await GpuRenderer.RunAsync(GpuMode.Benchmark, 1920, 1080, TimeSpan.FromSeconds(3), null, CancellationToken.None);
        Assert.True(r.Completed);
        Assert.True(r.AvgFps > 1, $"FPS {r.AvgFps}");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "vapt-gpu-test.txt"), $"{r.Adapter} {r.AvgFps:0.0} FPS score {r.Score:0}");
    }

    [Fact]
    public async Task Sensors_read_without_admin()
    {
        SensorHub.Acquire();
        try
        {
            for (var i = 0; i < 40 && !SensorHub.Ready; i++)
            {
                await Task.Delay(250);
            }

            Assert.True(SensorHub.Ready);
            Assert.NotEmpty(SensorHub.Readings);
        }
        finally
        {
            SensorHub.Release();
        }
    }
}

