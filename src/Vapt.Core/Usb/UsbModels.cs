namespace Vapt.Core.Usb;

/// <summary>Um disco removível (pendrive, cartão SD) que pode ser apagado.</summary>
public sealed record UsbDisk(
    int Number,
    string UniqueId,
    string Name,
    long Size,
    string BusType,
    string PartitionStyle,
    string Letters,
    bool ReadOnly)
{
    /// <summary>USB grande demais para ser pendrive: provavelmente um HD/SSD externo.</summary>
    public bool LooksLikeExternalDrive => Size > 300L * 1000 * 1000 * 1000;

    public string Display
    {
        get
        {
            var letters = string.IsNullOrEmpty(Letters) ? "" : $" ({string.Join(", ", Letters.Split(',').Select(l => l + ":"))})";
            return $"{Name}{letters} · {Bytes.Format(Size)}";
        }
    }

    public override string ToString() => Display;
}

public enum PartitionScheme
{
    /// <summary>MBR: liga em PCs antigos (BIOS) e em UEFI.</summary>
    Mbr,
    /// <summary>GPT: só UEFI, padrão dos PCs atuais.</summary>
    Gpt,
}

public enum UsbFileSystem
{
    Fat32,
    ExFat,
    Ntfs,
}

public enum ImageMode
{
    /// <summary>Formata e copia os arquivos da ISO (Windows e a maioria das distros UEFI).</summary>
    Extract,
    /// <summary>Grava a imagem byte a byte no disco (ISOs híbridas de Linux e .img).</summary>
    Raw,
}

/// <summary>Opções do instalador do Windows 11, gravadas num autounattend.xml.</summary>
public sealed record WindowsSetupTweaks(
    bool BypassRequirements,
    bool AllowLocalAccount,
    bool DisableBitLocker)
{
    public bool Any => BypassRequirements || AllowLocalAccount || DisableBitLocker;
}

public sealed record UsbJob
{
    public required UsbDisk Disk { get; init; }

    public PartitionScheme Scheme { get; init; } = PartitionScheme.Mbr;

    public UsbFileSystem FileSystem { get; init; } = UsbFileSystem.Fat32;

    /// <summary>Tamanho do cluster em bytes; 0 = padrão.</summary>
    public int ClusterSize { get; init; }

    public string Label { get; init; } = "PENDRIVE";

    public bool QuickFormat { get; init; } = true;

    /// <summary>ISO/IMG a gravar, ou nulo para só formatar.</summary>
    public string? ImagePath { get; init; }

    public ImageMode Mode { get; init; } = ImageMode.Extract;

    public WindowsSetupTweaks? SetupTweaks { get; init; }

    /// <summary>Lê de volta e compara depois de gravar uma imagem no modo Raw.</summary>
    public bool Verify { get; init; }
}

public readonly record struct UsbProgress(double Percent, string Status);
