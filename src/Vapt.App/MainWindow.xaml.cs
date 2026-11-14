using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Vapt.Core;
using Vapt.Pages;

namespace Vapt;

public sealed partial class MainWindow : Window
{
    private const string SettingsTag = "settings";

    /// <summary>Itens do menu, na ordem: tag, nome, ícone (Segoe Fluent Icons) e página.</summary>
    private static readonly (string Tag, string Name, string Glyph, Type Page)[] Sections =
    [
        ("home", "Início", "", typeof(HomePage)),
        ("cleanup", "Limpeza", "", typeof(CleanupPage)),
        ("tweaks", "Otimizações", "", typeof(TweaksPage)),
        ("startup", "Inicialização", "", typeof(StartupPage)),
        ("apps", "Aplicativos", "", typeof(AppsPage)),
        ("debloat", "Debloat", "", typeof(DebloatPage)),
        ("disk", "Espaço em disco", "", typeof(DiskPage)),
        ("duplicates", "Duplicados", "", typeof(DuplicatesPage)),
        ("usb", "Pendrive", "", typeof(UsbPage)),
        ("repair", "Reparar", "", typeof(RepairPage)),
    ];

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
        ConfigureSize();
        BuildNavigation();
        ApplyTheme();

        // "Usar a configuração do sistema" acompanha o Windows na hora (claro/escuro e cor de destaque).
        _uiSettings.ColorValuesChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplyTheme);
    }

    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();

    private void BuildNavigation()
    {
        foreach (var (tag, name, glyph, _) in Sections)
        {
            if (tag == "disk")
            {
                NavView.MenuItems.Add(new NavigationViewItemHeader { Content = "Ferramentas" });
            }

            NavView.MenuItems.Add(new NavigationViewItem
            {
                Content = name,
                Tag = tag,
                Icon = new FontIcon { Glyph = glyph },
            });
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

        var item = NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag)
            ?? (tag == SettingsTag ? NavView.SettingsItem as NavigationViewItem : null);
        if (item is not null)
        {
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
