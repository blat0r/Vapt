using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vapt.Core;
using Vapt.Core.Cleanup;

namespace Vapt.Pages;

public sealed partial class CleanupPage : Page
{
    private sealed class Row
    {
        public required CleanupItem Item { get; init; }

        public required CheckBox Check { get; init; }

        public required TextBlock Size { get; init; }

        public required SettingsCard Card { get; init; }

        public CleanupScan Scan { get; set; } = new(0, 0);
    }

    private readonly List<Row> _rows = [];
    private CancellationTokenSource? _cts;
    private bool _busy;

    public CleanupPage()
    {
        InitializeComponent();
        var items = CleanupCatalog.Build();
        if (!Elevation.IsAdministrator && items.Any(i => i.NeedsAdmin))
        {
            Body.Children.Insert(1, Ui.AdminBar("cleanup", "Alguns itens (Windows Update, temporários do sistema) só podem ser limpos como administrador."));
        }

        foreach (var item in items)
        {
            AddRow(item);
        }

        DismCard.IsEnabled = Elevation.IsAdministrator;
        Loaded += async (_, _) => await ScanAllAsync();
        Unloaded += (_, _) => _cts?.Cancel();
    }

    private void AddRow(CleanupItem item)
    {
        var locked = item.NeedsAdmin && !Elevation.IsAdministrator;
        var size = new TextBlock
        {
            MinWidth = 80,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Text = locked ? "Admin" : "…",
        };
        var check = new CheckBox
        {
            IsChecked = item.DefaultOn && !locked,
            IsEnabled = !locked,
            MinWidth = 0,
            Margin = new Thickness(8, 0, -8, 0),
        };
        AutomationName(check, item.Name);
        check.Checked += (_, _) => UpdateTotal();
        check.Unchecked += (_, _) => UpdateTotal();

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        content.Children.Add(size);
        content.Children.Add(check);

        var description = item.Description;
        if (item.Risk == CleanupRisk.Caution)
        {
            description += " (desmarcado por padrão)";
        }

        var card = Ui.Card(item.Name, description, item.Glyph, content);
        card.IsEnabled = !locked;
        card.IsClickEnabled = !locked;
        card.IsActionIconVisible = false;
        card.Click += (_, _) => check.IsChecked = check.IsChecked != true;
        ItemsPanel.Children.Add(card);
        _rows.Add(new Row { Item = item, Check = check, Size = size, Card = card });
    }

    private static void AutomationName(DependencyObject element, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);

    private async void OnRescan(object sender, RoutedEventArgs e) => await ScanAllAsync();

    private async Task ScanAllAsync()
    {
        if (_busy)
        {
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetBusy(true, "Analisando o que pode ser apagado com segurança…");
        try
        {
            var tasks = _rows.Where(r => r.Check.IsEnabled).Select(async row =>
            {
                row.Size.Text = "…";
                row.Scan = await CleanupEngine.ScanAsync(row.Item, token);
                row.Size.Text = row.Scan.Bytes > 0 ? Bytes.Format(row.Scan.Bytes) : "Vazio";
            });
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            SetBusy(false, null);
        }

        UpdateTotal();
    }

    private void UpdateTotal()
    {
        if (_busy)
        {
            return;
        }

        var chosen = _rows.Where(r => r.Check.IsChecked == true).ToList();
        var total = chosen.Sum(r => r.Scan.Bytes);
        TotalText.Text = Bytes.Format(total);
        SummaryText.Text = chosen.Count == 0
            ? "Marque o que quer limpar."
            : $"podem ser liberados em {chosen.Count} {(chosen.Count == 1 ? "item" : "itens")}.";
        CleanButton.IsEnabled = total > 0;
    }

    private async void OnClean(object sender, RoutedEventArgs e)
    {
        var chosen = _rows.Where(r => r.Check.IsChecked == true && r.Scan.Bytes > 0).ToList();
        if (chosen.Count == 0)
        {
            return;
        }

        if (chosen.Any(r => r.Item.Id == "browser-cache") && BrowsersRunning() is { Count: > 0 } open
            && !await Ui.ConfirmAsync(XamlRoot, "Navegador aberto", $"{string.Join(", ", open)} está aberto. Parte do cache fica em uso e será pulada. Feche o navegador para limpar tudo, ou continue assim mesmo.", "Continuar"))
        {
            return;
        }

        _cts = new CancellationTokenSource();
        SetBusy(true, "Limpando…");
        long freed = 0, skipped = 0;
        try
        {
            foreach (var row in chosen)
            {
                row.Size.Text = "Limpando…";
                var result = await CleanupEngine.CleanAsync(row.Item, _cts.Token);
                freed += result.FreedBytes;
                skipped += result.Skipped;
                row.Size.Text = "Limpo";
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false, null);
        }

        await ScanAllAsync();
        TotalText.Text = Bytes.Format(freed);
        SummaryText.Text = skipped > 0
            ? $"liberados. {skipped:N0} arquivos em uso foram pulados (normal: o programa dono ainda está aberto)."
            : "liberados. Tudo limpo!";
    }

    private static List<string> BrowsersRunning()
    {
        var names = new Dictionary<string, string>
        {
            ["chrome"] = "Chrome",
            ["msedge"] = "Edge",
            ["brave"] = "Brave",
            ["opera"] = "Opera",
            ["vivaldi"] = "Vivaldi",
            ["firefox"] = "Firefox",
        };
        return names.Where(n => System.Diagnostics.Process.GetProcessesByName(n.Key).Length > 0).Select(n => n.Value).ToList();
    }

    private void SetBusy(bool busy, string? text)
    {
        _busy = busy;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CleanButton.IsEnabled = !busy;
        RescanButton.IsEnabled = !busy;
        if (busy && text is not null)
        {
            TotalText.Text = "—";
            SummaryText.Text = text;
        }
    }

    private async void OnDism(object sender, RoutedEventArgs e)
    {
        if (!await Ui.ConfirmAsync(XamlRoot, "Limpar componentes do Windows?", "O DISM vai remover versões antigas de componentes já substituídas por atualizações. Leva de alguns minutos a meia hora. Atualizações instaladas continuam podendo ser desinstaladas.", "Executar"))
        {
            return;
        }

        DismButton.IsEnabled = false;
        var progress = new ProgressBar { Width = 140, Maximum = 100 };
        DismCard.Content = progress;
        var result = await CleanupEngine.ComponentCleanupAsync(line =>
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"([0-9]+(?:\.[0-9]+)?)%");
            if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var pct))
            {
                DispatcherQueue.TryEnqueue(() => progress.Value = pct);
            }
        }, CancellationToken.None);
        DismCard.Content = new TextBlock { Text = result.Ok ? "Concluído" : "Falhou (veja o log)", VerticalAlignment = VerticalAlignment.Center };
        if (!result.Ok)
        {
            Log.Error("DISM StartComponentCleanup: " + result.Output + result.Error);
        }
    }
}
