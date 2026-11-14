using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vapt.Core;
using Vapt.Core.Repair;
using Vapt.Core.Tweaks;

namespace Vapt.Pages;

public sealed partial class RepairPage : Page
{
    private readonly RadioButtons _dns = new() { Margin = new Thickness(56, 8, 16, 12) };
    private IReadOnlyList<PowerPlan> _plans = [];
    private bool _loading = true;

    public RepairPage()
    {
        InitializeComponent();
        var admin = Elevation.IsAdministrator;
        if (!admin)
        {
            Body.Children.Insert(2, Ui.AdminBar("repair", "SFC, DISM, redefinir a rede, trocar o DNS e criar planos de energia precisam de administrador."));
        }

        SfcCard.IsEnabled = admin;
        DismCard.IsEnabled = admin;
        NetResetCard.IsEnabled = admin;
        UltimateButton.IsEnabled = admin;

        foreach (var preset in RepairTools.DnsPresets)
        {
            _dns.Items.Add(new RadioButton
            {
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = preset.Name },
                        new TextBlock
                        {
                            Text = preset.Description,
                            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                        },
                    },
                },
                Tag = preset,
                IsEnabled = admin,
            });
        }

        _dns.SelectionChanged += async (_, _) => await OnDnsChanged();
        DnsOptions.Children.Add(_dns);
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var current = await RepairTools.CurrentDnsAsync();
            var match = RepairTools.DnsPresets.FirstOrDefault(p => p.Servers.Length > 0 && p.Servers.Intersect(current).Any());
            DnsCurrent.Text = current.Length == 0 ? "Automático" : match is not null ? $"{match.Name} ({string.Join(", ", current)})" : string.Join(", ", current);
            _dns.SelectedIndex = match is null
                ? (current.Length == 0 || !current.Any(c => c.Contains('.')) ? 0 : -1)
                : RepairTools.DnsPresets.ToList().IndexOf(match);

            _plans = await RepairTools.PowerPlansAsync();
            PowerBox.ItemsSource = _plans.Select(p => p.Name).ToList();
            PowerBox.SelectedIndex = _plans.ToList().FindIndex(p => p.Active);
            UltimateButton.Visibility = _plans.Count > 0 && !_plans.Any(p => p.Name.Contains("Máximo", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase))
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log.Warn("Reparar: carregar estado: " + ex.Message);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Roda uma ação longa mostrando o progresso dentro do próprio cartão.</summary>
    private async Task RunInCard(SettingsCard card, Func<Action<double>, Task<RepairResult>> action)
    {
        var original = card.Content;
        var bar = new ProgressBar { Width = 180, Maximum = 100, IsIndeterminate = true };
        var label = new TextBlock { Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(bar);
        panel.Children.Add(label);
        card.Content = panel;
        RepairResult result;
        try
        {
            result = await action(p => DispatcherQueue.TryEnqueue(() =>
            {
                bar.IsIndeterminate = false;
                bar.Value = p;
                label.Text = $"{p:0}%";
            }));
        }
        catch (Exception ex)
        {
            Log.Error($"Reparar: {card.Header}.", ex);
            result = new RepairResult(false, ex.Message);
        }

        card.Content = original;
        card.Description = result.Message;
    }

    private async void OnSfc(object sender, RoutedEventArgs e) => await RunInCard(SfcCard, p => RepairTools.SfcAsync(p));

    private async void OnDism(object sender, RoutedEventArgs e) => await RunInCard(DismCard, p => RepairTools.DismRestoreAsync(p));

    private async void OnIconCache(object sender, RoutedEventArgs e) => await RunInCard(IconCard, _ => RepairTools.RebuildIconCacheAsync());

    private async void OnRestartExplorer(object sender, RoutedEventArgs e) => await RunInCard(ExplorerCard, async _ =>
    {
        await TweakEngine.RestartExplorerAsync();
        return new RepairResult(true, "Explorador reiniciado.");
    });

    private async void OnFlushDns(object sender, RoutedEventArgs e) => await RunInCard(FlushCard, _ => RepairTools.FlushDnsAsync());

    private async void OnNetReset(object sender, RoutedEventArgs e)
    {
        if (await Ui.ConfirmAsync(XamlRoot, "Redefinir a rede?", "Winsock e TCP/IP voltam ao padrão. As redes Wi-Fi salvas continuam. Depois é preciso reiniciar o PC.", "Redefinir"))
        {
            await RunInCard(NetResetCard, _ => RepairTools.ResetNetworkAsync());
        }
    }

    private async Task OnDnsChanged()
    {
        if (_loading || _dns.SelectedItem is not RadioButton { Tag: DnsPreset preset })
        {
            return;
        }

        var result = await RepairTools.SetDnsAsync(preset);
        DnsCard.Description = result.Message;
        await LoadAsync();
    }

    private async void OnPowerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PowerBox.SelectedIndex < 0 || PowerBox.SelectedIndex >= _plans.Count)
        {
            return;
        }

        var result = await RepairTools.SetPowerPlanAsync(_plans[PowerBox.SelectedIndex]);
        PowerCard.Description = result.Message;
    }

    private async void OnUltimate(object sender, RoutedEventArgs e)
    {
        var result = await RepairTools.CreateUltimatePlanAsync();
        PowerCard.Description = result.Message;
        await LoadAsync();
    }
}
