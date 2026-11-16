using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vapt.Controls;
using Vapt.Core;
using Vapt.Core.Tweaks;

namespace Vapt.Pages;

public sealed partial class TweaksPage : Page
{
    private readonly TweakList _list;

    public TweaksPage()
    {
        InitializeComponent();
        if (!Elevation.IsAdministrator)
        {
            Body.Children.Insert(1, Ui.AdminBar("tweaks", "Os ajustes do sistema (hibernação, GPU, Windows Update…) só podem ser mudados como administrador. Os da sua conta funcionam já."));
        }

        // Privacidade, anúncios e IA ficam no Debloat (agrupados por objetivo); aqui é "como o Windows funciona".
        var moved = new InfoBar
        {
            Severity = InfoBarSeverity.Informational,
            Title = "Privacidade, anúncios e IA estão no Debloat",
            Message = "Lá ficam junto com os apps, serviços e tarefas de coleta de cada assunto.",
            IsClosable = true,
            IsOpen = true,
        };
        var go = new Button { Content = "Abrir o Debloat" };
        go.Click += (_, _) => App.MainWindow?.NavigateTo("debloat");
        moved.ActionButton = go;
        Body.Children.Insert(Body.Children.IndexOf(StatusBar), moved);

        Groups.Children.Add(BuildGameBoost());

        _list = new TweakList(
            TweakCatalog.Available.Where(t => t.Group is TweakGroup.Interface or TweakGroup.Performance or TweakGroup.Gaming).OrderBy(t => t.Group),
            StatusBar,
            groupHeaders: true);
        Groups.Children.Add(_list);
    }

    /// <summary>"Turbo para jogar": fecha o que roda em segundo plano antes de abrir um jogo.</summary>
    private FrameworkElement BuildGameBoost()
    {
        var checks = new List<(BackgroundApp App, CheckBox Check)>();
        var close = new Button { Content = "Fechar selecionados", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var expander = new CommunityToolkit.WinUI.Controls.SettingsExpander
        {
            Header = "Turbo para jogar",
            Description = "Fecha o que roda em segundo plano (OneDrive, Teams, Widgets, Adobe…) antes de abrir um jogo. Volta ao normal quando você reiniciar o PC.",
            HeaderIcon = new FontIcon { Glyph = "" },
            Content = close,
            Margin = new Thickness(0, 0, 0, 8),
        };

        void Fill()
        {
            expander.Items.Clear();
            checks.Clear();
            foreach (var app in GameBoost.List().Take(25))
            {
                var check = new CheckBox { IsChecked = app.Suggested, MinWidth = 0 };
                checks.Add((app, check));
                var detail = $"{Bytes.Format(app.MemoryBytes)} de RAM{(app.Count > 1 ? $" · {app.Count} processos" : "")}{(app.HasWindow ? " · tem janela aberta (vai pedir para fechar)" : "")}";
                expander.Items.Add(new CommunityToolkit.WinUI.Controls.SettingsCard { Header = app.Title, Description = detail, Content = check });
            }
        }

        expander.Expanded += (_, _) => Fill();
        close.Click += async (_, _) =>
        {
            if (checks.Count == 0)
            {
                Fill();
                expander.IsExpanded = true;
                return;
            }

            var chosen = checks.Where(c => c.Check.IsChecked == true).Select(c => c.App).ToList();
            if (chosen.Count == 0)
            {
                return;
            }

            close.IsEnabled = false;
            var (closed, notClosed) = await Task.Run(() => GameBoost.Close(chosen));
            close.IsEnabled = true;
            Fill();
            Ui.ShowStatus(StatusBar, notClosed.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Informational,
                notClosed.Count == 0
                    ? $"{closed.Count} programas fechados. Bom jogo!"
                    : $"{closed.Count} fechados. {string.Join(", ", notClosed)} ainda estão abertos (talvez perguntando se quer salvar).");
        };
        return expander;
    }

    private async void OnUndoAll(object sender, RoutedEventArgs e)
    {
        var applied = TweakEngine.AppliedIds();
        var tweaks = TweakCatalog.All.Where(t => applied.Contains(t.Id)).ToList();
        if (tweaks.Count == 0)
        {
            Ui.ShowStatus(StatusBar, InfoBarSeverity.Informational, "Nenhuma otimização do Vapt está aplicada.");
            return;
        }

        if (!await Ui.ConfirmAsync(XamlRoot, "Desfazer tudo?", $"Volto {tweaks.Count} otimizações aos valores que estavam antes do Vapt.", "Desfazer"))
        {
            return;
        }

        var failed = 0;
        foreach (var t in tweaks)
        {
            try
            {
                await TweakEngine.RevertAsync(t);
            }
            catch (Exception ex)
            {
                failed++;
                Log.Error($"Desfazer {t.Id}.", ex);
            }
        }

        await _list.RefreshAsync();
        Ui.ShowStatus(StatusBar, failed == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
            failed == 0 ? "Tudo voltou ao que era. Reinicie o Explorador ou o PC para ver todas as mudanças." : $"{failed} não puderam ser desfeitas (talvez precisem de administrador).");
    }
}
