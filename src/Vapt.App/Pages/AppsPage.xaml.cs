using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Vapt.Controls;
using Vapt.Core;
using Vapt.Core.Apps;

namespace Vapt.Pages;

/// <summary>Uma linha da lista: um programa instalado ou uma atualização disponível.</summary>
public sealed partial class AppRow : INotifyPropertyChanged
{
    private BitmapImage? _icon;
    private bool _selected;
    private bool _selectMode;

    public AppRow(InstalledApp app) => App = app;

    public AppRow(AppUpdate update) => Update = update;

    public InstalledApp? App { get; }

    public AppUpdate? Update { get; }

    public string Name => App?.Name ?? Update!.Name;

    public string Detail => Update is { } u
        ? $"{u.Version} → {u.Available} · {u.Id}"
        : string.Join(" · ", new[]
        {
            App!.Publisher,
            App.Version is { Length: > 0 } v ? "versão " + v : null,
            App.InstallDate is { } d ? "instalado em " + d.ToString("dd/MM/yyyy") : null,
        }.Where(s => !string.IsNullOrEmpty(s)));

    public string SizeText => App is { SizeBytes: > 0 } a ? Bytes.Format(a.SizeBytes) : "";

    public string Badge => App is { Broken: true } ? "Quebrado" : "";

    public Visibility BadgeVisibility => Badge.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string PrimaryLabel => Update is not null ? "Atualizar" : App!.Broken ? "Remover entrada" : "Desinstalar";

    public Visibility MoreVisibility => App is { IsStoreApp: false } ? Visibility.Visible : Visibility.Collapsed;

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected != value)
            {
                _selected = value;
                Changed(nameof(Selected));
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool SelectMode
    {
        get => _selectMode;
        set
        {
            _selectMode = value;
            Changed(nameof(CheckVisibility));
        }
    }

    public Visibility CheckVisibility => _selectMode ? Visibility.Visible : Visibility.Collapsed;

    public BitmapImage? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            Changed(nameof(Icon));
            Changed(nameof(FallbackVisibility));
        }
    }

    public Visibility FallbackVisibility => _icon is null ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? SelectionChanged;

    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed partial class AppsPage : Page
{
    private const int TabPrograms = 0, TabStore = 1, TabUpdates = 2;

    private List<AppRow> _desktop = [];
    private List<AppRow>? _store;
    private List<AppRow>? _updates;
    private bool _busy;

    public AppsPage()
    {
        InitializeComponent();
        SortBox.SelectedIndex = 0;
        UpdatesTab.Visibility = WingetUpdates.Available ? Visibility.Visible : Visibility.Collapsed;
        Loaded += async (_, _) => await LoadAsync();
    }

    private int Tab => Tabs.Items.IndexOf(Tabs.SelectedItem);

    // ── Carregar ─────────────────────────────────────────────────────────

    private async Task LoadAsync(bool force = false)
    {
        Ring.IsActive = true;
        EmptyState.Visibility = Visibility.Collapsed;
        try
        {
            switch (Tab)
            {
                case TabStore:
                    if (_store is null || force)
                    {
                        _store = (await InstalledApps.ListStoreAsync()).Select(a => new AppRow(a)).ToList();
                    }

                    break;
                case TabUpdates:
                    if (_updates is null || force)
                    {
                        List.ItemsSource = null;
                        _updates = (await WingetUpdates.ListAsync()).Select(u => new AppRow(u)).ToList();
                    }

                    break;
                default:
                    if (_desktop.Count == 0 || force)
                    {
                        _desktop = (await Task.Run(InstalledApps.ListDesktop)).Select(a => new AppRow(a)).ToList();
                        foreach (var row in _desktop)
                        {
                            row.SelectionChanged += (_, _) => UpdateActionBar();
                        }

                        _ = LoadIconsAsync(_desktop);
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Listar apps.", ex);
        }
        finally
        {
            Ring.IsActive = false;
        }

        Apply();
    }

    private static async Task LoadIconsAsync(List<AppRow> rows)
    {
        foreach (var row in rows)
        {
            var path = row.App?.IconPath;
            if (path is null || !(path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            row.Icon = await FileIcons.GetAsync(path);
        }
    }

    private List<AppRow> Source => (Tab switch
    {
        TabStore => _store,
        TabUpdates => _updates,
        _ => _desktop,
    }) ?? [];

    private void Apply()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        IEnumerable<AppRow> rows = Source;
        if (query.Length > 0)
        {
            rows = rows.Where(r => r.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || (r.App?.Publisher?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false)
                || (r.Update?.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        rows = SortBox.SelectedIndex switch
        {
            1 => rows.OrderByDescending(r => r.App?.SizeBytes ?? 0),
            2 => rows.OrderByDescending(r => r.App?.InstallDate ?? DateTime.MinValue),
            // Quebrados primeiro: são os que pedem atenção.
            _ => rows.OrderByDescending(r => r.App?.Broken ?? false).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        var list = rows.ToList();
        List.ItemsSource = list;
        EmptyState.Visibility = !Ring.IsActive && list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = Tab == TabUpdates ? "Todos os programas estão atualizados" : "Nada encontrado";
        UpdateActionBar();
    }

    private void UpdateActionBar()
    {
        SelectToggle.Visibility = Tab == TabPrograms ? Visibility.Visible : Visibility.Collapsed;
        SilentCheck.Visibility = Tab == TabPrograms ? Visibility.Visible : Visibility.Collapsed;
        if (Tab == TabUpdates && _updates is { Count: > 0 } updates)
        {
            ActionBar.Visibility = Visibility.Visible;
            ActionText.Text = $"{updates.Count} {(updates.Count == 1 ? "atualização disponível" : "atualizações disponíveis")}";
            ActionDetail.Text = "Pelo winget, o gerenciador de pacotes do Windows. Alguns instaladores pedem permissão de administrador.";
            ActionButton.Content = "Atualizar tudo";
            ActionButton.IsEnabled = !_busy;
            return;
        }

        if (Tab == TabPrograms && SelectToggle.IsChecked == true)
        {
            var selected = _desktop.Where(r => r.Selected).ToList();
            ActionBar.Visibility = Visibility.Visible;
            ActionText.Text = selected.Count == 0 ? "Marque os programas para desinstalar" : $"{selected.Count} {(selected.Count == 1 ? "selecionado" : "selecionados")}";
            var silent = selected.Count(r => r.App!.CanUninstallSilently);
            ActionDetail.Text = selected.Count == 0
                ? "Eles saem um depois do outro e no fim o Vapt procura as sobras de todos."
                : $"{silent} saem em silêncio; os outros abrem o próprio desinstalador.";
            ActionButton.Content = selected.Count == 0 ? "Desinstalar" : $"Desinstalar {selected.Count}";
            ActionButton.IsEnabled = selected.Count > 0 && !_busy;
            return;
        }

        ActionBar.Visibility = Visibility.Collapsed;
    }

    private async void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (IsLoaded)
        {
            await LoadAsync();
        }
    }

    private void OnSearch(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Apply();

    private void OnSort(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            Apply();
        }
    }

    private void OnSelectMode(object sender, RoutedEventArgs e)
    {
        var on = SelectToggle.IsChecked == true;
        foreach (var row in _desktop)
        {
            row.SelectMode = on;
            if (!on)
            {
                row.Selected = false;
            }
        }

        UpdateActionBar();
    }

    // ── Ações ────────────────────────────────────────────────────────────

    private async void OnPrimary(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AppRow row || _busy)
        {
            return;
        }

        if (row.Update is { } update)
        {
            await UpdateAsync([row]);
        }
        else if (row.App is { Broken: true } broken)
        {
            await CleanBrokenAsync(broken);
        }
        else
        {
            await UninstallAsync(row.App!, silent: false);
        }
    }

    private void OnMore(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AppRow { App: { } app } || sender is not Button button)
        {
            return;
        }

        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        if (!app.Broken)
        {
            Add("Desinstalar em silêncio", "", () => _ = UninstallAsync(app, silent: true), app.CanUninstallSilently);
        }

        Add("Abrir a pasta do programa", "", () => Ui.Open(app.InstallLocation!), !string.IsNullOrWhiteSpace(app.InstallLocation) && Directory.Exists(app.InstallLocation));
        Add("Copiar comando de desinstalação", "", () =>
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(app.UninstallCommand ?? "");
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        });
        if (app.Broken)
        {
            // Só para programas que já sumiram: num instalado isso apagaria a pasta dele.
            menu.Items.Add(new MenuFlyoutSeparator());
            Add("Remover entrada e sobras", "", () => _ = CleanBrokenAsync(app));
        }
        menu.ShowAt(button, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
    }

    private async void OnAction(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (Tab == TabUpdates && _updates is { Count: > 0 } updates)
        {
            await UpdateAsync(updates);
        }
        else if (Tab == TabPrograms)
        {
            await BulkUninstallAsync(_desktop.Where(r => r.Selected).Select(r => r.App!).ToList());
        }
    }

    private void SetBusy(bool busy, string? text = null)
    {
        _busy = busy;
        Ring.IsActive = busy;
        if (text is not null)
        {
            ActionBar.Visibility = Visibility.Visible;
            ActionText.Text = text;
        }

        UpdateActionBar();
        if (busy && text is not null)
        {
            ActionBar.Visibility = Visibility.Visible;
            ActionText.Text = text;
            ActionButton.IsEnabled = false;
        }
    }

    /// <summary>Desinstala um programa e, se ele sumiu da lista, procura as sobras.</summary>
    private async Task UninstallAsync(InstalledApp app, bool silent)
    {
        var message = app.IsStoreApp
            ? $"\"{app.Name}\" será removido da sua conta. Dá para instalar de novo pela Microsoft Store."
            : silent
                ? $"Vou desinstalar \"{app.Name}\" em silêncio e depois procurar as sobras."
                : $"Vou abrir o desinstalador de \"{app.Name}\". Quando ele terminar, o Vapt procura as pastas e o registro que sobraram.";
        if (!await Ui.ConfirmAsync(XamlRoot, "Desinstalar?", message, "Desinstalar", destructive: true))
        {
            return;
        }

        SetBusy(true, $"Desinstalando {app.Name}…");
        var ok = await InstalledApps.UninstallAsync(app, silent);
        if (app.IsStoreApp)
        {
            SetBusy(false);
            _store = null;
            await LoadAsync();
            if (!ok)
            {
                await Ui.InfoAsync(XamlRoot, "Não foi possível desinstalar", "Veja o log do Vapt para detalhes.");
            }

            return;
        }

        var all = await Task.Run(InstalledApps.ListDesktop);
        if (StillInstalled(app, all))
        {
            // Muitos desinstaladores abrem outro processo e fecham o primeiro: pergunta.
            SetBusy(false);
            if (!await Ui.ConfirmAsync(XamlRoot, "O desinstalador terminou?", $"\"{app.Name}\" ainda aparece instalado. Se a janela do desinstalador ainda está aberta, termine lá e clique em \"Já terminei\".", "Já terminei"))
            {
                await LoadAsync(force: true);
                return;
            }

            all = await Task.Run(InstalledApps.ListDesktop);
            if (StillInstalled(app, all))
            {
                await Ui.InfoAsync(XamlRoot, "Ainda instalado", $"\"{app.Name}\" continua na lista, então não procurei sobras (para não apagar o que está em uso).");
                await LoadAsync(force: true);
                return;
            }
        }

        SetBusy(false);
        var result = await LeftoversDialog.ShowAsync(XamlRoot, [app], all);
        await LoadAsync(force: true);
        await Ui.InfoAsync(XamlRoot, "Desinstalado", $"\"{app.Name}\" foi desinstalado." + (result is null ? "" : " " + result));
    }

    private static bool StillInstalled(InstalledApp app, IReadOnlyList<InstalledApp> all) =>
        all.Any(a => a.KeyPath == app.KeyPath || (a.Name == app.Name && a.Version == app.Version));

    private async Task BulkUninstallAsync(List<InstalledApp> apps)
    {
        if (apps.Count == 0)
        {
            return;
        }

        var silent = SilentCheck.IsChecked == true;
        var names = string.Join(", ", apps.Take(6).Select(a => a.Name)) + (apps.Count > 6 ? $" e mais {apps.Count - 6}" : "");
        if (!await Ui.ConfirmAsync(XamlRoot, $"Desinstalar {apps.Count} programas?", $"{names}.\n\nUm de cada vez{(silent ? "; os que permitem saem em silêncio" : "")}. No fim o Vapt procura as sobras de todos.", "Desinstalar", destructive: true))
        {
            return;
        }

        var i = 0;
        foreach (var app in apps)
        {
            i++;
            SetBusy(true, $"Desinstalando {i} de {apps.Count}: {app.Name}…");
            await InstalledApps.UninstallAsync(app, silent && app.CanUninstallSilently);
        }

        var all = await Task.Run(InstalledApps.ListDesktop);
        var gone = apps.Where(a => !StillInstalled(a, all)).ToList();
        SetBusy(false);
        SelectToggle.IsChecked = false;
        OnSelectMode(this, new RoutedEventArgs());
        var result = gone.Count > 0 ? await LeftoversDialog.ShowAsync(XamlRoot, gone, all) : null;
        await LoadAsync(force: true);
        var left = apps.Count - gone.Count;
        await Ui.InfoAsync(XamlRoot, "Desinstalação em lote", $"{gone.Count} de {apps.Count} desinstalados.{(left > 0 ? $" {left} continuam instalados (cancelados ou com o desinstalador aberto)." : "")}{(result is null ? "" : " " + result)}");
    }

    /// <summary>Programa que sumiu mas deixou a entrada: remove a entrada (com backup) e as sobras.</summary>
    private async Task CleanBrokenAsync(InstalledApp app)
    {
        var all = await Task.Run(InstalledApps.ListDesktop);
        var others = all.Where(a => a.KeyPath != app.KeyPath).ToList();
        var result = await LeftoversDialog.ShowAsync(XamlRoot, [app with { Broken = true }], others);
        if (result is not null)
        {
            await LoadAsync(force: true);
            await Ui.InfoAsync(XamlRoot, "Limpo", result);
        }
    }

    private async Task UpdateAsync(IReadOnlyList<AppRow> rows)
    {
        var failed = new List<string>();
        var i = 0;
        foreach (var row in rows.ToList())
        {
            i++;
            SetBusy(true, $"Atualizando {i} de {rows.Count}: {row.Name}…");
            if (!await WingetUpdates.UpdateAsync(row.Update!))
            {
                failed.Add(row.Name);
            }
        }

        SetBusy(false);
        _updates = null;
        _desktop = [];
        await LoadAsync(force: true);
        await Ui.InfoAsync(XamlRoot, "Atualizações", failed.Count == 0
            ? $"{rows.Count} {(rows.Count == 1 ? "programa atualizado" : "programas atualizados")}."
            : $"{rows.Count - failed.Count} atualizados; não deu: {string.Join(", ", failed)}. Alguns só atualizam com o programa fechado ou como administrador.");
    }
}
