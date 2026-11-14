using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vapt.Core;
using Vapt.Core.Apps;

namespace Vapt.Controls;

/// <summary>Janela "Sobras encontradas" (como a varredura pós-desinstalação do Revo).</summary>
internal static class LeftoversDialog
{
    /// <summary>Procura e mostra as sobras. Devolve o texto do resultado, ou nulo se nada foi feito.</summary>
    public static async Task<string?> ShowAsync(XamlRoot root, IReadOnlyList<InstalledApp> apps, IReadOnlyCollection<InstalledApp> stillInstalled)
    {
        var found = await Task.Run(() => apps.SelectMany(a => Leftovers.Find(a, stillInstalled)).DistinctBy(l => l.Path, StringComparer.OrdinalIgnoreCase).ToList());
        var theme = App.MainWindow?.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Default;
        var who = apps.Count == 1 ? $"\"{apps[0].Name}\"" : $"{apps.Count} programas";
        if (found.Count == 0)
        {
            await new ContentDialog
            {
                XamlRoot = root,
                Title = "Nenhuma sobra",
                Content = $"Não achei pastas, atalhos nem registro deixados por {who}.",
                CloseButtonText = "OK",
                RequestedTheme = theme,
            }.ShowAsync();
            return null;
        }

        var checks = new List<(Leftover Item, CheckBox Check)>();
        var list = new StackPanel { Spacing = 2 };
        foreach (var item in found.OrderBy(f => f.Kind).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = item.Path, TextTrimming = TextTrimming.CharacterEllipsis, Style = (Style)Application.Current.Resources["BodyTextBlockStyle"] });
            text.Children.Add(new TextBlock
            {
                Text = $"{item.KindLabel} · {item.Reason}{(item.Size > 0 ? " · " + Bytes.Format(item.Size) : "")}",
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            });
            var check = new CheckBox { IsChecked = item.Confident, Content = text, HorizontalAlignment = HorizontalAlignment.Stretch };
            ToolTipService.SetToolTip(check, item.Path);
            checks.Add((item, check));
            list.Children.Add(check);
        }

        var total = found.Sum(f => f.Size);
        var body = new StackPanel { Spacing = 12, Width = 560 };
        body.Children.Add(new TextBlock
        {
            Text = $"{who} deixou {found.Count} {(found.Count == 1 ? "item" : "itens")}{(total > 0 ? $" ({Bytes.Format(total)})" : "")}. Pastas e atalhos vão para a Lixeira; chaves do registro ganham um backup .reg antes de sair.",
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 360 });

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Sobras encontradas",
            Content = body,
            PrimaryButtonText = "Apagar selecionados",
            CloseButtonText = "Manter",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = theme,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        var chosen = checks.Where(c => c.Check.IsChecked == true).Select(c => c.Item).ToList();
        if (chosen.Count == 0)
        {
            return null;
        }

        var (removed, freed, failed) = await Leftovers.RemoveAsync(chosen);
        return failed.Count == 0
            ? $"{removed} sobras apagadas{(freed > 0 ? $" ({Bytes.Format(freed)})" : "")}."
            : $"{removed} sobras apagadas; {failed.Count} em uso ou sem permissão (tente como administrador).";
    }
}
