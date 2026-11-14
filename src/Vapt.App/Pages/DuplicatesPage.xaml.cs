using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vapt.Controls;
using Vapt.Core;
using Vapt.Core.Files;

namespace Vapt.Pages;

public sealed partial class DuplicatesPage : Page
{
    private static readonly long[] MinSizes = [100 * 1024, 1024 * 1024, 10 * 1024 * 1024, 100 * 1024 * 1024];
    private const int MaxGroupsShown = 300;

    private readonly List<(DuplicateGroup Group, List<(FileInfo File, CheckBox Check)> Rows)> _groups = [];
    private string? _folder;
    private CancellationTokenSource? _cts;

    public DuplicatesPage()
    {
        InitializeComponent();
        MinSizeBox.SelectedIndex = 1;
        _folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        FolderText.Text = Path.GetFileName(_folder);
        ScanButton.IsEnabled = true;
        Loaded += (_, _) =>
        {
            // "--dupes <pasta>": procura direto (atalhos e testes).
            var arg = Environment.GetCommandLineArgs().SkipWhile(a => a != "--dupes").Skip(1).FirstOrDefault();
            if (arg is not null && Directory.Exists(arg))
            {
                _folder = arg;
                FolderText.Text = Path.GetFileName(arg.TrimEnd('\\'));
                OnScan(this, new RoutedEventArgs());
            }
        };
        Unloaded += (_, _) => _cts?.Cancel();
    }

    private void OnPickFolder(object sender, RoutedEventArgs e)
    {
        var folder = NativeDialogs.PickFolder(Ui.WindowHandle, "Onde procurar duplicados?");
        if (folder is not null)
        {
            _folder = folder;
            FolderText.Text = Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } n ? n : folder;
            ScanButton.IsEnabled = true;
        }
    }

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        if (_cts is { IsCancellationRequested: false })
        {
            _cts.Cancel();
            return;
        }

        if (_folder is null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Groups.Children.Clear();
        _groups.Clear();
        EmptyState.Visibility = Visibility.Collapsed;
        SummaryBar.Visibility = Visibility.Visible;
        SummaryText.Text = "Procurando…";
        SummaryDetail.Text = _folder;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        DeleteButton.Visibility = Visibility.Collapsed;
        SelectCopiesButton.Visibility = Visibility.Collapsed;
        ScanText.Text = "Parar";

        IReadOnlyList<DuplicateGroup> found;
        try
        {
            var progress = new Progress<DuplicateProgress>(p =>
            {
                SummaryText.Text = p.Stage;
                SummaryDetail.Text = $"{p.Files:N0} arquivos vistos";
                Progress.IsIndeterminate = p.Fraction <= 0;
                Progress.Value = p.Fraction;
            });
            found = await DuplicateFinder.FindAsync(_folder, MinSizes[Math.Max(0, MinSizeBox.SelectedIndex)], progress, token);
        }
        catch (OperationCanceledException)
        {
            SummaryText.Text = "Busca cancelada";
            Progress.Visibility = Visibility.Collapsed;
            ScanText.Text = "Procurar";
            _cts = null;
            return;
        }

        _cts = null;
        ScanText.Text = "Procurar";
        Progress.Visibility = Visibility.Collapsed;
        ShowResults(found);
    }

    private void ShowResults(IReadOnlyList<DuplicateGroup> found)
    {
        if (found.Count == 0)
        {
            SummaryBar.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            EmptyTitle.Text = "Nenhum duplicado aqui";
            EmptyDetail.Text = "Não há dois arquivos com o mesmo conteúdo nessa pasta (com o tamanho mínimo escolhido).";
            return;
        }

        var wasted = found.Sum(g => g.Wasted);
        SummaryText.Text = $"{Bytes.Format(wasted)} em cópias repetidas";
        SummaryDetail.Text = $"{found.Count:N0} grupos de arquivos idênticos{(found.Count > MaxGroupsShown ? $" (mostrando os {MaxGroupsShown} maiores)" : "")}. A primeira cópia de cada grupo fica; marque o que quer apagar.";
        DeleteButton.Visibility = Visibility.Visible;
        SelectCopiesButton.Visibility = Visibility.Visible;

        foreach (var group in found.Take(MaxGroupsShown))
        {
            var rows = new List<(FileInfo, CheckBox)>();
            var expander = new SettingsExpander
            {
                Header = $"{group.Files[0].Name}",
                Description = $"{group.Files.Count} cópias de {Bytes.Format(group.Size)} · {Bytes.Format(group.Wasted)} repetidos",
                HeaderIcon = new FontIcon { Glyph = "" },
                IsExpanded = false,
            };
            var first = true;
            foreach (var file in group.Files)
            {
                var check = new CheckBox { IsChecked = false, MinWidth = 0 };
                check.Checked += (_, _) => UpdateDelete();
                check.Unchecked += (_, _) => UpdateDelete();
                var reveal = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Style = (Style)Application.Current.Resources["SubtleButtonStyle"] };
                ToolTipService.SetToolTip(reveal, "Mostrar no Explorador");
                reveal.Click += (_, _) => Ui.RevealInExplorer(file.FullName);
                var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                if (first)
                {
                    content.Children.Add(new Border
                    {
                        Padding = new Thickness(8, 2, 8, 3),
                        CornerRadius = new CornerRadius(10),
                        VerticalAlignment = VerticalAlignment.Center,
                        Background = (Brush)Application.Current.Resources["SystemFillColorSuccessBackgroundBrush"],
                        Child = new TextBlock { Text = "Original", Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] },
                    });
                }

                content.Children.Add(reveal);
                content.Children.Add(check);
                expander.Items.Add(new SettingsCard
                {
                    Header = Path.GetDirectoryName(file.FullName) ?? file.FullName,
                    Description = $"Modificado em {file.LastWriteTime:dd/MM/yyyy HH:mm}",
                    Content = content,
                });
                rows.Add((file, check));
                first = false;
            }

            _groups.Add((group, rows));
            Groups.Children.Add(expander);
        }

        UpdateDelete();
    }

    private void OnSelectCopies(object sender, RoutedEventArgs e)
    {
        foreach (var (_, rows) in _groups)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                rows[i].Check.IsChecked = i > 0;
            }
        }
    }

    private void UpdateDelete()
    {
        var chosen = _groups.SelectMany(g => g.Rows).Where(r => r.Check.IsChecked == true).ToList();
        var bytes = chosen.Sum(r => r.File.Length);
        DeleteButton.Content = chosen.Count == 0 ? "Apagar marcados" : $"Apagar {chosen.Count} ({Bytes.Format(bytes)})";
        DeleteButton.IsEnabled = chosen.Count > 0;
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        // Segurança: nunca apaga todas as cópias de um grupo.
        var allMarked = _groups.Where(g => g.Rows.All(r => r.Check.IsChecked == true)).ToList();
        foreach (var g in allMarked)
        {
            g.Rows[0].Check.IsChecked = false;
        }

        var chosen = _groups.SelectMany(g => g.Rows).Where(r => r.Check.IsChecked == true).Select(r => r.File).ToList();
        if (chosen.Count == 0)
        {
            return;
        }

        var note = allMarked.Count > 0 ? $"\n\nEm {allMarked.Count} grupo(s) você marcou todas as cópias; deixei a original." : "";
        if (!await Ui.ConfirmAsync(XamlRoot, "Apagar as cópias?", $"{chosen.Count} arquivos ({Bytes.Format(chosen.Sum(f => f.Length))}) vão para a Lixeira. Uma cópia de cada um continua no lugar.{note}", "Mover para a Lixeira", destructive: true))
        {
            return;
        }

        var failed = 0;
        long freed = 0;
        await Task.Run(() =>
        {
            foreach (var file in chosen)
            {
                try
                {
                    var size = file.Length;
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file.FullName, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    freed += size;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    failed++;
                }
            }
        });

        Log.Info($"Duplicados: {chosen.Count - failed} apagados ({Bytes.Format(freed)}).");
        await Ui.InfoAsync(XamlRoot, "Pronto", $"{Bytes.Format(freed)} liberados (estão na Lixeira até você esvaziar).{(failed > 0 ? $" {failed} arquivos em uso ficaram." : "")}");
        OnScan(this, new RoutedEventArgs());
    }
}
