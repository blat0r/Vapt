using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vapt.Core;
using Vapt.Core.Disk;
using Vapt.Core.Hardware;

namespace Vapt.Pages;

public sealed partial class SystemPage : Page
{
    private static SystemReport? _report;
    private CancellationTokenSource? _benchCts;

    public SystemPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            _report ??= await SystemReportBuilder.BuildAsync();
            Ring.IsActive = false;
            Ring.Visibility = Visibility.Collapsed;
            ExportButton.IsEnabled = true;
            BatteryTab.Visibility = _report.Battery is null ? Visibility.Collapsed : Visibility.Visible;
            BuildSummary(_report);
            BuildDisks(_report);
            BuildBattery(_report);
        };
        Unloaded += (_, _) => _benchCts?.Cancel();
    }

    private static Style S(string key) => (Style)Application.Current.Resources[key];

    private static Brush B(string key) => (Brush)Application.Current.Resources[key];

    private void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var i = sender.Items.IndexOf(sender.SelectedItem);
        SummaryPanel.Visibility = i == 0 ? Visibility.Visible : Visibility.Collapsed;
        DisksPanel.Visibility = i == 1 ? Visibility.Visible : Visibility.Collapsed;
        BatteryPanel.Visibility = i == 2 ? Visibility.Visible : Visibility.Collapsed;
        Scroller.ChangeView(null, 0, null, true);
    }

    /// <summary>Um cartão com título, ícone e linhas "rótulo  valor".</summary>
    private static Border InfoCard(string title, string glyph, IEnumerable<InfoItem> items)
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var row = 0;
        foreach (var item in items.Where(i => !string.IsNullOrWhiteSpace(i.Value)))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = item.Label, Foreground = B("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap };
            var value = new TextBlock { Text = item.Value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetRow(label, row);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
            row++;
        }

        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new FontIcon { Glyph = glyph, FontSize = 18, Foreground = B("AccentTextFillColorPrimaryBrush") });
        head.Children.Add(new TextBlock { Text = title, Style = S("BodyStrongTextBlockStyle") });
        var panel = new StackPanel();
        panel.Children.Add(head);
        panel.Children.Add(grid);
        return new Border { Style = S("CardBorderStyle"), Child = panel };
    }

    private void BuildSummary(SystemReport r)
    {
        SummaryPanel.Children.Clear();
        SummaryPanel.Children.Add(InfoCard("Sistema", "", r.System));
        SummaryPanel.Children.Add(InfoCard("Processador", "", r.Cpu));
        SummaryPanel.Children.Add(InfoCard("Memória RAM", "", r.RamSummary.Concat(r.RamModules.Select(m => new InfoItem(
            m.Slot,
            $"{Bytes.Format(m.Bytes)} {m.Type} {m.FormFactor} · {(m.ConfiguredMts > 0 ? m.ConfiguredMts : m.SpeedMts)} MT/s{(m.Voltage is { } v ? $" · {v:0.00} V" : "")} · {m.Manufacturer} {m.PartNumber}".Trim())))));
        foreach (var g in r.Gpus)
        {
            SummaryPanel.Children.Add(InfoCard("Placa de vídeo", "",
            [
                new("Modelo", g.Name),
                new("Memória", g.VramBytes > 0 ? Bytes.Format(g.VramBytes) : "Compartilhada com a RAM"),
                new("Driver", $"{g.Driver}{(g.DriverDate.Length > 0 ? $" ({g.DriverDate})" : "")}"),
                new("Tela", g.Resolution),
            ]));
        }

        SummaryPanel.Children.Add(InfoCard("Placa-mãe e firmware", "", r.Board));
    }

    // ── Discos ───────────────────────────────────────────────────────────

    private void BuildDisks(SystemReport r)
    {
        DisksPanel.Children.Clear();
        if (!Elevation.IsAdministrator)
        {
            DisksPanel.Children.Add(Ui.AdminBar("system", "Temperatura, desgaste do SSD e horas ligado só aparecem com o Vapt aberto como administrador."));
        }

        foreach (var d in r.Disks)
        {
            var badge = new Border
            {
                Padding = new Thickness(10, 3, 10, 4),
                CornerRadius = new CornerRadius(10),
                VerticalAlignment = VerticalAlignment.Center,
                Background = B(d.Verdict switch
                {
                    Health.Good => "SystemFillColorSuccessBackgroundBrush",
                    Health.Warning => "SystemFillColorCautionBackgroundBrush",
                    Health.Critical => "SystemFillColorCriticalBackgroundBrush",
                    _ => "SystemFillColorNeutralBackgroundBrush",
                }),
                Child = new TextBlock { Text = ReportHtml.Label(d.Verdict), Style = S("CaptionTextBlockStyle") },
            };
            var facts = string.Join(" · ", new[]
            {
                d.Temperature is { } t ? $"{t:0} °C" : null,
                d.WearPercent is { } w ? $"{w:0}% de desgaste" : null,
                d.PowerOnHours is { } h ? $"{h:N0} h ligado" : null,
            }.Where(s => s is not null));
            var expander = new SettingsExpander
            {
                Header = d.Name,
                Description = $"{d.Kind} {d.Bus} · {Bytes.Format(d.Size)}{(facts.Length > 0 ? " · " + facts : "")}",
                HeaderIcon = new FontIcon { Glyph = "\uEDA2" },
                Content = badge,
            };
            expander.Items.Add(new SettingsCard { Header = "Diagnóstico", Description = string.Join(" ", d.Reasons) });
            DisksPanel.Children.Add(expander);
        }

        // Teste de velocidade.
        DisksPanel.Children.Add(new TextBlock { Text = "Teste de velocidade", Style = S("SectionHeaderStyle") });
        var drives = DriveInfo.GetDrives().Where(x => x.IsReady && x.DriveType is DriveType.Fixed or DriveType.Removable).ToList();
        var driveBox = new ComboBox { MinWidth = 140, ItemsSource = drives.Select(x => x.Name.TrimEnd('\\')).ToList(), SelectedIndex = 0 };
        var sizeBox = new ComboBox { MinWidth = 110, ItemsSource = new[] { "512 MB", "1 GB", "4 GB" }, SelectedIndex = 1 };
        var run = new Button { Content = "Testar", Style = S("AccentButtonStyle"), MinWidth = 100 };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        controls.Children.Add(driveBox);
        controls.Children.Add(sizeBox);
        controls.Children.Add(run);
        var card = Ui.Card("Velocidade do disco", "Leitura e escrita sem o cache do Windows, como o CrystalDiskMark. Usa um arquivo temporário que é apagado no fim.", "", controls);
        DisksPanel.Children.Add(card);
        var results = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        DisksPanel.Children.Add(results);

        run.Click += async (_, _) =>
        {
            if (_benchCts is { IsCancellationRequested: false })
            {
                _benchCts.Cancel();
                return;
            }

            var root = drives[Math.Max(0, driveBox.SelectedIndex)].RootDirectory.FullName;
            long size = sizeBox.SelectedIndex switch { 0 => 512L << 20, 2 => 4L << 30, _ => 1L << 30 };
            if (new DriveInfo(root).AvailableFreeSpace < size + (1L << 30))
            {
                await Ui.InfoAsync(XamlRoot, "Pouco espaço", "Essa unidade não tem espaço livre para o arquivo de teste. Escolha um tamanho menor.");
                return;
            }

            _benchCts = new CancellationTokenSource();
            run.Content = "Parar";
            results.Children.Clear();
            var bar = new ProgressBar { Maximum = 1 };
            var stage = new TextBlock { Foreground = B("TextFillColorSecondaryBrush") };
            results.Children.Add(stage);
            results.Children.Add(bar);
            try
            {
                var progress = new Progress<(string Stage, double Fraction)>(p =>
                {
                    stage.Text = p.Stage + "…";
                    bar.Value = p.Fraction;
                });
                var res = await DiskBench.RunAsync(root, size, progress, _benchCts.Token);
                results.Children.Clear();
                results.Children.Add(BenchGrid(res));
                results.Children.Add(new TextBlock { Text = DiskBench.Verdict(res), TextWrapping = TextWrapping.Wrap, Foreground = B("TextFillColorSecondaryBrush") });
            }
            catch (OperationCanceledException)
            {
                results.Children.Clear();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Children.Clear();
                results.Children.Add(new TextBlock { Text = "O teste falhou: " + ex.Message, TextWrapping = TextWrapping.Wrap });
            }
            finally
            {
                run.Content = "Testar";
                _benchCts = null;
            }
        };
    }

    private static Grid BenchGrid(DiskBenchResult r)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        var tiles = new (string Title, string Value, string Sub)[]
        {
            ("Leitura sequencial", $"{r.SeqReadMBs:N0} MB/s", "arquivos grandes"),
            ("Escrita sequencial", $"{r.SeqWriteMBs:N0} MB/s", "copiar para o disco"),
            ("Aleatório 4K", $"{r.Rand4kQ1MBs:N1} MB/s", $"{r.Rand4kQ1Iops:N0} IOPS · abrir programas"),
            ("Aleatório 4K fila 32", $"{r.Rand4kQ32MBs:N0} MB/s", $"{r.Rand4kQ32Iops:N0} IOPS · carga pesada"),
        };
        for (var i = 0; i < tiles.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var panel = new StackPanel { Spacing = 2 };
            panel.Children.Add(new TextBlock { Text = tiles[i].Title, Style = S("CaptionTextBlockStyle"), Foreground = B("TextFillColorSecondaryBrush") });
            panel.Children.Add(new TextBlock { Text = tiles[i].Value, Style = S("SubtitleTextBlockStyle") });
            panel.Children.Add(new TextBlock { Text = tiles[i].Sub, Style = S("CaptionTextBlockStyle"), Foreground = B("TextFillColorTertiaryBrush") });
            var border = new Border { Style = S("CardBorderStyle"), Child = panel };
            Grid.SetColumn(border, i);
            grid.Children.Add(border);
        }

        return grid;
    }

    // ── Bateria ──────────────────────────────────────────────────────────

    private void BuildBattery(SystemReport r)
    {
        BatteryPanel.Children.Clear();
        if (r.Battery is not { } b)
        {
            return;
        }

        var verdict = b.Verdict switch
        {
            Health.Good => "Bateria saudável",
            Health.Warning => "Bateria já gasta",
            _ => "Bateria muito gasta: considere trocar",
        };
        BatteryPanel.Children.Add(InfoCard(verdict, "",
        [
            new("Capacidade hoje", $"{b.FullMWh:N0} mWh"),
            new("Capacidade nova", $"{b.DesignMWh:N0} mWh"),
            new("Desgaste", $"{b.WearPercent:0}% (segura {100 - b.WearPercent:0}% do que segurava nova)"),
            new("Ciclos de carga", b.Cycles?.ToString("N0") ?? "O fabricante não informa"),
            new("Agora", $"{b.ChargePercent}% · {b.State}"),
            new("Modelo", b.Name),
        ]));
    }

    // ── Relatório ────────────────────────────────────────────────────────

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_report is null)
        {
            return;
        }

        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"Relatório do PC {DateTime.Now:yyyy-MM-dd HH-mm}.html");
        try
        {
            await File.WriteAllTextAsync(file, ReportHtml.Build(_report));
            Ui.Open(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Ui.InfoAsync(XamlRoot, "Não foi possível salvar", ex.Message);
        }
    }
}
