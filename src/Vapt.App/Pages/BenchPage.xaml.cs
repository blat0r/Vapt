using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vapt.Core;
using Vapt.Core.Perf;

namespace Vapt.Pages;

public sealed partial class BenchPage : Page
{
    private const string CpuMulti = "CPU multi-core", CpuSingle = "CPU single-core";
    private CancellationTokenSource? _cpuCts;
    private CancellationTokenSource? _gpuCts;

    public BenchPage()
    {
        InitializeComponent();
        ShowBests();
        ShowHistory();
        Unloaded += (_, _) =>
        {
            _cpuCts?.Cancel();
            _gpuCts?.Cancel();
        };
    }

    private void ShowBests()
    {
        CpuMultiBest.Text = BenchHistory.Best(CpuMulti) is { } m ? $"recorde {m.Score:N0}" : "";
        CpuSingleBest.Text = BenchHistory.Best(CpuSingle) is { } s ? $"recorde {s.Score:N0}" : "";
    }

    private void ShowHistory()
    {
        History.Children.Clear();
        var all = BenchHistory.All();
        if (all.Count == 0)
        {
            History.Children.Add(new TextBlock { Text = "Nenhum teste ainda.", Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
            return;
        }

        foreach (var e in all.Take(12))
        {
            var score = new TextBlock { Text = $"{e.Score:N0}", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center };
            History.Children.Add(Ui.Card(e.Test, $"{e.When:dd/MM/yyyy HH:mm} · {e.Detail}", e.Test.StartsWith("CPU") ? "" : "", score));
        }
    }

    private async void OnCpu(object sender, RoutedEventArgs e)
    {
        if (_cpuCts is { IsCancellationRequested: false })
        {
            _cpuCts.Cancel();
            return;
        }

        _cpuCts = new CancellationTokenSource();
        CpuButtonText.Text = "Parar";
        CpuProgress.Visibility = Visibility.Visible;
        CpuMultiScore.Text = CpuSingleScore.Text = "…";
        try
        {
            var multi = await CpuWork.BenchAsync(true, new Progress<double>(p => CpuProgress.Value = p * 0.7), _cpuCts.Token);
            CpuMultiScore.Text = $"{multi.Score:N0}";
            var single = await CpuWork.BenchAsync(false, new Progress<double>(p => CpuProgress.Value = 0.7 + p * 0.3), _cpuCts.Token);
            CpuSingleScore.Text = $"{single.Score:N0}";
            var name = SystemInfo.CpuName;
            BenchHistory.Add(new BenchEntry(CpuMulti, multi.Score, $"{name} · {multi.Threads} threads · {multi.Seconds:0.0} s", DateTime.Now));
            BenchHistory.Add(new BenchEntry(CpuSingle, single.Score, $"{name} · {single.Seconds:0.0} s", DateTime.Now));
            ShowBests();
            ShowHistory();
        }
        catch (OperationCanceledException)
        {
            CpuMultiScore.Text = CpuSingleScore.Text = "—";
        }
        finally
        {
            CpuProgress.Visibility = Visibility.Collapsed;
            CpuButtonText.Text = "Testar processador";
            _cpuCts = null;
        }
    }

    private async void OnGpu(object sender, RoutedEventArgs e)
    {
        if (_gpuCts is { IsCancellationRequested: false })
        {
            _gpuCts.Cancel();
            return;
        }

        var (w, h, label) = GpuRes.SelectedIndex switch
        {
            0 => (1280, 720, "720p"),
            2 => (2560, 1440, "1440p"),
            _ => (1920, 1080, "1080p"),
        };
        var seconds = GpuTime.SelectedIndex == 1 ? 60 : 30;
        _gpuCts = new CancellationTokenSource();
        GpuButtonText.Text = "Parar";
        GpuProgress.Visibility = Visibility.Visible;
        GpuScore.Text = "…";
        GpuDetail.Text = "Uma janela abre com a cena. Não mexa no PC durante o teste.";
        try
        {
            var r = await GpuRenderer.RunAsync(GpuMode.Benchmark, w, h, TimeSpan.FromSeconds(seconds),
                new Progress<(double Fraction, double Fps)>(p => GpuProgress.Value = p.Fraction), _gpuCts.Token);
            if (!r.Completed)
            {
                GpuScore.Text = "—";
                GpuDetail.Text = "Teste interrompido (a janela foi fechada).";
                return;
            }

            GpuLabel.Text = $"Nota em {label}";
            GpuScore.Text = $"{r.Score:N0}";
            GpuDetail.Text = $"{r.AvgFps:0.0} FPS médios · mínimo {r.MinFps:0} · {r.Adapter}";
            BenchHistory.Add(new BenchEntry($"GPU 3D {label}", r.Score, $"{r.Adapter} · {r.AvgFps:0.0} FPS · {seconds} s", DateTime.Now));
            ShowHistory();
        }
        catch (OperationCanceledException)
        {
            GpuScore.Text = "—";
        }
        catch (Exception ex)
        {
            GpuScore.Text = "—";
            GpuDetail.Text = "O teste não abriu: " + ex.Message;
        }
        finally
        {
            GpuProgress.Visibility = Visibility.Collapsed;
            GpuButtonText.Text = "Testar placa de vídeo";
            _gpuCts = null;
        }
    }
}
