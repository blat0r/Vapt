using System.Diagnostics;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Vapt;

/// <summary>Fábricas e utilidades de interface repetidas nas páginas.</summary>
internal static class Ui
{
    public static nint WindowHandle => App.MainWindow is { } w ? WinRT.Interop.WindowNative.GetWindowHandle(w) : 0;

    public static SettingsCard Card(string header, string? description, string glyph, FrameworkElement? content) => new()
    {
        Header = header,
        Description = description!,
        HeaderIcon = new FontIcon { Glyph = glyph },
        Content = content!,
    };

    public static TextBlock Text(string text, string style = "BodyTextBlockStyle", bool secondary = false) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources[style],
        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[secondary ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush"],
        TextWrapping = TextWrapping.Wrap,
    };

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string primary, bool destructive = false)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            CloseButtonText = "Cancelar",
            DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary,
            RequestedTheme = App.MainWindow?.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Default,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task InfoAsync(XamlRoot root, string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            RequestedTheme = App.MainWindow?.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Default,
        };
        await dialog.ShowAsync();
    }

    /// <summary>Aviso "precisa de administrador" com botão que reabre o Vapt elevado.</summary>
    public static InfoBar AdminBar(string pageTag, string message)
    {
        var bar = new InfoBar
        {
            Severity = InfoBarSeverity.Warning,
            Title = "Precisa de administrador",
            Message = message,
            IsClosable = false,
            IsOpen = !Core.Elevation.IsAdministrator,
            Margin = new Thickness(0, 0, 0, 8),
        };
        var button = new Button { Content = "Reiniciar como administrador" };
        button.Click += (_, _) => App.RestartAsAdmin(pageTag);
        bar.ActionButton = button;
        return bar;
    }

    public static void ShowStatus(InfoBar bar, InfoBarSeverity severity, string message, Microsoft.UI.Xaml.Controls.Primitives.ButtonBase? action = null)
    {
        bar.Severity = severity;
        bar.Message = message;
        bar.ActionButton = action;
        bar.IsOpen = true;
    }

    public static void RevealInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Core.Log.Warn($"Explorer: {ex.Message}");
        }
    }

    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Core.Log.Warn($"Abrir {target}: {ex.Message}");
        }
    }
}
