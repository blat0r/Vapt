using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Vapt.Core;
using Vapt.Core.Tweaks;

namespace Vapt.Controls;

/// <summary>
/// Lista de otimizações com interruptor (aplica e desfaz na hora, como o app Configurações).
/// Usada na página Otimizações e na aba "IA e Recall" do Debloat.
/// </summary>
internal sealed partial class TweakList : StackPanel
{
    /// <summary>Um ponto de restauração por sessão basta.</summary>
    private static bool _restorePointDone;

    private readonly Dictionary<Tweak, ToggleSwitch> _toggles = [];
    private readonly InfoBar _status;
    private bool _syncing;

    public TweakList(IEnumerable<Tweak> tweaks, InfoBar status, bool groupHeaders)
    {
        _status = status;
        Spacing = (double)Application.Current.Resources["SettingsCardSpacing"];
        TweakGroup? last = null;
        foreach (var tweak in tweaks)
        {
            if (groupHeaders && tweak.Group != last)
            {
                Children.Add(new TextBlock
                {
                    Text = TweakCatalog.GroupName(tweak.Group),
                    Style = (Style)Application.Current.Resources["SectionHeaderStyle"],
                });
                last = tweak.Group;
            }

            Children.Add(BuildCard(tweak));
        }

        Loaded += async (_, _) => await RefreshAsync();
    }

    private FrameworkElement BuildCard(Tweak tweak)
    {
        // "Sim/Não": o nome já diz a ação ("Desligar o ID de anúncio"), então "Ligado" confundiria.
        var toggle = new ToggleSwitch { OnContent = "Sim", OffContent = "Não", IsEnabled = false };
        _toggles[tweak] = toggle;
        toggle.Toggled += async (_, _) => await OnToggled(tweak, toggle);

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        if (tweak.Risk == TweakRisk.Moderate)
        {
            content.Children.Add(new Border
            {
                Padding = new Thickness(8, 2, 8, 3),
                CornerRadius = new CornerRadius(10),
                VerticalAlignment = VerticalAlignment.Center,
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBackgroundBrush"],
                Child = new TextBlock { Text = "Atenção", Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] },
            });
        }

        content.Children.Add(toggle);
        var description = tweak.Description;
        if (tweak.NeedsAdmin && !Elevation.IsAdministrator)
        {
            description += " · Precisa de administrador.";
        }

        return Ui.Card(tweak.Name, description, tweak.Glyph, content);
    }

    public async Task RefreshAsync()
    {
        _syncing = true;
        try
        {
            foreach (var (tweak, toggle) in _toggles)
            {
                toggle.IsOn = await TweakEngine.IsAppliedAsync(tweak);
                toggle.IsEnabled = !tweak.NeedsAdmin || Elevation.IsAdministrator;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private async Task OnToggled(Tweak tweak, ToggleSwitch toggle)
    {
        if (_syncing)
        {
            return;
        }

        var apply = toggle.IsOn;
        toggle.IsEnabled = false;
        try
        {
            if (apply && tweak.NeedsAdmin)
            {
                await EnsureRestorePointAsync();
            }

            if (apply)
            {
                await TweakEngine.ApplyAsync(tweak);
            }
            else
            {
                await TweakEngine.RevertAsync(tweak);
            }

            if (tweak.RestartExplorer)
            {
                var restart = new Button { Content = "Reiniciar o Explorador" };
                restart.Click += async (_, _) =>
                {
                    _status.IsOpen = false;
                    await TweakEngine.RestartExplorerAsync();
                };
                Show(InfoBarSeverity.Success, $"\"{tweak.Name}\" {(apply ? "aplicado" : "desfeito")}. Reinicie o Explorador para ver a mudança.", restart);
            }
            else if (tweak.RequiresReboot)
            {
                Show(InfoBarSeverity.Success, $"\"{tweak.Name}\" {(apply ? "aplicado" : "desfeito")}. Vale depois de reiniciar o PC.", null);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Otimização {tweak.Id}.", ex);
            Show(InfoBarSeverity.Error, $"Não foi possível mudar \"{tweak.Name}\": {ex.Message}", null);
        }
        finally
        {
            _syncing = true;
            toggle.IsOn = await TweakEngine.IsAppliedAsync(tweak);
            toggle.IsEnabled = !tweak.NeedsAdmin || Elevation.IsAdministrator;
            _syncing = false;
        }
    }

    /// <summary>Cria o ponto de restauração antes da primeira mudança de sistema da sessão.</summary>
    public static async Task EnsureRestorePointAsync(InfoBar? status = null)
    {
        if (!App.Settings.RestorePointBeforeTweaks || _restorePointDone || !Elevation.IsAdministrator)
        {
            return;
        }

        if (status is not null)
        {
            Ui.ShowStatus(status, InfoBarSeverity.Informational, "Criando um ponto de restauração antes da primeira mudança…");
        }

        _restorePointDone = true;
        if (!await TweakEngine.CreateRestorePointAsync("Vapt: antes das otimizações") && status is not null)
        {
            Ui.ShowStatus(status, InfoBarSeverity.Warning, "Não deu para criar o ponto de restauração (a Proteção do Sistema pode estar desligada). As mudanças podem ser desfeitas pelo Vapt.");
        }
        else if (status is not null)
        {
            status.IsOpen = false;
        }
    }

    private void Show(InfoBarSeverity severity, string message, ButtonBase? action) => Ui.ShowStatus(_status, severity, message, action);
}
