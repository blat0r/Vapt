using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vapt.Controls;
using Vapt.Core;
using Vapt.Core.Debloat;
using Vapt.Core.Tweaks;

namespace Vapt.Pages;

/// <summary>
/// Debloat agrupado por objetivo (como o privacy.sexy e o Win11Debloat): visão geral com o
/// "recomendado", depois Apps, Anúncios, Privacidade (ajustes + telemetria em segundo plano),
/// IA e Recall e Serviços.
/// </summary>
public sealed partial class DebloatPage : Page
{
    private const int TabOverview = 0, TabApps = 1, TabAds = 2, TabPrivacy = 3, TabAi = 4, TabServices = 5;

    private readonly Dictionary<InstalledBloat, CheckBox> _checks = [];
    private readonly HashSet<int> _built = [];
    private DebloatPlan? _plan;
    private bool _busy;

    public DebloatPage()
    {
        InitializeComponent();
        if (!Elevation.IsAdministrator)
        {
            Header.Children.Insert(2, Ui.AdminBar("debloat", "Sem administrador, os apps saem só da sua conta e serviços, tarefas e políticas do sistema ficam travados. Como administrador, tudo fica liberado e os apps não voltam em contas novas."));
        }

        Loaded += async (_, _) =>
        {
            // "--tab <n>": abre direto numa aba (atalhos e testes).
            var tab = Environment.GetCommandLineArgs().SkipWhile(a => a != "--tab").Skip(1).FirstOrDefault();
            if (int.TryParse(tab, out var n) && n > 0 && n < Tabs.Items.Count)
            {
                Tabs.SelectedItem = Tabs.Items[n];
            }
            else
            {
                await LoadOverviewAsync();
            }
        };
    }

    private static Style S(string key) => (Style)Application.Current.Resources[key];

    private static Brush B(string key) => (Brush)Application.Current.Resources[key];

    private void Show(InfoBarSeverity severity, string message) => Ui.ShowStatus(StatusBar, severity, message);

    private void SelectTab(int index) => Tabs.SelectedItem = Tabs.Items[index];

    private async void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var index = sender.Items.IndexOf(sender.SelectedItem);
        var panels = new FrameworkElement[] { OverviewPanel, AppsPanel, AdsPanel, PrivacyPanel, AiPanel, ServicesPanel };
        for (var i = 0; i < panels.Length; i++)
        {
            panels[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        }

        StatusBar.IsOpen = false;
        Scroller.ChangeView(null, 0, null, disableAnimation: true);
        if (index == TabOverview)
        {
            await LoadOverviewAsync();
            return;
        }

        if (!_built.Add(index))
        {
            return;
        }

        switch (index)
        {
            case TabApps:
                await LoadAppsAsync();
                break;
            case TabAds:
                AdsPanel.Children.Add(Intro("Propagandas e sugestões que o Windows, o Iniciar, a pesquisa e o Edge mostram. Desligar não tira nenhuma função."));
                AdsPanel.Children.Add(new TweakList(TweakCatalog.All.Where(t => t.Group == TweakGroup.Ads), StatusBar, groupHeaders: false));
                break;
            case TabPrivacy:
                await BuildPrivacyAsync();
                break;
            case TabAi:
                BuildAi();
                break;
            case TabServices:
                foreach (var s in DebloatCatalog.Services.Where(s => s.Kind == ServiceKind.Background))
                {
                    AddServiceCard(ServicesPanel, s);
                }

                break;
        }
    }

    private static TextBlock Intro(string text) => new()
    {
        Text = text,
        Margin = new Thickness(1, 0, 0, 8),
        TextWrapping = TextWrapping.Wrap,
        Foreground = B("TextFillColorSecondaryBrush"),
        Style = S("BodyTextBlockStyle"),
    };

    // ── Visão geral ──────────────────────────────────────────────────────

    private int _overviewVersion;

    private async Task LoadOverviewAsync()
    {
        // A aba e o Loaded podem pedir ao mesmo tempo: só a chamada mais nova desenha.
        var version = ++_overviewVersion;
        PlanBusy.Visibility = Visibility.Visible;
        ApplyPlanButton.IsEnabled = false;
        PlanList.Children.Clear();
        try
        {
            var plan = await DebloatPreset.PlanAsync();
            if (version != _overviewVersion)
            {
                return;
            }

            _plan = plan;
        }
        catch (Exception ex)
        {
            Log.Error("Debloat: plano.", ex);
            PlanBusy.Visibility = Visibility.Collapsed;
            PlanList.Children.Add(Intro("Não foi possível montar o plano: " + ex.Message));
            return;
        }

        PlanBusy.Visibility = Visibility.Collapsed;
        void Line(int count, string text)
        {
            if (count == 0)
            {
                return;
            }

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new FontIcon { Glyph = "", FontSize = 14, Foreground = B("AccentTextFillColorPrimaryBrush") });
            row.Children.Add(new TextBlock { Text = text, Style = S("BodyTextBlockStyle") });
            PlanList.Children.Add(row);
        }

        var p = _plan;
        Line(p.Apps.Count, $"Remover {p.Apps.Count} {(p.Apps.Count == 1 ? "app inútil" : "apps inúteis")} ({string.Join(", ", p.Apps.Take(3).Select(a => a.App.Name))}{(p.Apps.Count > 3 ? "…" : "")})");
        Line(p.Ads.Count, $"Desligar {p.Ads.Count} {(p.Ads.Count == 1 ? "fonte" : "fontes")} de anúncios e sugestões");
        Line(p.Privacy.Count, $"Ligar {p.Privacy.Count} {(p.Privacy.Count == 1 ? "proteção" : "proteções")} de privacidade");
        Line(p.Ai.Count, $"Desligar {p.Ai.Count} {(p.Ai.Count == 1 ? "recurso" : "recursos")} de IA (Recall, Copilot…)");
        Line(p.Tasks.Count + p.Services.Count, $"Parar {p.Tasks.Count + p.Services.Count} tarefas e serviços de coleta de dados");
        if (p.Total == 0)
        {
            PlanList.Children.Add(Intro(Elevation.IsAdministrator
                ? "Nada a fazer: o recomendado já está aplicado neste PC."
                : "O que dava para fazer sem administrador já está feito. Reinicie como administrador para liberar o resto."));
        }
        else if (!Elevation.IsAdministrator)
        {
            PlanList.Children.Add(Intro("Como administrador o recomendado também desliga tarefas, serviços e políticas do sistema."));
        }

        ApplyPlanText.Text = p.Total == 0 ? "Tudo aplicado" : $"Aplicar o recomendado ({p.Total})";
        ApplyPlanButton.IsEnabled = p.Total > 0 && !_busy;
        await BuildCategoryCardsAsync(version);
    }

    private async Task BuildCategoryCardsAsync(int version)
    {
        CategoryCards.Children.Clear();
        async Task<string> Applied(TweakGroup g)
        {
            var all = TweakCatalog.All.Where(t => t.Group == g).ToList();
            var on = 0;
            foreach (var t in all)
            {
                on += await TweakEngine.IsAppliedAsync(t) ? 1 : 0;
            }

            return $"{on} de {all.Count} aplicados";
        }

        var appsLeft = _plan?.Apps.Count ?? 0;
        var background = DebloatCatalog.Services.Where(s => s.Kind == ServiceKind.Background && TweakEngine.ServiceStart(s.Name) is not null).ToList();
        var backgroundDone = background.Count(s => TweakEngine.ServiceStart(s.Name) >= s.Target);
        var ads = await Applied(TweakGroup.Ads);
        var privacy = await Applied(TweakGroup.Privacy);
        var ai = await Applied(TweakGroup.AI);
        if (version != _overviewVersion)
        {
            return;
        }

        CategoryCards.Children.Clear();
        AddCategory("Apps pré-instalados", appsLeft > 0 ? $"{appsLeft} recomendados para remover · jogos, apps de fabricante e da Microsoft" : "Nenhum app recomendado sobrando", "", TabApps);
        AddCategory("Anúncios e sugestões", $"{ads} · Iniciar, pesquisa, tela de bloqueio, Configurações e Edge", "", TabAds);
        AddCategory("Privacidade", $"{privacy} · telemetria, rastreamento, fala e tarefas de coleta", "", TabPrivacy);
        AddCategory("IA e Recall", $"{ai} · Recall, Click to Do, Copilot, IA do Edge, Chrome, Paint e Bloco de Notas", "", TabAi);
        AddCategory("Serviços em segundo plano", $"{backgroundDone} de {background.Count} otimizados · fax, mapas, impressão, controle dos pais", "", TabServices);
    }

    private void AddCategory(string title, string description, string glyph, int tab)
    {
        var card = Ui.Card(title, description, glyph, null);
        card.IsClickEnabled = true;
        card.Click += (_, _) => SelectTab(tab);
        CategoryCards.Children.Add(card);
    }

    private async void OnApplyPlan(object sender, RoutedEventArgs e)
    {
        if (_plan is not { Total: > 0 } plan)
        {
            return;
        }

        var lines = PlanList.Children.OfType<StackPanel>().Select(r => "• " + ((TextBlock)r.Children[1]).Text);
        if (!await Ui.ConfirmAsync(XamlRoot, "Aplicar o debloat recomendado?", string.Join("\n", lines) + "\n\nTudo pode ser desfeito: apps pela Microsoft Store, o resto nas abas do Debloat ou em \"Desfazer tudo\" das Otimizações.", "Aplicar"))
        {
            return;
        }

        _busy = true;
        ApplyPlanButton.IsEnabled = false;
        PlanBusy.Visibility = Visibility.Visible;
        try
        {
            await TweakList.EnsureRestorePointAsync(StatusBar);
            var progress = new Progress<string>(name => ApplyPlanText.Text = name.Length > 28 ? name[..28] + "…" : name);
            var (done, failed) = await DebloatPreset.ApplyAsync(plan, progress);
            Show(failed.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                failed.Count == 0
                    ? $"Pronto! {done} itens aplicados. Reinicie o Explorador (ou o PC) para ver tudo."
                    : $"{done} itens aplicados; não deu: {string.Join(", ", failed.Take(5))}{(failed.Count > 5 ? "…" : "")}.");
        }
        catch (Exception ex)
        {
            Log.Error("Debloat recomendado.", ex);
            Show(InfoBarSeverity.Error, "O debloat parou: " + ex.Message);
        }
        finally
        {
            _busy = false;
            _built.Clear(); // as abas mostram o estado novo quando abertas
            AdsPanel.Children.Clear();
            PrivacyPanel.Children.Clear();
            // O primeiro item de Serviços e de IA vem do XAML (aviso e cartão do Recall): fica.
            foreach (var panel in new[] { ServicesPanel, AiPanel })
            {
                while (panel.Children.Count > 1)
                {
                    panel.Children.RemoveAt(panel.Children.Count - 1);
                }
            }
            await LoadOverviewAsync();
        }
    }

    // ── Apps ─────────────────────────────────────────────────────────────

    private async Task LoadAppsAsync()
    {
        AppsBusy.Visibility = Visibility.Visible;
        AppGroups.Children.Clear();
        _checks.Clear();
        IReadOnlyList<InstalledBloat> found;
        try
        {
            found = await DebloatEngine.DetectAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Debloat: detectar apps.", ex);
            AppsTitle.Text = "Não foi possível listar os apps";
            AppsSubtitle.Text = ex.Message;
            AppsBusy.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var group in Enum.GetValues<AppGroup>())
        {
            var items = found.Where(f => f.App.Group == group)
                .OrderByDescending(f => f.App.Recommended)
                .ThenBy(f => f.App.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (items.Count == 0)
            {
                continue;
            }

            AppGroups.Children.Add(new TextBlock { Text = $"{DebloatCatalog.GroupName(group)} ({items.Count})", Style = S("SectionHeaderStyle") });
            foreach (var item in items)
            {
                AppGroups.Children.Add(BuildAppCard(item));
            }
        }

        AppsBusy.Visibility = Visibility.Collapsed;
        var recommended = found.Count(f => f.App.Recommended);
        AppsTitle.Text = found.Count == 0 ? "Nenhum bloatware conhecido aqui" : $"{found.Count} apps que dá para tirar";
        AppsSubtitle.Text = found.Count == 0
            ? "Este Windows já está limpo dos apps que o Vapt conhece."
            : $"{recommended} são recomendados para remover (sem efeito colateral conhecido). Os outros são opcionais: tire só o que você não usa.";
        BuildRemoved();
        UpdateRemoveButton();
    }

    private FrameworkElement BuildAppCard(InstalledBloat item)
    {
        var app = item.App;
        var check = new CheckBox { IsChecked = app.Selected, MinWidth = 0, Margin = new Thickness(8, 0, -8, 0) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(check, app.Name);
        check.Checked += (_, _) => UpdateRemoveButton();
        check.Unchecked += (_, _) => UpdateRemoveButton();
        _checks[item] = check;

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        content.Children.Add(Badge(app.Recommended ? "Recomendado" : "Opcional", app.Recommended ? "SystemFillColorSuccessBackgroundBrush" : "SystemFillColorNeutralBackgroundBrush"));
        content.Children.Add(check);

        var description = app.Description;
        if (item.PackageFullName is null && item.Provisioned)
        {
            description += " · Só no instalador do Windows (aparece em contas novas).";
        }

        var glyph = app.Group switch
        {
            AppGroup.Oem => "",
            AppGroup.Sponsored => "",
            _ => app.Id.Contains("Xbox", StringComparison.OrdinalIgnoreCase) ? "" : "",
        };
        var card = Ui.Card(app.Name, description, glyph, content);
        card.IsClickEnabled = true;
        card.IsActionIconVisible = false;
        card.Click += (_, _) => check.IsChecked = check.IsChecked != true;
        ToolTipService.SetToolTip(card, app.Id);
        return card;
    }

    private static Border Badge(string text, string brush) => new()
    {
        Padding = new Thickness(8, 2, 8, 3),
        CornerRadius = new CornerRadius(10),
        VerticalAlignment = VerticalAlignment.Center,
        Background = B(brush),
        Child = new TextBlock { Text = text, Style = S("CaptionTextBlockStyle") },
    };

    private void UpdateRemoveButton()
    {
        var n = _checks.Count(c => c.Value.IsChecked == true);
        RemoveText.Text = n == 0 ? "Remover" : $"Remover {n}";
        RemoveButton.IsEnabled = n > 0 && !_busy;
    }

    private void OnSelectRecommended(object sender, RoutedEventArgs e)
    {
        foreach (var (item, check) in _checks)
        {
            check.IsChecked = item.App.Recommended;
        }
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        var chosen = _checks.Where(c => c.Value.IsChecked == true).Select(c => c.Key).ToList();
        if (chosen.Count == 0)
        {
            return;
        }

        var names = string.Join(", ", chosen.Take(8).Select(c => c.App.Name)) + (chosen.Count > 8 ? $" e mais {chosen.Count - 8}" : "");
        var scope = Elevation.IsAdministrator ? "de todas as contas deste PC" : "da sua conta";
        if (!await Ui.ConfirmAsync(XamlRoot, $"Remover {chosen.Count} apps?", $"Vou remover {scope}: {names}.\n\nQuase todos podem ser instalados de novo pela Microsoft Store.", "Remover", destructive: true))
        {
            return;
        }

        _busy = true;
        UpdateRemoveButton();
        AppsBusy.Visibility = Visibility.Visible;
        try
        {
            var progress = new Progress<string>(name => AppsSubtitle.Text = $"Removendo {name}…");
            var (removed, failed) = await DebloatEngine.RemoveAsync(chosen, progress);
            Show(failed.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                failed.Count == 0 ? $"{removed} apps removidos." : $"{removed} removidos; não saíram: {string.Join(", ", failed)}. Alguns apps só saem como administrador.");
        }
        catch (Exception ex)
        {
            Log.Error("Debloat: remover.", ex);
            Show(InfoBarSeverity.Error, "A remoção parou: " + ex.Message);
        }
        finally
        {
            _busy = false;
        }

        await LoadAppsAsync();
    }

    private void BuildRemoved()
    {
        RemovedPanel.Children.Clear();
        var removed = DebloatEngine.Removed();
        if (removed.Count == 0)
        {
            return;
        }

        var expander = new SettingsExpander
        {
            Header = "Removidos pelo Vapt",
            Description = $"{removed.Count} apps. Para ter um de volta, abra a página dele na Microsoft Store.",
            HeaderIcon = new FontIcon { Glyph = "" },
            Margin = new Thickness(0, 24, 0, 0),
        };
        foreach (var r in removed.OrderByDescending(r => r.When))
        {
            var store = new Button { Content = "Abrir na Loja" };
            store.Click += (_, _) => Ui.Open(DebloatEngine.StoreLink(r));
            expander.Items.Add(new SettingsCard { Header = r.Name, Description = $"Removido em {r.When:dd/MM/yyyy HH:mm}", Content = store });
        }

        RemovedPanel.Children.Add(expander);
    }

    // ── Privacidade ──────────────────────────────────────────────────────

    private async Task BuildPrivacyAsync()
    {
        PrivacyPanel.Children.Add(Intro("O que o Windows coleta sobre você e manda para a Microsoft. Nada aqui tira função do dia a dia."));
        PrivacyPanel.Children.Add(new TweakList(TweakCatalog.All.Where(t => t.Group == TweakGroup.Privacy), StatusBar, groupHeaders: false));

        PrivacyPanel.Children.Add(new TextBlock { Text = "Coleta em segundo plano", Style = S("SectionHeaderStyle") });
        foreach (var s in DebloatCatalog.Services.Where(s => s.Kind == ServiceKind.Telemetry))
        {
            AddServiceCard(PrivacyPanel, s);
        }

        await AddTasksAsync(PrivacyPanel);
    }

    // ── IA e Recall ──────────────────────────────────────────────────────

    private void BuildAi()
    {
        var recall = RecallInfo.Probe();
        if (recall.HasSnapshots)
        {
            RecallTitle.Text = $"O Recall está ativo e já guarda {Bytes.Format(recall.SnapshotBytes)}";
            RecallText.Text = "Ele tira uma captura da tela a cada poucos segundos para você \"pesquisar no passado\". As capturas ocupam disco e o serviço usa memória e processador. Desligue abaixo se não usa.";
        }
        else if (recall.Present)
        {
            RecallTitle.Text = "O Recall existe neste PC, mas não está guardando capturas";
            RecallText.Text = "Para garantir que ele não ligue sozinho depois de uma atualização, aplique as opções abaixo.";
        }
        else
        {
            RecallTitle.Text = "Este PC não tem o Recall";
            RecallText.Text = "O Recall só vem em PCs Copilot+ (com NPU). Mesmo assim, as políticas abaixo impedem que ele seja instalado por uma atualização futura e tiram as outras IAs do Windows e dos navegadores.";
        }

        AiPanel.Children.Add(new TweakList(TweakCatalog.All.Where(t => t.Group == TweakGroup.AI), StatusBar, groupHeaders: false));
        foreach (var s in DebloatCatalog.Services.Where(s => s.Kind == ServiceKind.AI))
        {
            AddServiceCard(AiPanel, s);
        }
    }

    // ── Serviços ─────────────────────────────────────────────────────────

    private static string StartName(int? start) => start switch
    {
        2 => "Automático",
        3 => "Manual",
        4 => "Desativado",
        0 or 1 => "Sistema",
        _ => "Não existe",
    };

    private void AddServiceCard(Panel panel, BloatService service)
    {
        var current = TweakEngine.ServiceStart(service.Name);
        if (current is null)
        {
            return; // não existe nesta edição/versão
        }

        var state = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = B("TextFillColorSecondaryBrush"), Text = StartName(current) };
        var toggle = new ToggleSwitch
        {
            OnContent = service.Target == 4 ? "Desativado" : "Manual",
            OffContent = "Padrão",
            IsOn = current >= service.Target,
            IsEnabled = Elevation.IsAdministrator,
        };
        var syncing = false;
        toggle.Toggled += async (_, _) =>
        {
            if (syncing)
            {
                return;
            }

            toggle.IsEnabled = false;
            try
            {
                if (toggle.IsOn)
                {
                    await TweakEngine.SetServiceAsync(service.Name, service.Target);
                }
                else
                {
                    await TweakEngine.RestoreServiceAsync(service.Name, service.Default);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Serviço {service.Name}.", ex);
                Show(InfoBarSeverity.Error, $"\"{service.Title}\": {ex.Message}");
            }
            finally
            {
                var now = TweakEngine.ServiceStart(service.Name);
                state.Text = StartName(now);
                syncing = true;
                toggle.IsOn = now >= service.Target;
                syncing = false;
                toggle.IsEnabled = true;
            }
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        if (service.Caution)
        {
            content.Children.Add(Badge("Atenção", "SystemFillColorCautionBackgroundBrush"));
        }

        content.Children.Add(state);
        content.Children.Add(toggle);
        var description = service.Description + (Elevation.IsAdministrator ? "" : " · Precisa de administrador.");
        var card = Ui.Card(service.Title, description, "", content);
        ToolTipService.SetToolTip(card, $"Serviço: {service.Name}");
        panel.Children.Add(card);
    }

    // ── Tarefas ──────────────────────────────────────────────────────────

    private async Task AddTasksAsync(Panel panel)
    {
        var ring = new ProgressRing { IsActive = true, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(ring);
        var states = (await DebloatEngine.TaskStatesAsync()).ToDictionary(s => s.Path, StringComparer.OrdinalIgnoreCase);
        panel.Children.Remove(ring);

        var toggles = new List<ToggleSwitch>();
        var disableAll = new Button { Content = "Desligar todas", IsEnabled = Elevation.IsAdministrator };
        var expander = new SettingsExpander
        {
            Header = "Tarefas agendadas de coleta",
            Description = "Rodam escondidas (compatibilidade, programa de experiência do usuário, feedback, relatórios de erro) e às vezes pesam no disco.",
            HeaderIcon = new FontIcon { Glyph = "" },
            Content = disableAll,
            IsExpanded = false,
        };

        foreach (var task in DebloatCatalog.Tasks)
        {
            if (!states.TryGetValue(task.Path, out var st) || !st.Exists)
            {
                continue;
            }

            var toggle = new ToggleSwitch { OnContent = "Desligada", OffContent = "Ligada", IsOn = !st.Enabled, IsEnabled = Elevation.IsAdministrator };
            var syncing = false;
            toggle.Toggled += async (_, _) =>
            {
                if (syncing)
                {
                    return;
                }

                toggle.IsEnabled = false;
                try
                {
                    await DebloatEngine.SetTaskEnabledAsync(task, enabled: !toggle.IsOn);
                }
                catch (Exception ex)
                {
                    Log.Error($"Tarefa {task.Path}.", ex);
                    Show(InfoBarSeverity.Error, $"\"{task.Title}\": {ex.Message}");
                    syncing = true;
                    toggle.IsOn = !toggle.IsOn;
                    syncing = false;
                }
                finally
                {
                    toggle.IsEnabled = true;
                    UpdateTasksDescription();
                }
            };
            toggles.Add(toggle);
            var card = new SettingsCard { Header = task.Title, Description = task.Description, Content = toggle };
            ToolTipService.SetToolTip(card, task.Path);
            expander.Items.Add(card);
        }

        void UpdateTasksDescription() =>
            disableAll.Content = toggles.All(t => t.IsOn) ? "Todas desligadas" : $"Desligar todas ({toggles.Count(t => !t.IsOn)})";

        UpdateTasksDescription();
        disableAll.Click += (_, _) =>
        {
            foreach (var t in toggles.Where(t => !t.IsOn))
            {
                t.IsOn = true; // cada uma aplica pelo próprio evento
            }
        };
        panel.Children.Add(expander);
    }
}
