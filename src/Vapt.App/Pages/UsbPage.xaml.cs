using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vapt.Controls;
using Vapt.Core;
using Vapt.Core.Usb;

namespace Vapt.Pages;

public sealed partial class UsbPage : Page
{
    private static readonly int[] ClusterSizes = [0, 4096, 8192, 16384, 32768, 65536];

    private ImageInfo? _image;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _loading = true;

    public UsbPage()
    {
        InitializeComponent();
        Body.Children.Insert(0, Ui.AdminBar("usb", "Para apagar e gravar o pendrive, o Vapt precisa rodar como administrador."));

        foreach (var size in ClusterSizes)
        {
            ClusterBox.Items.Add(size == 0 ? "Padrão" : size >= 1024 ? $"{size / 1024} KB" : $"{size} bytes");
        }

        ClusterBox.SelectedIndex = 0;
        BootBox.SelectedIndex = 0;
        ModeBox.SelectedIndex = 0;
        SchemeBox.SelectedIndex = 0;
        FsBox.SelectedIndex = 0;
        _loading = false;
        UpdateVisibility();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private UsbDisk? SelectedDisk => DiskBox.SelectedItem as UsbDisk;

    private bool WantsImage => BootBox.SelectedIndex == 1;

    private bool RawMode => WantsImage && ModeBox.SelectedIndex == 1;

    // ── Lista de pendrives ───────────────────────────────────────────────

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_busy)
        {
            return;
        }

        var previous = SelectedDisk?.UniqueId;
        StatusText.Text = "Procurando pendrives…";
        try
        {
            var disks = await UsbService.ListAsync();
            DiskBox.ItemsSource = disks;
            DiskBox.SelectedItem = disks.FirstOrDefault(d => d.UniqueId == previous) ?? disks.FirstOrDefault();
            StatusText.Text = disks.Count switch
            {
                0 => "Nenhum pendrive encontrado. Conecte um e clique em atualizar.",
                1 => "Pronto · 1 pendrive encontrado",
                _ => $"Pronto · {disks.Count} pendrives encontrados",
            };
        }
        catch (Exception ex)
        {
            Log.Error("Listar pendrives.", ex);
            StatusText.Text = "Não foi possível listar os discos.";
        }

        UpdateVisibility();
    }

    private void OnDiskChanged(object sender, SelectionChangedEventArgs e)
    {
        var disk = SelectedDisk;
        ExternalWarning.IsOpen = disk?.LooksLikeExternalDrive == true;
        TargetText.Text = disk is null ? "" : $"Disco {disk.Number} · {disk.BusType} · {disk.PartitionStyle}";
        if (disk is not null && LabelBox.Text is "" or "PENDRIVE")
        {
            LabelBox.Text = "PENDRIVE";
        }

        UpdateStartEnabled();
    }

    // ── Imagem ───────────────────────────────────────────────────────────

    private void OnBootChanged(object sender, SelectionChangedEventArgs e) => UpdateVisibility();

    private void OnModeChanged(object sender, SelectionChangedEventArgs e) => UpdateVisibility();

    private void OnSchemeChanged(object sender, SelectionChangedEventArgs e) => UpdateVisibility();

    private void OnFsChanged(object sender, SelectionChangedEventArgs e) => UpdateVisibility();

    private async void OnPickImage(object sender, RoutedEventArgs e)
    {
        var path = NativeDialogs.PickFile(Ui.WindowHandle, "Escolha a imagem", ("Imagens de disco", "*.iso;*.img;*.raw;*.bin"), ("Todos os arquivos", "*.*"));
        if (path is null)
        {
            return;
        }

        ImageRing.IsActive = true;
        ImageCard.Description = "Lendo a imagem…";
        try
        {
            _image = await UsbService.InspectAsync(path);
        }
        catch (Exception ex)
        {
            Log.Error("Inspecionar imagem.", ex);
            _image = null;
            ImageCard.Description = "Não foi possível ler a imagem.";
        }
        finally
        {
            ImageRing.IsActive = false;
        }

        if (_image is null)
        {
            UpdateVisibility();
            return;
        }

        // Padrões como o Rufus: Windows = copiar arquivos (GPT/FAT32); Linux híbrido e .img = gravar imagem.
        _loading = true;
        if (_image.IsWindows)
        {
            ModeBox.SelectedIndex = 0;
            SchemeBox.SelectedIndex = 1;
            FsBox.SelectedIndex = 0;
            LabelBox.Text = Fat32Formatter.NormalizeLabel(_image.VolumeLabel ?? "WINDOWS").TrimEnd();
            BypassToggle.IsOn = true;
        }
        else
        {
            ModeBox.SelectedIndex = _image.IsHybrid ? 1 : 0;
            if (!string.IsNullOrWhiteSpace(_image.VolumeLabel))
            {
                LabelBox.Text = Fat32Formatter.NormalizeLabel(_image.VolumeLabel).TrimEnd();
            }
        }

        _loading = false;
        UpdateVisibility();
    }

    private void DescribeImage()
    {
        if (_image is null)
        {
            ImageCard.Description = "Nenhuma imagem escolhida";
            return;
        }

        var kind = _image.IsWindows ? "Instalação do Windows" : _image.IsIso ? (_image.IsHybrid ? "ISO híbrida (Linux e outros)" : "ISO") : "Imagem de disco";
        var extra = _image.IsWindows && _image.NeedsSplit && FsBox.SelectedIndex == 0 && !RawMode
            ? " · o install.wim passa de 4 GB e será dividido para caber em FAT32"
            : "";
        ImageCard.Description = $"{Path.GetFileName(_image.Path)} · {Bytes.Format(_image.Size)} · {kind}{extra}";
    }

    // ── Estado da tela ───────────────────────────────────────────────────

    private void UpdateVisibility()
    {
        if (_loading)
        {
            return;
        }

        var image = WantsImage;
        ImageCard.Visibility = Show(image);
        var canChooseMode = image && _image is { IsIso: true, IsHybrid: true } && !_image.IsWindows;
        ModeCard.Visibility = Show(canChooseMode);
        if (image && _image is { IsIso: false })
        {
            ModeBox.SelectedIndex = 1; // .img só grava em modo imagem
        }
        else if (image && _image is { IsWindows: true } or { IsHybrid: false })
        {
            ModeBox.SelectedIndex = 0;
        }

        var raw = RawMode;
        WindowsCard.Visibility = Show(image && _image?.IsWindows == true && !raw);

        // No modo imagem o layout vem da própria imagem.
        foreach (var element in new FrameworkElement[] { PartitionHeader, SchemeCard, FormatHeader, LabelCard, FsCard, ClusterCard, QuickCard })
        {
            element.Visibility = Show(!raw);
        }

        VerifyCard.Visibility = Show(raw);
        QuickCard.Visibility = Show(!raw && !image);
        SchemeCard.Description = SchemeBox.SelectedIndex == 1
            ? "Sistema de destino: UEFI (PCs dos últimos ~10 anos)"
            : "Sistema de destino: BIOS ou UEFI (mais compatível)";
        FsCard.Description = FsBox.SelectedIndex switch
        {
            0 => image ? "Necessário para dar boot em UEFI." : "Funciona em tudo (TV, carro, videogame). Arquivos de até 4 GB.",
            1 => "Arquivos grandes e funciona em Windows, Mac e na maioria das TVs novas.",
            _ => "Arquivos grandes, só Windows grava sem programas extras.",
        };
        DescribeImage();
        UpdateStartEnabled();
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateStartEnabled()
    {
        StartButton.IsEnabled = !_busy && SelectedDisk is not null && (!WantsImage || _image is not null);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Body.IsHitTestVisible = !busy;
        Body.Opacity = busy ? 0.6 : 1;
        CancelButton.Visibility = Show(busy);
        UpdateStartEnabled();
    }

    // ── Iniciar ──────────────────────────────────────────────────────────

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (SelectedDisk is not { } disk)
        {
            return;
        }

        if (!Elevation.IsAdministrator)
        {
            if (await Ui.ConfirmAsync(XamlRoot, "Precisa de administrador", "Para apagar e gravar o pendrive, o Vapt precisa reabrir como administrador.", "Reabrir"))
            {
                App.RestartAsAdmin("usb");
            }

            return;
        }

        if (!await ConfirmWipeAsync(disk))
        {
            return;
        }

        var job = new UsbJob
        {
            Disk = disk,
            Scheme = SchemeBox.SelectedIndex == 1 ? PartitionScheme.Gpt : PartitionScheme.Mbr,
            FileSystem = FsBox.SelectedIndex switch
            {
                1 => UsbFileSystem.ExFat,
                2 => UsbFileSystem.Ntfs,
                _ => UsbFileSystem.Fat32,
            },
            ClusterSize = ClusterSizes[Math.Max(0, ClusterBox.SelectedIndex)],
            Label = LabelBox.Text,
            QuickFormat = QuickToggle.IsOn,
            ImagePath = WantsImage ? _image?.Path : null,
            Mode = RawMode ? ImageMode.Raw : ImageMode.Extract,
            Verify = VerifyToggle.IsOn,
            SetupTweaks = WindowsCard.Visibility == Visibility.Visible
                ? new WindowsSetupTweaks(BypassToggle.IsOn, LocalAccountToggle.IsOn, BitLockerToggle.IsOn)
                : null,
        };

        _cts = new CancellationTokenSource();
        SetBusy(true);
        Progress.ShowError = false;
        Progress.Value = 0;
        var started = DateTime.Now;
        var progress = new Progress<UsbProgress>(p =>
        {
            Progress.Value = p.Percent;
            StatusText.Text = p.Status;
        });

        try
        {
            var letter = await UsbService.RunAsync(job, progress, _cts.Token);
            Progress.Value = 100;
            var took = DateTime.Now - started;
            StatusText.Text = $"Pronto em {took:mm\\:ss}" + (letter is null ? "" : $" · pendrive em {letter}:");
            Log.Info($"Pendrive concluído em {took}.");
        }
        catch (OperationCanceledException)
        {
            Progress.ShowError = true;
            StatusText.Text = "Cancelado. O pendrive pode ter ficado sem formatação: formate de novo antes de usar.";
        }
        catch (Exception ex)
        {
            Log.Error("Pendrive falhou.", ex);
            Progress.ShowError = true;
            StatusText.Text = "Falhou: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
            _cts = null;
            await RefreshAsync();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private async Task<bool> ConfirmWipeAsync(UsbDisk disk)
    {
        var check = new CheckBox { Content = "Entendo que tudo neste pendrive será apagado" };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = $"Todos os arquivos em \"{disk.Name}\" ({Bytes.Format(disk.Size)}{(string.IsNullOrEmpty(disk.Letters) ? "" : ", " + disk.Letters + ":")}) serão apagados. Não dá para desfazer.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(check);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Apagar o pendrive?",
            Content = content,
            PrimaryButtonText = "Apagar e continuar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
            RequestedTheme = ActualTheme,
        };
        check.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
        check.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
