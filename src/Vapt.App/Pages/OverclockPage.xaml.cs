using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vapt.Core;
using Vapt.Core.Hardware;
using Vapt.Core.Perf;

namespace Vapt.Pages;

public sealed partial class OverclockPage : Page
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private NvidiaGpu? _gpu;
    private bool _sliderReady;

    public OverclockPage()
    {
        InitializeComponent();
        var admin = Elevation.IsAdministrator;
        if (!admin)
        {
            Body.Children.Insert(1, Ui.AdminBar("oc", "Mudar o limite de energia e o clock da placa de vídeo precisa do Vapt aberto como administrador."));
        }

        PowerCard.IsEnabled = admin;
        ClockCard.IsEnabled = admin;

        if (!Nvml.Available || Nvml.Read().Count == 0)
        {
            NvidiaPanel.Visibility = Visibility.Collapsed;
            NoNvidia.Visibility = Visibility.Visible;
        }

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => RefreshGpu();
        Loaded += async (_, _) =>
        {
            RefreshGpu();
            _timer.Start();
            await BuildGuideAsync();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    private void Show(InfoBarSeverity severity, string message) => Ui.ShowStatus(StatusBar, severity, message);

    private void RefreshGpu()
    {
        if (NvidiaPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        _gpu = Nvml.Read().FirstOrDefault();
        if (_gpu is not { } g)
        {
            return;
        }

        GpuName.Text = g.Name;
        CoreText.Text = $"{g.CoreMHz} MHz (máx {g.MaxCoreMHz})";
        MemText.Text = $"{g.MemMHz} MHz";
        PowerText.Text = $"{g.PowerW:0} W de {g.PowerLimitW:0} W";
        TempText.Text = $"{g.TempC} °C · ventoinha {g.FanPercent}%";
        CapText.Text = g.Throttle;

        if (!_sliderReady && g.PowerMaxW > 0)
        {
            PowerSlider.Minimum = Math.Floor(g.PowerMinW);
            PowerSlider.Maximum = Math.Ceiling(g.PowerMaxW);
            PowerSlider.Value = Math.Round(g.PowerLimitW);
            ClockMin.Value = 210;
            ClockMax.Value = g.MaxCoreMHz > 0 ? g.MaxCoreMHz : 1800;
            ClockMax.Maximum = g.MaxCoreMHz > 0 ? g.MaxCoreMHz : 3500;
            _sliderReady = true;
            OnPowerSlider(PowerSlider, null!);
        }
    }

    private void OnPowerSlider(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_gpu is not { } g || g.PowerDefaultW <= 0)
        {
            return;
        }

        var pct = PowerSlider.Value / g.PowerDefaultW * 100;
        PowerValue.Text = $"{PowerSlider.Value:0} W ({pct:0}% do padrão {g.PowerDefaultW:0} W)";
    }

    private void OnApplyPower(object sender, RoutedEventArgs e)
    {
        try
        {
            Nvml.SetPowerLimit(0, PowerSlider.Value);
            Show(InfoBarSeverity.Success, $"Limite de energia em {PowerSlider.Value:0} W. Rode \"Verificar estabilidade\" para conferir.");
        }
        catch (InvalidOperationException ex)
        {
            Show(InfoBarSeverity.Error, ex.Message);
        }
    }

    private void OnApplyClock(object sender, RoutedEventArgs e)
    {
        var min = (uint)Math.Max(200, ClockMin.Value);
        var max = (uint)Math.Max(min + 15, ClockMax.Value);
        try
        {
            Nvml.LockCoreClock(0, min, max);
            Show(InfoBarSeverity.Success, $"Núcleo travado entre {min} e {max} MHz. Restaure quando quiser voltar ao normal.");
        }
        catch (InvalidOperationException ex)
        {
            Show(InfoBarSeverity.Error, ex.Message);
        }
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        try
        {
            Nvml.Reset(0);
            _sliderReady = false;
            RefreshGpu();
            Show(InfoBarSeverity.Success, "Placa de vídeo de volta ao padrão de fábrica.");
        }
        catch (InvalidOperationException ex)
        {
            Show(InfoBarSeverity.Error, ex.Message);
        }
    }

    private void OnStability(object sender, RoutedEventArgs e) => App.MainWindow?.NavigateTo("stress");

    /// <summary>Guia de BIOS conforme o processador e a memória deste PC.</summary>
    private async Task BuildGuideAsync()
    {
        var cpu = SystemInfo.CpuName;
        var report = await SystemReportBuilder.BuildAsync();
        var rated = report.RamModules.Count > 0 ? report.RamModules.Max(m => m.SpeedMts) : 0;
        var running = report.RamModules.Count > 0 ? report.RamModules.Max(m => m.ConfiguredMts) : 0;
        var board = report.Board.FirstOrDefault(i => i.Label == "Placa-mãe")?.Value ?? "";

        void Tip(string title, string text, string glyph) => Guide.Children.Add(Ui.Card(title, text, glyph, null));

        if (rated > running && running > 0)
        {
            Tip("Ligue o XMP/EXPO da memória (o ganho mais fácil)", $"Sua RAM aguenta {rated} MT/s mas está rodando a {running} MT/s. Na BIOS ({board}), ative o perfil XMP (Intel) ou EXPO/DOCP (AMD). É seguro e garantido pelo fabricante da memória.", "");
        }
        else if (running > 0)
        {
            Tip("Memória já na velocidade nominal", $"Sua RAM roda a {running} MT/s, o que ela promete. Ir além é overclock manual de memória (avançado).", "");
        }

        if (cpu.Contains("Ryzen", StringComparison.OrdinalIgnoreCase))
        {
            Tip("Ryzen: Curve Optimizer (undervolt por núcleo)", "Na BIOS: PBO → Curve Optimizer → All Cores → Negativo 10 a 20. O processador usa menos tensão, esquenta menos e mantém o boost por mais tempo. Teste com o Stress test; se travar, diminua o valor.", "");
            Tip("Ryzen: PBO (Precision Boost Overdrive)", "PBO em \"Enabled\" deixa o processador puxar mais energia quando a refrigeração aguenta. Ganho de 3 a 8% em multi-core, com mais calor. Bom com cooler decente.", "");
        }
        else if (System.Text.RegularExpressions.Regex.IsMatch(cpu, @"\d{4,5}K", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            Tip("Intel K: multiplicador e undervolt", "Seu processador é desbloqueado (K). Na BIOS, suba o multiplicador de 1 em 1 e teste; ou faça undervolt (offset negativo de tensão) para esquentar menos. Desligue limites de energia só com cooler forte.", "");
        }
        else if (cpu.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            Tip("Intel sem K: sem overclock", "Esse processador é travado. O que dá para fazer: memória no XMP, plano de energia Alto desempenho (em Reparar) e uma refrigeração melhor para manter o turbo.", "");
        }

        Tip("Depois de qualquer mudança", "Rode o Stress test por 15 a 30 minutos. Travou, reiniciou ou apareceu erro de hardware = volte um passo.", "");
    }
}
