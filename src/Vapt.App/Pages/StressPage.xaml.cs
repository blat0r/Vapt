using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vapt.Core;
using Vapt.Core.Perf;

namespace Vapt.Pages;

public sealed partial class StressPage : Page
{
    private readonly List<(float? Cpu, float? Gpu)> _history = [];
    private readonly List<string> _log = [];
    private CancellationTokenSource? _cts;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
    private DateTime _started;
    private float _cpuMax, _gpuMax;
    private double _fps;

    public StressPage()
    {
        InitializeComponent();
        if (!Elevation.IsAdministrator)
        {
            Body.Children.Insert(1, Ui.AdminBar("stress", "Sem administrador não dá para ler a temperatura do processador, então o limite de segurança da CPU não funciona. Abra como administrador antes de testar a CPU."));
        }

        Unloaded += (_, _) => Stop("Teste parado (saiu da página).");
    }

    /// <summary>Usado pela página de overclock para a verificação de estabilidade.</summary>
    public static TimeSpan[] Durations { get; } = [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60)];

    private void AddLog(string line)
    {
        _log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        LogText.Text = string.Join("\n", _log.TakeLast(6));
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_cts is not null)
        {
            Stop("Teste parado por você.");
            return;
        }

        var cpu = CpuToggle.IsOn;
        var gpu = GpuToggle.IsOn;
        if (!cpu && !gpu)
        {
            return;
        }

        var duration = Durations[Math.Max(0, Duration.SelectedIndex)];
        _cts = new CancellationTokenSource();
        _history.Clear();
        _log.Clear();
        _cpuMax = _gpuMax = 0;
        _fps = 0;
        _started = DateTime.Now;
        ResultBar.IsOpen = false;
        LivePanel.Visibility = Visibility.Visible;
        StartText.Text = "Parar";
        SensorHub.Acquire();

        if (cpu)
        {
            var threads = CpuWork.StartStress(_cts.Token);
            AddLog($"Processador: carga em {threads} threads.");
        }

        if (gpu)
        {
            AddLog("Placa de vídeo: janela de carga aberta (ESC fecha).");
            var token = _cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    var r = await GpuRenderer.RunAsync(GpuMode.Stress, 1280, 720, duration + TimeSpan.FromMinutes(1),
                        new Progress<(double Fraction, double Fps)>(p => _fps = p.Fps), token);
                    if (!r.Completed && !token.IsCancellationRequested)
                    {
                        DispatcherQueue.TryEnqueue(() => Stop("A janela da GPU foi fechada."));
                    }
                }
                catch (Exception ex)
                {
                    DispatcherQueue.TryEnqueue(() => Stop("A carga de GPU falhou: " + ex.Message));
                }
            });
        }

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => Tick(duration);
        _timer.Start();
        await Task.CompletedTask;
    }

    private void Tick(TimeSpan duration)
    {
        var elapsed = DateTime.Now - _started;
        ElapsedText.Text = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00} de {duration.TotalMinutes:0} min";
        var s = SensorHub.Summary;
        var cpuTemp = s?.CpuTemp;
        var gpuTemp = s?.GpuTemp;
        if (cpuTemp is { } ct)
        {
            _cpuMax = Math.Max(_cpuMax, ct);
        }

        if (gpuTemp is { } gt)
        {
            _gpuMax = Math.Max(_gpuMax, gt);
        }

        CpuNow.Text = cpuTemp is { } c ? $"{c:0} °C · {s!.CpuLoad:0}%" : CpuToggle.IsOn ? "sem leitura" : "—";
        CpuMaxText.Text = _cpuMax > 0 ? $"máx {_cpuMax:0} °C{(s?.CpuPower is { } cp ? $" · {cp:0} W" : "")}" : "";
        GpuNow.Text = gpuTemp is { } g ? $"{g:0} °C · {s!.GpuLoad:0}%" : "—";
        GpuMaxText.Text = _gpuMax > 0 ? $"máx {_gpuMax:0} °C{(s?.GpuPower is { } gp ? $" · {gp:0} W" : "")}" : "";
        FpsText.Text = GpuToggle.IsOn && _fps > 0 ? $"{_fps:0}" : "—";

        _history.Add((cpuTemp, gpuTemp));
        if (_history.Count > 300)
        {
            _history.RemoveAt(0);
        }

        DrawChart();

        // Limites de segurança.
        var cpuLimit = 85 + 5 * CpuLimit.SelectedIndex;
        var gpuLimit = 80 + 5 * GpuLimit.SelectedIndex;
        if (CpuToggle.IsOn && cpuTemp >= cpuLimit)
        {
            Stop($"O processador chegou a {cpuTemp:0} °C (limite {cpuLimit} °C). Refrigeração no limite: confira o cooler e a pasta térmica.", warning: true);
        }
        else if (GpuToggle.IsOn && gpuTemp >= gpuLimit)
        {
            Stop($"A placa de vídeo chegou a {gpuTemp:0} °C (limite {gpuLimit} °C). Confira as ventoinhas e o fluxo de ar do gabinete.", warning: true);
        }
        else if (elapsed >= duration)
        {
            Stop("Tempo completo.");
        }
    }

    private void OnChartSize(object sender, SizeChangedEventArgs e) => DrawChart();

    private void DrawChart()
    {
        var w = Chart.ActualWidth;
        var h = Chart.ActualHeight;
        if (w < 10 || _history.Count < 2)
        {
            return;
        }

        Microsoft.UI.Xaml.Media.PointCollection Points(Func<(float? Cpu, float? Gpu), float?> pick)
        {
            var pts = new Microsoft.UI.Xaml.Media.PointCollection();
            for (var i = 0; i < _history.Count; i++)
            {
                if (pick(_history[i]) is { } t)
                {
                    pts.Add(new Windows.Foundation.Point(w * i / 299.0, h - Math.Clamp((t - 30) / 70.0, 0, 1) * h));
                }
            }

            return pts;
        }

        CpuLine.Points = Points(x => x.Cpu);
        GpuLine.Points = Points(x => x.Gpu);
    }

    private async void Stop(string reason, bool warning = false)
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();
        _cts = null;
        _timer?.Stop();
        SensorHub.Release();
        StartText.Text = "Começar";
        AddLog(reason);

        // Erros de hardware que o Windows registrou durante o teste.
        var errors = await HardwareErrors.SinceAsync(_started);
        var minutes = (DateTime.Now - _started).TotalMinutes;
        ResultBar.IsOpen = true;
        if (errors.Count > 0)
        {
            ResultBar.Severity = InfoBarSeverity.Error;
            ResultBar.Title = $"Instável: {errors.Count} erro(s) de hardware durante o teste";
            ResultBar.Message = string.Join("\n", errors.Take(3).Select(x => $"{x.When:HH:mm:ss} · {x.Meaning}"));
        }
        else if (warning)
        {
            ResultBar.Severity = InfoBarSeverity.Warning;
            ResultBar.Title = "Parou pelo limite de temperatura";
            ResultBar.Message = reason;
        }
        else
        {
            ResultBar.Severity = minutes >= 5 ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
            ResultBar.Title = minutes >= 5 ? $"Estável por {minutes:0} minutos" : "Teste curto demais para concluir";
            ResultBar.Message = $"Nenhum erro de hardware no log do Windows. Temperatura máxima: CPU {(_cpuMax > 0 ? $"{_cpuMax:0} °C" : "—")}, GPU {(_gpuMax > 0 ? $"{_gpuMax:0} °C" : "—")}.";
        }

        Log.Info($"Stress test: {reason} {errors.Count} erros, CPU máx {_cpuMax:0}, GPU máx {_gpuMax:0}.");
    }
}
