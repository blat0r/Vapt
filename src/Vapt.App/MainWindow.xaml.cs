using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Vapt.Core;
using Vapt.Core.Perf;
using Vapt.Pages;

namespace Vapt;

public sealed partial class MainWindow : Window
{
    private const string SettingsTag = "settings";

    /// <summary>Itens do menu, na ordem: tag, nome, ícone (Segoe Fluent Icons) e página.</summary>
    private static readonly (string Tag, string Name, string Glyph, Type Page)[] Sections =
    [
        ("home", "Início", "", typeof(HomePage)),
        ("system", "Meu PC", "", typeof(SystemPage)),
        ("cleanup", "Limpeza", "", typeof(CleanupPage)),
        ("tweaks", "Otimizações", "", typeof(TweaksPage)),
        ("startup", "Inicialização", "", typeof(StartupPage)),
        ("apps", "Aplicativos", "", typeof(AppsPage)),
        ("debloat", "Debloat", "", typeof(DebloatPage)),
        ("disk", "Espaço em disco", "", typeof(DiskPage)),
        ("duplicates", "Duplicados", "", typeof(DuplicatesPage)),
        ("network", "Rede", "", typeof(NetworkPage)),
        ("usb", "Pendrive", "", typeof(UsbPage)),
        ("repair", "Reparar", "", typeof(RepairPage)),
        // Grupo "Desempenho" (vira um item que expande no menu).
        ("sensors", "Sensores", "", typeof(SensorsPage)),
        ("bench", "Benchmark", "", typeof(BenchPage)),
        ("stress", "Stress test", "", typeof(StressPage)),
        ("oc", "Overclock", "", typeof(OverclockPage)),
        ("overlay", "Overlay de FPS", "", typeof(OverlayPage)),
    ];

    private static readonly HashSet<string> PerfTags = ["sensors", "bench", "stress", "oc", "overlay"];

    /// <summary>Todos os itens do menu, inclusive os de dentro de grupos.</summary>
    private IEnumerable<NavigationViewItem> AllItems() =>
        NavView.MenuItems.OfType<NavigationViewItem>().SelectMany(i => i.MenuItems.OfType<NavigationViewItem>().Prepend(i));

    private bool _syncingSelection;
    private string _currentTag = "home";

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Vapt.ico"));
        AdminBadge.Visibility = Elevation.IsAdministrator ? Visibility.Visible : Visibility.Collapsed;
        ConfigureBackdrop();
        ConfigureSize();
        BuildNavigation();
        ApplyTheme();

        // "Usar a configuração do sistema" acompanha o Windows na hora (claro/escuro e cor de destaque).
        _uiSettings.ColorValuesChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplyTheme);

        // Atualização automática: alguns segundos depois de abrir e a cada 6 horas.
        Closed += (_, _) =>
        {
            GameOverlay.Stop(); // fecha também a sessão de medição de FPS do Windows
            Updater.ApplyOnExitIfReady();
        };
        GameOverlay.Options = App.Settings.Overlay;
        if (App.Settings.OverlayAtStart || Environment.GetCommandLineArgs().Contains("--overlay"))
        {
            GameOverlay.Start();
        }
        if (App.Settings.AutoUpdate)
        {
            _ = CheckUpdatesSoonAsync();
            _updateTimer = DispatcherQueue.CreateTimer();
            _updateTimer.Interval = TimeSpan.FromHours(6);
            _updateTimer.Tick += async (_, _) => await CheckUpdatesAsync();
            _updateTimer.Start();
        }
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _updateTimer;

    private async Task CheckUpdatesSoonAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        await CheckUpdatesAsync();
    }

    /// <summary>Procura, baixa e avisa. Também usado pelo botão das Configurações.</summary>
    public async Task<UpdateState> CheckUpdatesAsync()
    {
        var state = await Updater.CheckAndDownloadAsync();
        if (state == UpdateState.Ready)
        {
            ShowUpdateReady();
        }

        return state;
    }

    public void ShowUpdateReady()
    {
        var restart = new Button { Content = "Reiniciar e atualizar", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        restart.Click += (_, _) => Updater.ApplyAndRestart();
        UpdateBar.Message = $"A versão {Updater.ReadyVersion} já foi baixada. Reinicie agora ou ela entra sozinha quando você fechar o Vapt.";
        UpdateBar.ActionButton = restart;
        UpdateBar.IsOpen = true;
    }

    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();

    /// <summary>
    /// Mica só existe no Windows 11. No Windows 10 usa acrílico; se nem isso houver, fundo sólido
    /// (sem isso a janela ficaria transparente ou preta).
    /// </summary>
    private void ConfigureBackdrop()
    {
        if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
        {
            return; // o MicaBackdrop do XAML já vale
        }

        if (Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
        }
        else
        {
            SystemBackdrop = null;
            RootGrid.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
        }
    }

    private void BuildNavigation()
    {
        NavigationViewItem? perf = null;
        foreach (var (tag, name, glyph, _) in Sections)
        {
            if (tag == "disk")
            {
                NavView.MenuItems.Add(new NavigationViewItemHeader { Content = "Ferramentas" });
            }

            var item = new NavigationViewItem
            {
                Content = name,
                Tag = tag,
                Icon = new FontIcon { Glyph = glyph },
            };
            if (PerfTags.Contains(tag))
            {
                if (perf is null)
                {
                    perf = new NavigationViewItem
                    {
                        Content = "Desempenho",
                        Tag = "perf",
                        Icon = new FontIcon { Glyph = "" },
                        SelectsOnInvoked = false,
                    };
                    NavView.MenuItems.Add(perf);
                }

                perf.MenuItems.Add(item);
            }
            else
            {
                NavView.MenuItems.Add(item);
            }
        }

        if (NavView.SettingsItem is NavigationViewItem settingsItem)
        {
            settingsItem.Content = "Configurações";
            settingsItem.Tag = SettingsTag;
        }

        NavView.SelectedItem = NavView.MenuItems.OfType<NavigationViewItem>().First();
    }

    private string? _pendingTag;

    public void NavigateTo(string tag)
    {
        // O item de Configurações só existe depois que o menu carrega.
        if (!NavView.IsLoaded)
        {
            if (_pendingTag is null)
            {
                NavView.Loaded += (_, _) =>
                {
                    if (_pendingTag is { } pending)
                    {
                        _pendingTag = null;
                        NavigateTo(pending);
                    }
                };
            }

            _pendingTag = tag;
            return;
        }

        var item = AllItems().FirstOrDefault(i => (string)i.Tag == tag)
            ?? (tag == SettingsTag ? NavView.SettingsItem as NavigationViewItem : null);
        if (item is not null)
        {
            if (PerfTags.Contains(tag) && AllItems().FirstOrDefault(i => (string)i.Tag == "perf") is { } group)
            {
                group.IsExpanded = true;
            }

            NavView.SelectedItem = item;
        }
    }

    private static Type PageFor(string tag) =>
        tag == SettingsTag ? typeof(SettingsPage) : Sections.FirstOrDefault(s => s.Tag == tag).Page ?? typeof(HomePage);

    private static int IndexOf(string tag) => tag == SettingsTag ? int.MaxValue : Array.FindIndex(Sections, s => s.Tag == tag);

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingSelection)
        {
            return;
        }

        var tag = args.IsSettingsSelected ? SettingsTag : (args.SelectedItemContainer?.Tag as string ?? "home");
        if (tag == "perf")
        {
            return; // o grupo só abre e fecha
        }

        var pageType = PageFor(tag);
        if (ContentFrame.CurrentSourcePageType == pageType)
        {
            return;
        }

        // Descendo no menu a página sobe deslizando; subindo, entra suave.
        NavigationTransitionInfo transition = IndexOf(tag) > IndexOf(_currentTag)
            ? new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromBottom }
            : new EntranceNavigationTransitionInfo();
        _currentTag = tag;
        ContentFrame.Navigate(pageType, null, transition);
    }

    private void OnFrameNavigated(object sender, NavigationEventArgs e)
    {
        var tag = e.SourcePageType == typeof(SettingsPage)
            ? SettingsTag
            : Sections.FirstOrDefault(s => s.Page == e.SourcePageType).Tag ?? "home";
        _currentTag = tag;
        _syncingSelection = true;
        try
        {
            NavigateTo(tag);
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    public string CurrentTag => _currentTag;

    /// <summary>Monta todas as páginas (sem mostrar a janela) e devolve quantas falharam.</summary>
    public int SmokeTest()
    {
        var failures = 0;
        foreach (var page in Sections.Select(s => s.Page).Append(typeof(SettingsPage)))
        {
            try
            {
                if (!ContentFrame.Navigate(page, null, new SuppressNavigationTransitionInfo()) || ContentFrame.Content is null)
                {
                    throw new InvalidOperationException("Navigate devolveu falso.");
                }

                Log.Info($"Smoke: {page.Name} ok.");
            }
            catch (Exception ex)
            {
                failures++;
                Log.Error($"Smoke: {page.Name} falhou.", ex);
            }
        }

        Log.Info(failures == 0 ? "Smoke: todas as páginas montaram." : $"Smoke: {failures} página(s) falharam.");
        return failures;
    }

    private void OnTitleBarBackRequested(TitleBar sender, object args)
    {
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
        }
    }

    private void OnTitleBarPaneToggleRequested(TitleBar sender, object args) => NavView.IsPaneOpen = !NavView.IsPaneOpen;

    public void ApplyTheme()
    {
        var theme = App.Settings.Theme;
        var light = theme switch
        {
            AppTheme.Light => true,
            AppTheme.Dark => false,
            _ => AppsUseLightTheme(),
        };
        RootGrid.RequestedTheme = light ? ElementTheme.Light : ElementTheme.Dark;
        AppWindow.TitleBar.PreferredTheme = light ? TitleBarTheme.Light : TitleBarTheme.Dark;
    }

    private static bool AppsUseLightTheme()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
    }

    private void ConfigureSize()
    {
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(640 * scale);
            presenter.PreferredMinimumHeight = (int)(500 * scale);
        }

        var width = (int)(1180 * scale);
        var height = (int)(800 * scale);
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        width = Math.Min(width, area.Width);
        height = Math.Min(height, area.Height);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2,
            width,
            height));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
