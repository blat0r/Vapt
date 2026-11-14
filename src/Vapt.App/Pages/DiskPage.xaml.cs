using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Vapt.Controls;
using Vapt.Core;
using Vapt.Core.Disk;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Vapt.Pages;

public sealed partial class DiskPage : Page
{
    /// <summary>
    /// A análise sobrevive à troca de página: fica aqui, fora da instância.
    /// </summary>
    private static DiskScanner? _scanner;
    private static CancellationTokenSource? _scanCts;
    private static DiskNode? _view;
    private static readonly Stack<DiskNode> Back = new();
    private static readonly Stack<DiskNode> Forward = new();

    private readonly DispatcherQueueTimer _timer;
    private CancellationTokenSource? _statsCts;
    private DiskNode? _selected;

    public DiskPage()
    {
        InitializeComponent();
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(450);
        _timer.Tick += (_, _) => OnTick();

        Map.HoverChanged += (_, node) => ShowHover(node);
        Map.SelectionChanged += (_, node) => ShowSelection(node);
        Map.ZoomRequested += (_, node) => ZoomTo(node, animateFrom: Map.BoundsOf(node));
        Map.ItemContextRequested += (_, e) => ShowContextMenu(e.Node, e.Position);
        Map.BackRequested += (_, _) => OnBack(this, new RoutedEventArgs());
        Map.ForwardRequested += (_, _) => OnForward(this, new RoutedEventArgs());

        AddAccelerator(VirtualKey.Left, VirtualKeyModifiers.Menu, () => OnBack(this, new RoutedEventArgs()));
        AddAccelerator(VirtualKey.Right, VirtualKeyModifiers.Menu, () => OnForward(this, new RoutedEventArgs()));
        AddAccelerator(VirtualKey.Back, VirtualKeyModifiers.None, () => OnUp(this, new RoutedEventArgs()));
        AddAccelerator(VirtualKey.F5, VirtualKeyModifiers.None, () => OnScan(this, new RoutedEventArgs()));

        LoadDrives();
        Loaded += (_, _) => RestoreState();
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            _statsCts?.Cancel();
        };
    }

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers mods, Action action)
    {
        var acc = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = key, Modifiers = mods };
        acc.Invoked += (_, e) =>
        {
            // Backspace dentro do filtro apaga texto, não sobe de pasta.
            if (key == VirtualKey.Back && FilterBox.FocusState != FocusState.Unfocused)
            {
                return;
            }

            e.Handled = true;
            action();
        };
        KeyboardAccelerators.Add(acc);
    }

    private sealed record DriveEntry(string Path, string Label)
    {
        public override string ToString() => Label;
    }

    private void LoadDrives()
    {
        var entries = new List<DriveEntry>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Unknown)
                {
                    continue;
                }

                var name = string.IsNullOrWhiteSpace(d.VolumeLabel)
                    ? (d.DriveType == DriveType.Removable ? "Removível" : "Disco local")
                    : d.VolumeLabel;
                var label = $"{name} ({d.Name.TrimEnd('\\')})  ·  {Bytes.Format(d.TotalFreeSpace)} livres de {Bytes.Format(d.TotalSize)}";
                entries.Add(new DriveEntry(d.RootDirectory.FullName, label));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        DriveBox.ItemsSource = entries;
        var last = App.Settings.LastScanPath;
        DriveBox.SelectedItem = entries.FirstOrDefault(e => string.Equals(e.Path, last, StringComparison.OrdinalIgnoreCase))
            ?? entries.FirstOrDefault(e => e.Path.StartsWith(System.IO.Path.GetPathRoot(Environment.SystemDirectory)!, StringComparison.OrdinalIgnoreCase))
            ?? entries.FirstOrDefault();
    }

    private void RestoreState()
    {
        if (_scanner is null)
        {
            UpdateChrome();
            // "--scan <pasta>": começa a análise direto (atalhos e testes).
            var argv = Environment.GetCommandLineArgs();
            var scan = argv.SkipWhile(a => a != "--scan").Skip(1).FirstOrDefault();
            if (scan is not null && Directory.Exists(scan))
            {
                Log.Info($"Análise pedida na linha de comando: {scan}");
                StartScanSafe(scan);
            }

            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        Map.Root = _view ?? _scanner.Root;
        UpdateChrome();
        if (!_scanner.Finished && _scanCts is { IsCancellationRequested: false })
        {
            SetScanning(true);
            _timer.Start();
        }
        else
        {
            OnTick();
            RefreshStats();
        }
    }

    // ── Varredura ─────────────────────────────────────────────────────────

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        if (_scanCts is { IsCancellationRequested: false } && _scanner is { Finished: false })
        {
            _scanCts.Cancel();
            return;
        }

        if (DriveBox.SelectedItem is DriveEntry drive)
        {
            await StartScan(drive.Path);
        }
    }

    private async void OnPickFolder(object sender, RoutedEventArgs e)
    {
        var folder = NativeDialogs.PickFolder(Ui.WindowHandle, "Escolha a pasta para analisar");
        if (folder is not null)
        {
            await StartScan(folder);
        }
    }

    private async void StartScanSafe(string path)
    {
        try
        {
            await StartScan(path);
        }
        catch (Exception ex)
        {
            Log.Error("Falha na análise.", ex);
        }
    }

    private async Task StartScan(string path)
    {
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        App.Settings.LastScanPath = path;
        App.Settings.Save();

        var scanner = new DiskScanner(path);
        _scanner = scanner;
        _view = scanner.Root;
        Back.Clear();
        Forward.Clear();
        _selected = null;
        ShowSelection(null);

        EmptyState.Visibility = Visibility.Collapsed;
        Map.FreeSpace = 0;
        Map.Root = scanner.Root;
        UpdateChrome();
        SetScanning(true);
        _timer.Start();

        var cts = _scanCts;
        await scanner.RunAsync(cts.Token);
        if (!ReferenceEquals(scanner, _scanner))
        {
            return;
        }

        _timer.Stop();
        SetScanning(false);
        OnTick();
        RefreshStats();
    }

    private void SetScanning(bool scanning)
    {
        ScanProgress.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        ScanProgress.IsIndeterminate = scanning && _scanner is { IsDriveRoot: false };
        ScanIcon.Glyph = scanning ? "" : "";
        ScanText.Text = scanning ? "Parar" : "Analisar";
        ScanButton.Style = (Style)Application.Current.Resources[scanning ? "DefaultButtonStyle" : "AccentButtonStyle"];
    }

    private void OnTick()
    {
        if (_scanner is not { } s)
        {
            return;
        }

        Map.Invalidate();
        UpdateBreadcrumb();
        if (s.Finished)
        {
            StatusText.Text = $"{s.ScannedFiles:N0} arquivos em {s.ScannedDirectories:N0} pastas · {Bytes.Format(s.Root.Size)} · {s.Elapsed.TotalSeconds:0.0} s";

            // A análise pode ter terminado enquanto outra página estava aberta: quem começou
            // já não existe, então esta página fecha o "Analisando…" sozinha.
            if (_timer.IsRunning)
            {
                _timer.Stop();
                SetScanning(false);
                RefreshStats();
            }
        }
        else if (_scanCts is null or { IsCancellationRequested: true })
        {
            // Cancelada.
            _timer.Stop();
            SetScanning(false);
            StatusText.Text = $"Análise interrompida · {s.ScannedFiles:N0} arquivos · {Bytes.Format(s.Root.Size)}";
        }
        else
        {
            var pct = s.IsDriveRoot ? $" · {s.Progress:P0}" : "";
            StatusText.Text = $"Analisando… {s.ScannedFiles:N0} arquivos · {Bytes.Format(s.Root.Size)}{pct}";
            if (s.IsDriveRoot)
            {
                ScanProgress.Value = s.Progress * 100;
            }
        }

        ShowSelection(_selected);
    }

    // ── Navegação ────────────────────────────────────────────────────────

    private void ZoomTo(DiskNode node, Windows.Foundation.Rect? animateFrom = null, bool record = true)
    {
        if (_view is null || ReferenceEquals(node, _view) || !node.IsDirectory)
        {
            return;
        }

        if (record)
        {
            Back.Push(_view);
            Forward.Clear();
        }

        _view = node;
        Map.Root = node;
        Map.AnimateZoomFrom(animateFrom);
        UpdateChrome();
        RefreshStats();
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (Back.Count == 0 || _view is null)
        {
            return;
        }

        Forward.Push(_view);
        ZoomTo(Back.Pop(), record: false);
    }

    private void OnForward(object sender, RoutedEventArgs e)
    {
        if (Forward.Count == 0 || _view is null)
        {
            return;
        }

        Back.Push(_view);
        ZoomTo(Forward.Pop(), record: false);
    }

    private void OnUp(object sender, RoutedEventArgs e)
    {
        if (_view?.Parent is { } parent)
        {
            ZoomTo(parent);
        }
    }

    private void OnHome(object sender, RoutedEventArgs e)
    {
        if (_scanner is not null)
        {
            ZoomTo(_scanner.Root);
        }
    }

    private void UpdateChrome()
    {
        BackButton.IsEnabled = Back.Count > 0;
        ForwardButton.IsEnabled = Forward.Count > 0;
        UpButton.IsEnabled = _view?.Parent is not null;
        HomeButton.IsEnabled = _view?.Parent is not null;
        UpdateBreadcrumb();
    }

    private sealed record Crumb(DiskNode Node)
    {
        public override string ToString() => $"{Node.Name.TrimEnd('\\')}  ({Bytes.Format(Node.Size)})";
    }

    private void UpdateBreadcrumb()
    {
        var crumbs = new List<Crumb>();
        for (var n = _view; n is not null; n = n.Parent)
        {
            crumbs.Insert(0, new Crumb(n));
        }

        Breadcrumb.ItemsSource = crumbs;
    }

    private void OnBreadcrumbClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is Crumb crumb)
        {
            ZoomTo(crumb.Node);
        }
    }

    // ── Filtro ───────────────────────────────────────────────────────────

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => Map.Filter = DiskFilter.Parse(FilterBox.Text);

    // ── Painel lateral ───────────────────────────────────────────────────

    private void ShowHover(DiskNode? node)
    {
        HoverText.Text = node is null ? "" : node.Kind == NodeKind.FreeSpace
            ? $"Espaço livre · {Bytes.Format(node.Size)}"
            : $"{node.FullPath}  ·  {Bytes.Format(node.Size)}";
    }

    private void ShowSelection(DiskNode? node)
    {
        _selected = node;
        if (node is null)
        {
            InfoName.Text = _view is null ? "Nada selecionado" : _view.Name;
            InfoSize.Text = _view is null ? "" : Bytes.Format(_view.Size);
            InfoDetail.Text = _view is null ? "" : $"{_view.FileCount:N0} arquivos";
            InfoActions.Visibility = Visibility.Collapsed;
            return;
        }

        InfoName.Text = node.Name;
        InfoSize.Text = Bytes.Format(node.Size);
        var share = _view is { Size: > 0 } v ? node.Size / (double)v.Size : 0;
        var lines = new List<string>();
        if (node.IsDirectory)
        {
            lines.Add($"{node.FileCount:N0} arquivos");
        }
        else
        {
            lines.Add(FileCategories.Label(FileCategories.Of(node)));
        }

        lines.Add($"{share:P1} desta visão");
        if (node.Modified > DateTime.MinValue.AddYears(1))
        {
            lines.Add($"Modificado em {node.Modified.ToLocalTime():dd/MM/yyyy HH:mm}");
        }

        if (node.Denied)
        {
            lines.Add("Sem permissão para abrir (rode como administrador para ver)");
        }

        InfoDetail.Text = string.Join("\n", lines);
        InfoActions.Visibility = node.Kind == NodeKind.FreeSpace ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshStats()
    {
        _statsCts?.Cancel();
        if (_view is not { } view)
        {
            return;
        }

        var cts = _statsCts = new CancellationTokenSource();
        var dark = ActualTheme == ElementTheme.Dark;
        _ = Task.Run(() => DiskStatsBuilder.Compute(view, 15, cts.Token), cts.Token).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && !cts.IsCancellationRequested)
            {
                DispatcherQueue.TryEnqueue(() => ShowStats(t.Result, dark));
            }
        }, TaskScheduler.Default);
    }

    private void ShowStats(DiskStats stats, bool dark)
    {
        LegendPanel.Children.Clear();
        var total = Math.Max(1, stats.ByCategory.Sum(c => c.Bytes));
        foreach (var (category, bytes) in stats.ByCategory)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var swatch = new Rectangle
            {
                Width = 12,
                Height = 12,
                RadiusX = 3,
                RadiusY = 3,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(TreemapView.CategoryColor(category, dark)),
            };
            var name = Ui.Text(FileCategories.Label(category), "CaptionTextBlockStyle");
            name.TextWrapping = TextWrapping.NoWrap;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(name, 1);
            var size = Ui.Text($"{Bytes.Format(bytes)} · {bytes / (double)total:P0}", "CaptionTextBlockStyle", secondary: true);
            size.TextWrapping = TextWrapping.NoWrap;
            Grid.SetColumn(size, 2);
            row.Children.Add(swatch);
            row.Children.Add(name);
            row.Children.Add(size);
            LegendPanel.Children.Add(row);
        }

        LargestList.ItemsSource = stats.Largest.Select(n => new ListViewItem
        {
            Tag = n,
            Padding = new Thickness(12, 4, 12, 4),
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = n.Name, TextTrimming = TextTrimming.CharacterEllipsis, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] },
                    new TextBlock
                    {
                        Text = Bytes.Format(n.Size),
                        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    },
                },
            },
        }).ToList();
    }

    private void OnLargestClicked(object sender, ItemClickEventArgs e)
    {
        if ((e.ClickedItem as ListViewItem)?.Tag is DiskNode node)
        {
            if (node.Parent is { } parent && !ReferenceEquals(parent, _view))
            {
                ZoomTo(parent);
            }

            Map.Selected = node;
            ShowSelection(node);
        }
    }

    // ── Ações ────────────────────────────────────────────────────────────

    private void ShowContextMenu(DiskNode node, Windows.Foundation.Point position)
    {
        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        if (node.IsDirectory)
        {
            Add("Ampliar", "", () => ZoomTo(node, Map.BoundsOf(node)));
        }

        Add("Abrir", "", () => Ui.Open(node.FullPath));
        Add("Mostrar no Explorador", "", () => Ui.RevealInExplorer(node.FullPath));
        Add("Copiar caminho", "", () =>
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(node.FullPath);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        });
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(App.Settings.DeleteToRecycleBin ? "Mover para a Lixeira" : "Excluir", "", () => _ = DeleteAsync(node), node.Parent is not null);
        menu.ShowAt(Map, new FlyoutShowOptions { Position = position, ShowMode = FlyoutShowMode.Standard });
    }

    private void OnRevealSelected(object sender, RoutedEventArgs e)
    {
        if (_selected is not null)
        {
            Ui.RevealInExplorer(_selected.FullPath);
        }
    }

    private void OnZoomSelected(object sender, RoutedEventArgs e)
    {
        if (_selected is not null)
        {
            ZoomTo(_selected.IsDirectory ? _selected : _selected.Parent ?? _selected, Map.BoundsOf(_selected));
        }
    }

    private void OnDeleteSelected(object sender, RoutedEventArgs e)
    {
        if (_selected is { Parent: not null })
        {
            _ = DeleteAsync(_selected);
        }
    }

    private async Task DeleteAsync(DiskNode node)
    {
        var recycle = App.Settings.DeleteToRecycleBin;
        var what = node.IsDirectory ? $"a pasta \"{node.Name}\" ({node.FileCount:N0} arquivos, {Bytes.Format(node.Size)})" : $"\"{node.Name}\" ({Bytes.Format(node.Size)})";
        var ok = await Ui.ConfirmAsync(
            XamlRoot,
            recycle ? "Mover para a Lixeira?" : "Excluir de vez?",
            recycle ? $"Vou mover {what} para a Lixeira. Dá para restaurar de lá." : $"Vou apagar {what} definitivamente. Não dá para desfazer.",
            recycle ? "Mover" : "Excluir",
            destructive: true);
        if (!ok)
        {
            return;
        }

        try
        {
            var path = node.FullPath;
            await Task.Run(() =>
            {
                var option = recycle ? Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin : Microsoft.VisualBasic.FileIO.RecycleOption.DeletePermanently;
                if (node.IsDirectory)
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, option);
                }
                else
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, option);
                }
            });
            node.Parent!.Remove(node);
            if (ReferenceEquals(_selected, node))
            {
                ShowSelection(null);
            }

            Map.Invalidate();
            RefreshStats();
            Log.Info($"Excluído ({(recycle ? "Lixeira" : "definitivo")}): {path}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Ui.InfoAsync(XamlRoot, "Não foi possível excluir", ex.Message);
        }
    }
}
