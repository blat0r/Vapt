using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vapt.Core;
using Vapt.Core.Hardware;
using Vapt.Core.Net;

namespace Vapt.Pages;

public sealed partial class NetworkPage : Page
{
    private CancellationTokenSource? _cts;

    public NetworkPage()
    {
        InitializeComponent();
        Unloaded += (_, _) => _cts?.Cancel();
    }

    private static Style S(string key) => (Style)Application.Current.Resources[key];

    private static Brush B(string key) => (Brush)Application.Current.Resources[key];

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (_cts is { IsCancellationRequested: false })
        {
            _cts.Cancel();
            return;
        }

        _cts = new CancellationTokenSource();
        RunText.Text = "Parar";
        Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;
        SpeedTiles.Visibility = Visibility.Collapsed;
        Details.Children.Clear();
        try
        {
            var progress = new Progress<(string Stage, double Fraction)>(p =>
            {
                VerdictText.Text = p.Stage;
                Progress.Value = p.Fraction;
            });
            Show(await NetworkTest.RunAsync(progress, _cts.Token));
        }
        catch (OperationCanceledException)
        {
            VerdictText.Text = "Teste cancelado";
        }
        catch (Exception ex)
        {
            Log.Error("Teste de rede.", ex);
            VerdictText.Text = "O teste falhou";
            AdapterText.Text = ex.Message;
        }
        finally
        {
            Progress.Visibility = Visibility.Collapsed;
            RunText.Text = "Testar de novo";
            _cts = null;
        }
    }

    private void Show(NetworkResult r)
    {
        VerdictText.Text = r.Title;
        VerdictText.Foreground = B(r.Verdict switch
        {
            Health.Good => "SystemFillColorSuccessBrush",
            Health.Warning => "SystemFillColorCautionBrush",
            _ => "SystemFillColorCriticalBrush",
        });
        AdapterText.Text = r.Adapter.Length > 0 ? $"Conectado por {r.Adapter}" : "";

        // Números grandes.
        SpeedTiles.Children.Clear();
        var best = r.Internet.Where(p => p.Answered).OrderBy(p => p.Avg).FirstOrDefault();
        var tiles = new (string Title, string Value, string Sub)[]
        {
            ("Download", r.DownloadMbps is { } d ? $"{d:N0} Mb/s" : "—", "baixar"),
            ("Upload", r.UploadMbps is { } u ? $"{u:N0} Mb/s" : "—", "enviar e fazer live"),
            ("Ping", best is not null ? $"{best.Avg:0} ms" : "—", best is not null ? $"oscilação {best.Jitter:0} ms" : ""),
            ("Ping com a linha cheia", r.LoadedPingMs is { } l ? $"{l:0} ms" : "—", "durante o download"),
        };
        for (var i = 0; i < tiles.Length; i++)
        {
            var panel = new StackPanel { Spacing = 2 };
            panel.Children.Add(new TextBlock { Text = tiles[i].Title, Style = S("CaptionTextBlockStyle"), Foreground = B("TextFillColorSecondaryBrush") });
            panel.Children.Add(new TextBlock { Text = tiles[i].Value, Style = S("SubtitleTextBlockStyle") });
            panel.Children.Add(new TextBlock { Text = tiles[i].Sub, Style = S("CaptionTextBlockStyle"), Foreground = B("TextFillColorTertiaryBrush") });
            var border = new Border { Style = S("CardBorderStyle"), Child = panel };
            Grid.SetColumn(border, i);
            SpeedTiles.Children.Add(border);
        }

        SpeedTiles.Visibility = Visibility.Visible;

        // O que significa.
        Details.Children.Add(new TextBlock { Text = "Diagnóstico", Style = S("SectionHeaderStyle") });
        foreach (var reason in r.Reasons)
        {
            var good = r.Verdict == Health.Good;
            var card = Ui.Card(reason, null, good ? "" : "", null);
            Details.Children.Add(card);
        }

        Details.Children.Add(new TextBlock { Text = "Detalhes", Style = S("SectionHeaderStyle") });
        foreach (var p in new[] { r.Router }.Concat(r.Internet).Where(p => p is not null))
        {
            var text = p!.Answered
                ? $"média {p.Avg:0.#} ms · mín {p.Min:0} · máx {p.Max:0} · oscilação {p.Jitter:0.#} ms · perda {p.LossPercent:0.#}%"
                : "não respondeu (alguns roteadores bloqueiam ping)";
            Details.Children.Add(Ui.Card($"{p.Name} ({p.Address})", text, p.Name == "Roteador" ? "" : "", null));
        }

        if (r.DnsMs is { } dns)
        {
            Details.Children.Add(Ui.Card("Tempo de DNS", $"{dns:0} ms para achar um endereço novo", "", null));
        }
    }
}
