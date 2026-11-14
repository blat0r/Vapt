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

        _list = new TweakList(
            TweakCatalog.All.Where(t => t.Group is TweakGroup.Interface or TweakGroup.Performance or TweakGroup.Gaming).OrderBy(t => t.Group),
            StatusBar,
            groupHeaders: true);
        Groups.Children.Add(_list);
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
