using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vapt.Core;

namespace Vapt.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly bool _loading;

    public SettingsPage()
    {
        _loading = true;
        InitializeComponent();
        var s = App.Settings;
        ThemeBox.SelectedIndex = (int)s.Theme;
        RestoreToggle.IsOn = s.RestorePointBeforeTweaks;
        RecycleToggle.IsOn = s.DeleteToRecycleBin;
        AlwaysAdminToggle.IsOn = s.AlwaysAdmin;
        AutoUpdateToggle.IsOn = s.AutoUpdate;
        if (!Updater.IsInstalled)
        {
            UpdateCard.Description = "Disponível no Vapt instalado (pelo instalador do GitHub). Esta cópia é de desenvolvimento.";
            CheckUpdateButton.IsEnabled = false;
        }
        var admin = Elevation.IsAdministrator;
        AdminCard.Description = admin
            ? "O Vapt está rodando como administrador: todas as funções estão liberadas."
            : "Limpeza do sistema, otimizações do sistema e o pendrive precisam de administrador.";
        AdminButton.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;
        var version = typeof(App).Assembly.GetName().Version;
        VersionText.Text = $"Versão {version?.ToString(3)}";
        _loading = false;
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        App.Settings.Theme = (AppTheme)ThemeBox.SelectedIndex;
        App.Settings.Save();
        App.MainWindow?.ApplyTheme();
    }

    private void OnRestoreToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            App.Settings.RestorePointBeforeTweaks = RestoreToggle.IsOn;
            App.Settings.Save();
        }
    }

    private void OnRecycleToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            App.Settings.DeleteToRecycleBin = RecycleToggle.IsOn;
            App.Settings.Save();
        }
    }

    private void OnAdmin(object sender, RoutedEventArgs e) => App.RestartAsAdmin("settings");

    private void OnAutoUpdateToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            App.Settings.AutoUpdate = AutoUpdateToggle.IsOn;
            App.Settings.Save();
        }
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        CheckUpdateButton.Content = "Procurando…";
        var state = App.MainWindow is { } w ? await w.CheckUpdatesAsync() : UpdateState.Failed;
        UpdateCard.Description = state switch
        {
            UpdateState.UpToDate => $"Você já tem a versão mais nova ({Updater.CurrentVersion}).",
            UpdateState.Ready => $"A versão {Updater.ReadyVersion} foi baixada. Use \"Reiniciar e atualizar\" no aviso do topo.",
            UpdateState.Failed => "Não deu para falar com o GitHub agora. Confira a internet e tente de novo.",
            _ => UpdateCard.Description,
        };
        CheckUpdateButton.Content = "Procurar agora";
        CheckUpdateButton.IsEnabled = true;
    }

    private void OnAlwaysAdminToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            App.Settings.AlwaysAdmin = AlwaysAdminToggle.IsOn;
            App.Settings.Save();
        }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);
        Ui.Open(AppPaths.LogDirectory);
    }

    private void OnOpenData(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        Ui.Open(AppPaths.DataDirectory);
    }
}
