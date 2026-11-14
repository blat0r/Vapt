using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vapt.Controls;
using Vapt.Core;
using Vapt.Core.Startup;

namespace Vapt.Pages;

public sealed partial class HomePage : Page
{
    /// <summary>O último check-up vale alguns minutos (voltar à página não refaz tudo).</summary>
    private static HealthReport? _report;
    private static DateTime _reportAt;
    private static readonly HashSet<string> Resolved = [];

    private readonly SystemInfo.CpuMeter _cpu = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private readonly Dictionary<string, TextBlock> _tileStats = [];
    private int _score;
    private bool _busy;

    public HomePage()
    {
        InitializeComponent();
        var hour = DateTime.Now.Hour;
        Greeting.Text = (hour < 12 ? "Bom dia" : hour < 18 ? "Boa tarde" : "Boa noite") + "!";
        MachineText.Text = $"{Environment.MachineName} · {SystemInfo.WindowsVersion}";
        CpuName.Text = SystemInfo.CpuName;

        AddTile(0, 0, "cleanup", "Limpeza", "", "Temporários, caches e Lixeira");
        AddTile(0, 1, "tweaks", "Otimizações", "", "Interface, sistema e jogos");
        AddTile(0, 2, "debloat", "Debloat", "", "Apps, anúncios, privacidade e IA");
        AddTile(0, 3, "startup", "Inicialização", "", "O que abre com o Windows");
        AddTile(1, 0, "apps", "Aplicativos", "", "Desinstalar sem rastro e atualizar");
        AddTile(1, 1, "disk", "Espaço em disco", "", "Mapa do que ocupa espaço");
        AddTile(1, 2, "usb", "Pendrive", "", "Formatar e criar pendrive de boot");
        AddTile(1, 3, "repair", "Reparar", "", "SFC, DISM, rede, DNS e energia");

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1.5);
        _timer.Tick += (_, _) => UpdateMeters();
        Loaded += async (_, _) =>
        {
            UpdateMeters();
            _timer.Start();
            FillTileStats();
            if (_report is not null && DateTime.UtcNow - _reportAt < TimeSpan.FromMinutes(5))
            {
                ShowReport(_report, animate: false);
            }
            else
            {
                await RunCheckupAsync();
            }
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    // ── Check-up ─────────────────────────────────────────────────────────

    private async void OnRescan(object sender, RoutedEventArgs e) => await RunCheckupAsync();

    private async Task RunCheckupAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        Resolved.Clear();
        FixAllButton.IsEnabled = false;
        RescanButton.IsEnabled = false;
        ScoreRing.IsIndeterminate = true;
        ScoreText.Text = "";
        ScoreCaption.Text = "";
        VerdictText.Text = "Analisando seu PC…";
        HeroText.Text = "Olhando arquivos inúteis, apps pré-instalados, inicialização, privacidade, Recall e espaço em disco.";
        FindingsPanel.Children.Clear();
        try
        {
            _report = await Task.Run(() => HealthCheck.RunAsync());
            _reportAt = DateTime.UtcNow;
            ShowReport(_report, animate: true);
        }
        catch (Exception ex)
        {
            Log.Error("Check-up.", ex);
            VerdictText.Text = "O check-up não terminou";
            HeroText.Text = ex.Message;
            ScoreRing.IsIndeterminate = false;
        }
        finally
        {
            _busy = false;
            RescanButton.IsEnabled = true;
        }
    }

    private void ShowReport(HealthReport report, bool animate)
    {
        var open = report.Findings.Where(f => !Resolved.Contains(f.Id)).ToList();
        _score = Math.Clamp(100 - open.Sum(f => f.Weight), 0, 100);
        SetScore(_score, animate);

        var fixable = open.Where(f => f.SafeForFixAll && f.Fix is not null).ToList();
        FixAllButton.IsEnabled = fixable.Count > 0;
        FixAllText.Text = fixable.Count > 0 ? "Vapt vupt: corrigir tudo" : "Tudo em dia";
        HeroText.Text = open.Count == 0
            ? "Não achei nada para corrigir. Volte de vez em quando ou depois de instalar programas."
            : fixable.Count > 0
                ? $"Encontrei {open.Count} {(open.Count == 1 ? "ponto" : "pontos")} de melhoria. \"Corrigir tudo\" resolve na hora o que é seguro: {string.Join(", ", fixable.Select(f => FixAllName(f.Id)))}."
                : $"Encontrei {open.Count} {(open.Count == 1 ? "ponto" : "pontos")} de melhoria. Cada um mostra o que fazer abaixo.";

        FindingsPanel.Children.Clear();
        if (report.Findings.Count > 0)
        {
            FindingsPanel.Children.Add(new TextBlock { Text = "O que o check-up encontrou", Style = (Style)Application.Current.Resources["SectionHeaderStyle"] });
        }

        foreach (var finding in report.Findings)
        {
            FindingsPanel.Children.Add(BuildFindingCard(finding));
        }

        if (report.Findings.FirstOrDefault(f => f.Id == "junk") is { } junk)
        {
            _tileStats["cleanup"].Text = Resolved.Contains("junk") ? "Tudo limpo" : junk.Title.Split(" de ")[0] + " para limpar";
        }
        else
        {
            _tileStats["cleanup"].Text = "Tudo limpo";
        }

        _tileStats["debloat"].Text = report.Findings.FirstOrDefault(f => f.Id == "bloat") is { } bloat ? bloat.Title.Replace(" instalados", "").Replace(" instalado", "") : "Nada de bloatware";
    }

    private static string FixAllName(string id) => id switch
    {
        "junk" => "limpar o lixo",
        "privacy" => "ligar a privacidade básica",
        "storage-sense" => "ligar o Sensor de Armazenamento",
        _ => id,
    };

    private void SetScore(int score, bool animate)
    {
        ScoreRing.IsIndeterminate = false;
        ScoreCaption.Text = "de 100";
        var verdict = new HealthReport(score, []).Verdict;
        VerdictText.Text = verdict;
        if (!animate)
        {
            ScoreRing.Value = score;
            ScoreText.Text = score.ToString();
            return;
        }

        // Anel e número sobem juntos (ease-out), 700 ms.
        var start = ScoreRing.Value;
        var began = DateTime.UtcNow;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.Tick += (t, _) =>
        {
            var p = Math.Min(1, (DateTime.UtcNow - began).TotalMilliseconds / 700);
            var e = 1 - Math.Pow(1 - p, 3);
            var v = start + (score - start) * e;
            ScoreRing.Value = v;
            ScoreText.Text = ((int)Math.Round(v)).ToString();
            if (p >= 1)
            {
                t.Stop();
            }
        };
        timer.Start();
    }

    private FrameworkElement BuildFindingCard(Finding finding)
    {
        var resolved = Resolved.Contains(finding.Id);
        var card = Ui.Card(finding.Title, finding.Detail, finding.Glyph, null);
        if (resolved)
        {
            card.Content = Done("Resolvido");
            return card;
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (finding.Level == FindingLevel.Important)
        {
            actions.Children.Add(Badge("Importante", "SystemFillColorCautionBackgroundBrush"));
        }

        var canFix = finding.Fix is not null && (!finding.NeedsAdmin || Elevation.IsAdministrator);
        if (canFix)
        {
            var fix = new Button { Content = finding.FixLabel ?? "Corrigir", Style = (Style)Application.Current.Resources["AccentButtonStyle"], MinWidth = 96 };
            fix.Click += async (_, _) => await FixOneAsync(finding, card);
            actions.Children.Add(fix);
        }

        if (finding.Page is not null)
        {
            var label = canFix ? "Detalhes" : finding.NeedsAdmin && !Elevation.IsAdministrator && finding.Fix is null && finding.FixLabel is "Desligar" ? "Abrir" : finding.FixLabel ?? "Abrir";
            var go = new Button { Content = label, MinWidth = 96 };
            go.Click += (_, _) => App.MainWindow?.NavigateTo(finding.Page);
            actions.Children.Add(go);
        }

        card.Content = actions;
        return card;
    }

    private async Task FixOneAsync(Finding finding, SettingsCard card)
    {
        card.Content = new ProgressRing { IsActive = true, Width = 22, Height = 22 };
        try
        {
            if (finding.NeedsAdmin)
            {
                await TweakList.EnsureRestorePointAsync();
            }

            var result = await finding.Fix!(CancellationToken.None);
            Resolved.Add(finding.Id);
            card.Content = Done(result);
            Log.Info($"Check-up: {finding.Id} corrigido ({result}).");
        }
        catch (Exception ex)
        {
            Log.Error($"Check-up: corrigir {finding.Id}.", ex);
            card.Content = new TextBlock { Text = "Falhou: " + ex.Message, TextWrapping = TextWrapping.Wrap, MaxWidth = 260, Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] };
        }

        if (_report is not null)
        {
            var open = _report.Findings.Where(f => !Resolved.Contains(f.Id)).ToList();
            var score = Math.Clamp(100 - open.Sum(f => f.Weight), 0, 100);
            SetScore(score, animate: true);
            var fixable = open.Count(f => f.SafeForFixAll && f.Fix is not null);
            FixAllButton.IsEnabled = fixable > 0;
            FixAllText.Text = fixable > 0 ? "Vapt vupt: corrigir tudo" : "Tudo em dia";
        }

        UpdateMeters();
    }

    private async void OnFixAll(object sender, RoutedEventArgs e)
    {
        if (_report is null)
        {
            return;
        }

        var fixable = _report.Findings.Where(f => f.SafeForFixAll && f.Fix is not null && !Resolved.Contains(f.Id)).ToList();
        FixAllButton.IsEnabled = false;
        RescanButton.IsEnabled = false;
        foreach (var finding in fixable)
        {
            var card = FindingsPanel.Children.OfType<SettingsCard>().FirstOrDefault(c => (string)c.Header == finding.Title);
            if (card is not null)
            {
                await FixOneAsync(finding, card);
            }
        }

        RescanButton.IsEnabled = true;
        var open = _report.Findings.Count(f => !Resolved.Contains(f.Id));
        HeroText.Text = open == 0
            ? "Pronto! Tudo que o check-up encontrou foi resolvido."
            : $"Pronto! Sobrou {open} {(open == 1 ? "ponto que precisa" : "pontos que precisam")} da sua decisão: veja abaixo.";
    }

    private static FrameworkElement Done(string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new FontIcon { Glyph = "", FontSize = 16, Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"] });
        panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        return panel;
    }

    private static Border Badge(string text, string brush) => new()
    {
        Padding = new Thickness(8, 2, 8, 3),
        CornerRadius = new CornerRadius(10),
        VerticalAlignment = VerticalAlignment.Center,
        Background = (Brush)Application.Current.Resources[brush],
        Child = new TextBlock { Text = text, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] },
    };

    // ── Ferramentas e medidores ──────────────────────────────────────────

    private void AddTile(int row, int column, string tag, string title, string glyph, string description)
    {
        var stat = new TextBlock
        {
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
            Margin = new Thickness(0, 6, 0, 0),
        };
        _tileStats[tag] = stat;
        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) });
        content.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        content.Children.Add(new TextBlock
        {
            Text = description,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(stat);
        var button = new Button { Content = content, Style = (Style)Resources["TileStyle"] };
        button.Click += (_, _) => App.MainWindow?.NavigateTo(tag);
        Grid.SetRow(button, row);
        Grid.SetColumn(button, column);
        Tiles.Children.Add(button);
    }

    private async void FillTileStats()
    {
        try
        {
            var startup = await Task.Run(StartupManager.List);
            _tileStats["startup"].Text = $"{startup.Count(s => s.Enabled)} ligados";
            var apps = await Task.Run(Core.Apps.InstalledApps.ListDesktop);
            _tileStats["apps"].Text = $"{apps.Count} programas";
            var applied = 0;
            foreach (var t in Core.Tweaks.TweakCatalog.All.Where(t => t.IsAppliedCustom is null))
            {
                applied += await Core.Tweaks.TweakEngine.IsAppliedAsync(t) ? 1 : 0;
            }

            _tileStats["tweaks"].Text = $"{applied} ativas";
        }
        catch (Exception ex)
        {
            Log.Warn("Resumo da tela inicial: " + ex.Message);
        }
    }

    private void UpdateMeters()
    {
        var cpu = _cpu.Sample();
        CpuValue.Text = $"{cpu:P0}";
        CpuBar.Value = cpu * 100;

        var (total, available) = SystemInfo.Memory();
        if (total > 0)
        {
            var used = total - available;
            RamValue.Text = $"{used / (double)total:P0}";
            RamBar.Value = used * 100.0 / total;
            RamDetail.Text = $"{Bytes.Format(used)} de {Bytes.Format(total)} em uso";
        }

        try
        {
            var drive = SystemInfo.SystemDrive;
            var usedDisk = drive.TotalSize - drive.TotalFreeSpace;
            DiskTitle.Text = $"Disco do sistema ({drive.Name.TrimEnd('\\')})";
            DiskValue.Text = $"{Bytes.Format(drive.TotalFreeSpace)} livres";
            DiskBar.Value = usedDisk * 100.0 / drive.TotalSize;
            DiskBar.ShowPaused = drive.TotalFreeSpace < drive.TotalSize * 0.1; // amarelo com menos de 10% livre
            DiskDetail.Text = $"{Bytes.Format(usedDisk)} de {Bytes.Format(drive.TotalSize)} em uso";
        }
        catch (IOException)
        {
        }

        var up = SystemInfo.Uptime;
        FooterText.Text = $"Ligado há {(up.TotalDays >= 1 ? $"{(int)up.TotalDays} d " : "")}{up.Hours} h {up.Minutes} min";
    }
}
