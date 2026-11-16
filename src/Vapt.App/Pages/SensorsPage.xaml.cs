using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Vapt.Core;
using Vapt.Core.Perf;

namespace Vapt.Pages;

public sealed partial class SensorsPage : Page
{
    private sealed class Row
    {
        public required Grid Grid { get; init; }

        public required TextBlock Value { get; init; }

        public required TextBlock Min { get; init; }

        public required TextBlock Max { get; init; }

        public required SensorKind Kind { get; init; }

        public required string Name { get; init; }
    }

    private readonly Dictionary<string, Row> _rows = [];
    private readonly Dictionary<string, StackPanel> _groups = [];
    private readonly Dictionary<string, (TextBlock Value, TextBlock Sub)> _tiles = [];
    private SensorKind? _filter;

    public SensorsPage()
    {
        InitializeComponent();
        BuildBanners();
        BuildTiles();
        BuildFilters();
        Loaded += (_, _) =>
        {
            SensorHub.Updated += OnUpdated;
            SensorHub.Acquire();
            if (SensorHub.Ready)
            {
                Refresh();
            }
        };
        Unloaded += (_, _) =>
        {
            SensorHub.Updated -= OnUpdated;
            SensorHub.Release();
        };
    }

    private static Style S(string key) => (Style)Application.Current.Resources[key];

    private static Brush B(string key) => (Brush)Application.Current.Resources[key];

    private void BuildBanners()
    {
        if (!Elevation.IsAdministrator)
        {
            Header.Children.Insert(2, Ui.AdminBar("sensors", "Temperatura, clock e consumo do processador e da placa-mãe só aparecem com o Vapt aberto como administrador. A placa de vídeo já aparece."));
        }
        else if (!SensorHub.PawnIoInstalled)
        {
            var bar = new InfoBar
            {
                Severity = InfoBarSeverity.Informational,
                Title = "Falta o driver PawnIO para ler o processador",
                Message = "É o driver que os monitores de hardware usam hoje (o antigo WinRing0 é bloqueado pelo Defender). Código aberto, assinado pela Microsoft.",
                IsOpen = true,
                IsClosable = true,
            };
            var install = new Button { Content = "Instalar PawnIO" };
            install.Click += async (_, _) =>
            {
                install.IsEnabled = false;
                install.Content = "Instalando…";
                var ok = await SensorHub.InstallPawnIoAsync();
                bar.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Error;
                bar.Title = ok ? "PawnIO instalado" : "Não deu para instalar";
                bar.Message = ok ? "Feche e abra o Vapt para ver a temperatura e o consumo do processador." : "Tente pelo site oficial: github.com/namazso/PawnIO.";
                bar.ActionButton = null;
            };
            bar.ActionButton = install;
            Header.Children.Insert(2, bar);
        }
    }

    private void BuildTiles()
    {
        var tiles = new (string Key, string Title)[]
        {
            ("cpuTemp", "Processador · temperatura"), ("cpuLoad", "Processador · uso"), ("cpuClock", "Processador · clock médio"), ("cpuPower", "Processador · consumo"),
            ("gpuTemp", "Placa de vídeo · temperatura"), ("gpuLoad", "Placa de vídeo · uso"), ("gpuClock", "Placa de vídeo · clock"), ("gpuPower", "Placa de vídeo · consumo"),
        };
        for (var i = 0; i < tiles.Length; i++)
        {
            var value = new TextBlock { Text = "—", Style = S("SubtitleTextBlockStyle") };
            var sub = new TextBlock { Style = S("CaptionTextBlockStyle"), Foreground = B("TextFillColorTertiaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis };
            var panel = new StackPanel { Spacing = 2 };
            panel.Children.Add(new TextBlock { Text = tiles[i].Title, Style = S("CaptionTextBlockStyle"), Foreground = B("TextFillColorSecondaryBrush") });
            panel.Children.Add(value);
            panel.Children.Add(sub);
            var border = new Border { Style = S("CardBorderStyle"), Child = panel, Padding = new Thickness(14, 10, 14, 10) };
            Grid.SetRow(border, i / 4);
            Grid.SetColumn(border, i % 4);
            Tiles.Children.Add(border);
            _tiles[tiles[i].Key] = (value, sub);
        }
    }

    private void BuildFilters()
    {
        var options = new (string Label, SensorKind? Kind)[]
        {
            ("Todos", null), ("Temperatura", SensorKind.Temperature), ("Clock", SensorKind.Clock), ("Uso", SensorKind.Load),
            ("Consumo", SensorKind.Power), ("Ventoinhas", SensorKind.Fan), ("Voltagem", SensorKind.Voltage), ("Memória", SensorKind.Data),
        };
        foreach (var (label, kind) in options)
        {
            var chip = new ToggleButton { Content = label, IsChecked = kind is null, Padding = new Thickness(12, 4, 12, 5), CornerRadius = new CornerRadius(14) };
            chip.Click += (_, _) =>
            {
                _filter = kind;
                foreach (var other in Filters.Children.OfType<ToggleButton>())
                {
                    other.IsChecked = ReferenceEquals(other, chip);
                }

                ApplyFilter();
            };
            Filters.Children.Add(chip);
        }
    }

    /// <summary>A roda do mouse rola os filtros para o lado quando eles não cabem na largura.</summary>
    private void OnFilterWheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (FilterScroll.ScrollableWidth <= 0)
        {
            return;
        }

        var delta = e.GetCurrentPoint(FilterScroll).Properties.MouseWheelDelta;
        FilterScroll.ChangeView(FilterScroll.HorizontalOffset - delta, null, null, disableAnimation: false);
        e.Handled = true;
    }

    private void OnUpdated() => DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh()
    {
        Ring.IsActive = false;
        Ring.Visibility = Visibility.Collapsed;
        var s = SensorHub.Summary;
        if (s is not null)
        {
            SetTile("cpuTemp", s.CpuTemp is { } ct ? $"{ct:0} °C" : "—", s.CpuTemp is null ? (Elevation.IsAdministrator ? "precisa do PawnIO" : "precisa de administrador") : s.CpuName);
            SetTile("cpuLoad", s.CpuLoad is { } cl ? $"{cl:0}%" : "—", s.CpuName);
            SetTile("cpuClock", s.CpuClock is { } cc ? $"{cc / 1000:0.00} GHz" : "—", "média dos núcleos");
            SetTile("cpuPower", s.CpuPower is { } cp ? $"{cp:0} W" : "—", s.CpuPower is null ? "precisa do PawnIO" : "pacote inteiro");
            SetTile("gpuTemp", s.GpuTemp is { } gt ? $"{gt:0} °C" : "—", s.GpuHotspot is { } hs ? $"ponto mais quente {hs:0} °C" : s.GpuName);
            SetTile("gpuLoad", s.GpuLoad is { } gl ? $"{gl:0}%" : "—", s.VramUsedGb is { } vu ? $"VRAM {vu:0.0}{(s.VramTotalGb is { } vt ? $" / {vt:0} GB" : " GB")}" : s.GpuName);
            SetTile("gpuClock", s.GpuClock is { } gc ? $"{gc:0} MHz" : "—", s.GpuMemClock is { } mc ? $"memória {mc:0} MHz" : "");
            SetTile("gpuPower", s.GpuPower is { } gp ? $"{gp:0} W" : "—", s.GpuName);
        }

        foreach (var r in SensorHub.Readings)
        {
            var key = $"{r.Hardware}|{r.Kind}|{r.Name}";
            if (!_rows.TryGetValue(key, out var row))
            {
                row = AddRow(r);
                _rows[key] = row;
            }

            row.Value.Text = r.Format(r.Value);
            row.Min.Text = r.Format(r.Min);
            row.Max.Text = r.Format(r.Max);
        }

        ApplyFilter();
    }

    private void SetTile(string key, string value, string sub)
    {
        var (v, s) = _tiles[key];
        v.Text = value;
        s.Text = sub;
    }

    private Row AddRow(SensorReading r)
    {
        if (!_groups.TryGetValue(r.Hardware, out var group))
        {
            group = new StackPanel { Spacing = 2 };
            var head = new Grid { Margin = new Thickness(0, 0, 0, 6), ColumnSpacing = 8 };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foreach (var w in new[] { 110, 90, 90 })
            {
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
            }

            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            title.Children.Add(new TextBlock { Text = r.Hardware, Style = S("BodyStrongTextBlockStyle") });
            title.Children.Add(new TextBlock { Text = r.HardwareKind, Style = S("CaptionTextBlockStyle"), Foreground = B("TextFillColorTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(title);
            var cols = new[] { "Agora", "Mínimo", "Máximo" };
            for (var i = 0; i < cols.Length; i++)
            {
                var t = new TextBlock { Text = cols[i], Style = S("CaptionTextBlockStyle"), Foreground = B("TextFillColorTertiaryBrush"), HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetColumn(t, i + 1);
                head.Children.Add(t);
            }

            group.Children.Add(head);
            Groups.Children.Add(new Border { Style = S("CardBorderStyle"), Child = group });
            _groups[r.Hardware] = group;
        }

        var grid = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var w in new[] { 110, 90, 90 })
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        }

        var icon = r.Kind switch
        {
            SensorKind.Temperature => "",
            SensorKind.Clock => "",
            SensorKind.Load => "",
            SensorKind.Power => "",
            SensorKind.Fan => "",
            SensorKind.Voltage => "",
            _ => "",
        };
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        name.Children.Add(new FontIcon { Glyph = icon, FontSize = 13, Foreground = B("TextFillColorSecondaryBrush") });
        name.Children.Add(new TextBlock { Text = r.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        var value = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, Style = S("BodyStrongTextBlockStyle") };
        var min = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, Foreground = B("TextFillColorSecondaryBrush") };
        var max = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, Foreground = B("TextFillColorSecondaryBrush") };
        Grid.SetColumn(value, 1);
        Grid.SetColumn(min, 2);
        Grid.SetColumn(max, 3);
        grid.Children.Add(name);
        grid.Children.Add(value);
        grid.Children.Add(min);
        grid.Children.Add(max);
        group.Children.Add(grid);
        return new Row { Grid = grid, Value = value, Min = min, Max = max, Kind = r.Kind, Name = r.Name };
    }

    private void OnSearch(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyFilter();

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        foreach (var row in _rows.Values)
        {
            var visible = (_filter is null || row.Kind == _filter) && (query.Length == 0 || row.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase));
            row.Grid.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        // Esconde o cartão da peça que ficou sem nenhum sensor visível.
        foreach (var group in _groups.Values)
        {
            var any = group.Children.Skip(1).Any(c => c.Visibility == Visibility.Visible);
            ((FrameworkElement)group.Parent).Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
