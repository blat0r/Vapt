using System.Buffers.Binary;
using System.Text;

namespace Vapt.Core.Usb;

/// <summary>Destino de escrita por setor (um volume de verdade ou um arquivo nos testes).</summary>
public interface ISectorDevice
{
    int BytesPerSector { get; }

    long TotalSectors { get; }

    /// <summary>Setores antes da partição (vai no campo HiddSec do setor de boot).</summary>
    long HiddenSectors { get; }

    /// <summary>Escreve a partir de um setor; o tamanho do buffer é múltiplo do setor.</summary>
    void Write(long sector, ReadOnlySpan<byte> data);
}

/// <summary>Valores calculados de um FAT32 (expostos para teste e para a interface).</summary>
public sealed record Fat32Layout(
    int BytesPerSector,
    int SectorsPerCluster,
    int ReservedSectors,
    long FatSectors,
    long TotalSectors,
    long ClusterCount)
{
    public long DataStartSector => ReservedSectors + 2 * FatSectors;

    public int ClusterBytes => BytesPerSector * SectorsPerCluster;
}

/// <summary>
/// Formata FAT32 em volumes de qualquer tamanho (o Windows recusa acima de 32 GB). Segue a
/// especificação da Microsoft (fatgen103): 32 setores reservados, 2 FATs, raiz no cluster 2,
/// FSInfo no setor 1 e cópia do boot no setor 6. Código próprio, sem trechos do Rufus.
/// </summary>
public static class Fat32Formatter
{
    public const long MaxClusters = 0x0FFFFFF5 - 2;
    public const long MinClusters = 65525;

    /// <summary>Cluster padrão pelo tamanho do volume (parecido com o que o Rufus escolhe).</summary>
    public static int DefaultClusterBytes(long volumeBytes)
    {
        const long GB = 1L << 30;
        return volumeBytes switch
        {
            < 8 * GB => 4096,
            < 16 * GB => 8192,
            < 32 * GB => 16384,
            _ => 32768,
        };
    }

    public static Fat32Layout Plan(int bytesPerSector, long totalSectors, int clusterBytes)
    {
        if (clusterBytes <= 0)
        {
            clusterBytes = DefaultClusterBytes(totalSectors * bytesPerSector);
        }

        if (clusterBytes % bytesPerSector != 0 || clusterBytes > 64 * 1024)
        {
            throw new ArgumentException($"Cluster de {clusterBytes} bytes não serve para setores de {bytesPerSector}.");
        }

        if (totalSectors > uint.MaxValue)
        {
            throw new InvalidOperationException("FAT32 não aceita volumes com mais de 2³² setores (2 TB com setores de 512 bytes).");
        }

        var spc = clusterBytes / bytesPerSector;
        const int reserved = 32;
        const int fats = 2;

        // fatgen103: tamanho da FAT (em setores) com 4 bytes por entrada.
        var numerator = 4L * (totalSectors - reserved);
        var denominator = (long)spc * bytesPerSector + 4L * fats;
        var fatSectors = numerator / denominator + 1;

        var clusters = (totalSectors - reserved - fats * fatSectors) / spc;
        if (clusters > MaxClusters)
        {
            throw new InvalidOperationException("Clusters demais para FAT32. Escolha um cluster maior.");
        }

        if (clusters < MinClusters)
        {
            throw new InvalidOperationException("Volume pequeno demais para FAT32 com esse cluster. Escolha um cluster menor.");
        }

        return new Fat32Layout(bytesPerSector, spc, reserved, fatSectors, totalSectors, clusters);
    }

    public static Fat32Layout Format(ISectorDevice device, string label, int clusterBytes, IProgress<double>? progress = null, CancellationToken cancellation = default)
    {
        var bps = device.BytesPerSector;
        var layout = Plan(bps, device.TotalSectors, clusterBytes);
        var volumeId = (uint)Random.Shared.Next() ^ (uint)Environment.TickCount;
        var label11 = NormalizeLabel(label);

        // 1) Zera reservados, as duas FATs e o cluster da raiz (só o necessário: "rápido").
        var zeroUntil = layout.DataStartSector + layout.SectorsPerCluster;
        var chunkSectors = Math.Max(1, (1 << 20) / bps); // 1 MB por escrita
        var zero = new byte[chunkSectors * bps];
        for (long s = 0; s < zeroUntil; s += chunkSectors)
        {
            cancellation.ThrowIfCancellationRequested();
            var count = (int)Math.Min(chunkSectors, zeroUntil - s);
            device.Write(s, zero.AsSpan(0, count * bps));
            progress?.Report(0.9 * (s + count) / zeroUntil);
        }

        // 2) Setor de boot, FSInfo e as cópias (6 e 7).
        var boot = BuildBootSector(layout, device.HiddenSectors, volumeId, label11);
        var fsInfo = BuildFsInfo(layout, bps);
        var trail = new byte[bps];
        BinaryPrimitives.WriteUInt32LittleEndian(trail.AsSpan(bps - 4), 0xAA550000);
        device.Write(0, boot);
        device.Write(1, fsInfo);
        device.Write(2, trail);
        device.Write(6, boot);
        device.Write(7, fsInfo);
        device.Write(8, trail);

        // 3) Início de cada FAT: mídia, fim de cadeia e a raiz (cluster 2) ocupada.
        var fatHead = new byte[bps];
        BinaryPrimitives.WriteUInt32LittleEndian(fatHead.AsSpan(0), 0x0FFFFFF8);
        BinaryPrimitives.WriteUInt32LittleEndian(fatHead.AsSpan(4), 0x0FFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(fatHead.AsSpan(8), 0x0FFFFFFF);
        device.Write(layout.ReservedSectors, fatHead);
        device.Write(layout.ReservedSectors + layout.FatSectors, fatHead);

        // 4) Rótulo como primeira entrada da raiz (é o que o Explorador mostra).
        var root = new byte[bps];
        Encoding.ASCII.GetBytes(label11).CopyTo(root, 0);
        root[11] = 0x08; // ATTR_VOLUME_ID
        device.Write(layout.DataStartSector, root);

        progress?.Report(1);
        return layout;
    }

    internal static byte[] BuildBootSector(Fat32Layout l, long hiddenSectors, uint volumeId, string label11)
    {
        var b = new byte[l.BytesPerSector];
        var s = b.AsSpan();
        b[0] = 0xEB;
        b[1] = 0x58;
        b[2] = 0x90;
        Encoding.ASCII.GetBytes("MSWIN4.1").CopyTo(b, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(s[11..], (ushort)l.BytesPerSector);
        b[13] = (byte)l.SectorsPerCluster;
        BinaryPrimitives.WriteUInt16LittleEndian(s[14..], (ushort)l.ReservedSectors);
        b[16] = 2; // número de FATs
        // 17: RootEntCnt = 0, 19: TotSec16 = 0 (FAT32)
        b[21] = 0xF8; // mídia fixa
        // 22: FATSz16 = 0
        BinaryPrimitives.WriteUInt16LittleEndian(s[24..], 63);
        BinaryPrimitives.WriteUInt16LittleEndian(s[26..], 255);
        BinaryPrimitives.WriteUInt32LittleEndian(s[28..], (uint)Math.Min(hiddenSectors, uint.MaxValue));
        BinaryPrimitives.WriteUInt32LittleEndian(s[32..], (uint)l.TotalSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(s[36..], (uint)l.FatSectors);
        // 40: ExtFlags = 0 (FATs espelhadas), 42: versão 0.0
        BinaryPrimitives.WriteUInt32LittleEndian(s[44..], 2); // cluster da raiz
        BinaryPrimitives.WriteUInt16LittleEndian(s[48..], 1); // setor do FSInfo
        BinaryPrimitives.WriteUInt16LittleEndian(s[50..], 6); // cópia do boot
        b[64] = 0x80; // drive
        b[66] = 0x29; // assinatura estendida
        BinaryPrimitives.WriteUInt32LittleEndian(s[67..], volumeId);
        Encoding.ASCII.GetBytes(label11).CopyTo(b, 71);
        Encoding.ASCII.GetBytes("FAT32   ").CopyTo(b, 82);
        // Código de boot mínimo: INT 18h (a BIOS tenta o próximo dispositivo).
        b[0x5A] = 0xCD;
        b[0x5B] = 0x18;
        b[510] = 0x55;
        b[511] = 0xAA;
        return b;
    }

    internal static byte[] BuildFsInfo(Fat32Layout l, int bps)
    {
        var b = new byte[bps];
        var s = b.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s[0..], 0x41615252);
        BinaryPrimitives.WriteUInt32LittleEndian(s[484..], 0x61417272);
        BinaryPrimitives.WriteUInt32LittleEndian(s[488..], (uint)(l.ClusterCount - 1)); // livres (a raiz usa 1)
        BinaryPrimitives.WriteUInt32LittleEndian(s[492..], 3); // próximo livre
        BinaryPrimitives.WriteUInt32LittleEndian(s[(bps - 4)..], 0xAA550000);
        return b;
    }

    /// <summary>Rótulo FAT: 11 caracteres ASCII maiúsculos, sem os proibidos, completado com espaço.</summary>
    public static string NormalizeLabel(string? label)
    {
        var clean = new StringBuilder();
        foreach (var ch in (label ?? "").Normalize(NormalizationForm.FormD).ToUpperInvariant())
        {
            if (ch > 127 || "\"*+,./:;<=>?[\\]|".Contains(ch) || char.IsControl(ch))
            {
                continue;
            }

            clean.Append(ch);
        }

        var text = clean.ToString().Trim();
        if (text.Length == 0)
        {
            text = "NO NAME";
        }

        return text.Length > 11 ? text[..11] : text.PadRight(11);
    }
}
