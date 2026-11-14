using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vapt.Controls;
using Vapt.Core;
using Vapt.Core.Startup;

namespace Vapt.Pages;

public sealed partial class StartupPage : Page
{
    public StartupPage()
    {
        InitializeComponent();
        if (!Elevation.IsAdministrator)
        {
            Body.Children.Insert(2, Ui.AdminBar("startup", "Itens de \"todos os usuários\" só podem ser mudados como administrador."));
        }

        Load();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Load();

    private void Load()
    {
        List.Children.Clear();
        IReadOnlyList<StartupEntry> entries;
        try
        {
            entries = StartupManager.List();
        }
        catch (Exception ex)
        {
            Log.Error("Listar inicialização.", ex);
            entries = [];
        }

        var on = entries.Count(e => e.Enabled);
        CountText.Text = entries.Count == 0 ? "Nada abre sozinho com o Windows." : $"{on} de {entries.Count} ligados";
        foreach (var entry in entries)
        {
            List.Children.Add(BuildCard(entry));
        }
    }

    private FrameworkElement BuildCard(StartupEntry entry)
    {
        var locked = entry.NeedsAdmin && !Elevation.IsAdministrator;
        var toggle = new ToggleSwitch { IsOn = entry.Enabled, IsEnabled = !locked, OnContent = "Ligado", OffContent = "Desligado" };
        toggle.Toggled += (_, _) =>
        {
            try
            {
                StartupManager.SetEnabled(entry, toggle.IsOn);
                UpdateCount();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                Log.Error("Inicialização.", ex);
                toggle.IsOn = !toggle.IsOn;
            }
        };

        var more = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
        };
        var menu = new MenuFlyout();
        var reveal = new MenuFlyoutItem { Text = "Abrir local do arquivo", Icon = new FontIcon { Glyph = "" }, IsEnabled = entry.ExecutablePath is not null && File.Exists(entry.ExecutablePath) };
        reveal.Click += (_, _) => Ui.RevealInExplorer(entry.ExecutablePath!);
        var copy = new MenuFlyoutItem { Text = "Copiar comando", Icon = new FontIcon { Glyph = "" } };
        copy.Click += (_, _) =>
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(entry.Command);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        };
        var remove = new MenuFlyoutItem { Text = "Remover da inicialização", Icon = new FontIcon { Glyph = "" }, IsEnabled = !locked };
        remove.Click += async (_, _) =>
        {
            if (await Ui.ConfirmAsync(XamlRoot, "Remover da inicialização?", $"\"{entry.Name}\" sai da lista de vez (o programa continua instalado). Para só pausar, use o botão Ligado/Desligado.", "Remover", destructive: true))
            {
                try
                {
                    StartupManager.Remove(entry);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    await Ui.InfoAsync(XamlRoot, "Não foi possível remover", ex.Message);
                }

                Load();
            }
        };
        menu.Items.Add(reveal);
        menu.Items.Add(copy);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(remove);
        more.Flyout = menu;

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(toggle);
        content.Children.Add(more);

        var description = string.Join(" · ", new[] { entry.Publisher, entry.SourceLabel }.Where(s => !string.IsNullOrEmpty(s)));
        var card = Ui.Card(entry.Name, description, "", content);
        ToolTipService.SetToolTip(card, entry.Command);
        FileIcons.Apply(card, entry.ExecutablePath);
        return card;
    }

    private void UpdateCount()
    {
        var all = StartupManager.List();
        CountText.Text = $"{all.Count(e => e.Enabled)} de {all.Count} ligados";
    }
}
