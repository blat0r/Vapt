using System.Buffers.Binary;
using System.Text;
using Vapt.Core.Usb;

namespace Vapt.Core.Tests;

public class Fat32FormatterTests
{
    /// <summary>Disco falso: guarda só os setores escritos.</summary>
    private sealed class MemoryDevice(long totalSectors, int bps = 512, long hidden = 2048) : ISectorDevice
    {
        public Dictionary<long, byte[]> Sectors { get; } = [];

        public int BytesPerSector => bps;

        public long TotalSectors => totalSectors;

        public long HiddenSectors => hidden;

        public void Write(long sector, ReadOnlySpan<byte> data)
        {
            Assert.Equal(0, data.Length % bps);
            Assert.True(sector + data.Length / bps <= totalSectors, "escreveu fora do volume");
            for (var i = 0; i < data.Length / bps; i++)
            {
                var chunk = data.Slice(i * bps, bps);
                if (chunk.IndexOfAnyExcept((byte)0) < 0)
                {
                    Sectors.Remove(sector + i);
                }
                else
                {
                    Sectors[sector + i] = chunk.ToArray();
                }
            }
        }

        public byte[] Read(long sector) => Sectors.TryGetValue(sector, out var s) ? s : new byte[bps];
    }

    [Theory]
    [InlineData(64L)] // GB
    [InlineData(128L)]
    [InlineData(256L)]
    [InlineData(1000L)]
    public void Plan_fits_clusters_in_fat(long gigabytes)
    {
        var sectors = gigabytes * 1000 * 1000 * 1000 / 512;
        var l = Fat32Formatter.Plan(512, sectors, 0);
        Assert.InRange(l.ClusterCount, Fat32Formatter.MinClusters, Fat32Formatter.MaxClusters);
        // Cada FAT precisa de uma entrada (4 bytes) por cluster + 2 reservadas.
        Assert.True(l.FatSectors * 512 / 4 >= l.ClusterCount + 2);
        // E os dados não podem passar do fim do volume.
        Assert.True(l.DataStartSector + l.ClusterCount * l.SectorsPerCluster <= sectors);
        Assert.Equal(32768, l.ClusterBytes);
    }

    [Fact]
    public void Plan_rejects_too_many_clusters()
    {
        // 2 TB com cluster de 4 KB = clusters demais.
        var sectors = (2L << 40) / 512 - 1;
        Assert.Throws<InvalidOperationException>(() => Fat32Formatter.Plan(512, sectors, 4096));
    }

    [Fact]
    public void Format_writes_valid_structures()
    {
        var sectors = 64L * 1000 * 1000 * 1000 / 512;
        var dev = new MemoryDevice(sectors);
        var l = Fat32Formatter.Format(dev, "Meu Pendrive", 0);

        var boot = dev.Read(0);
        Assert.Equal(0xEB, boot[0]);
        Assert.Equal(0x55, boot[510]);
        Assert.Equal(0xAA, boot[511]);
        Assert.Equal(512, BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11)));
        Assert.Equal(l.SectorsPerCluster, boot[13]);
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(14)));
        Assert.Equal(2, boot[16]);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(17)));
        Assert.Equal((uint)sectors, BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(32)));
        Assert.Equal((uint)l.FatSectors, BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(36)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(44)));
        Assert.Equal(2048u, BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(28)));
        Assert.Equal("MEU PENDRIV", Encoding.ASCII.GetString(boot, 71, 11));
        Assert.Equal("FAT32   ", Encoding.ASCII.GetString(boot, 82, 8));
        Assert.Equal(boot, dev.Read(6)); // cópia

        var fsInfo = dev.Read(1);
        Assert.Equal(0x41615252u, BinaryPrimitives.ReadUInt32LittleEndian(fsInfo.AsSpan(0)));
        Assert.Equal(0x61417272u, BinaryPrimitives.ReadUInt32LittleEndian(fsInfo.AsSpan(484)));
        Assert.Equal((uint)(l.ClusterCount - 1), BinaryPrimitives.ReadUInt32LittleEndian(fsInfo.AsSpan(488)));
        Assert.Equal(0xAA550000u, BinaryPrimitives.ReadUInt32LittleEndian(fsInfo.AsSpan(508)));

        foreach (var fatStart in new[] { 32L, 32 + l.FatSectors })
        {
            var fat = dev.Read(fatStart);
            Assert.Equal(0x0FFFFFF8u, BinaryPrimitives.ReadUInt32LittleEndian(fat.AsSpan(0)));
            Assert.Equal(0x0FFFFFFFu, BinaryPrimitives.ReadUInt32LittleEndian(fat.AsSpan(4)));
            Assert.Equal(0x0FFFFFFFu, BinaryPrimitives.ReadUInt32LittleEndian(fat.AsSpan(8)));
        }

        var root = dev.Read(l.DataStartSector);
        Assert.Equal("MEU PENDRIV", Encoding.ASCII.GetString(root, 0, 11));
        Assert.Equal(0x08, root[11]);
    }

    [Theory]
    [InlineData("pendrive", "PENDRIVE   ")]
    [InlineData("Instalação Win", "INSTALACAO ")]
    [InlineData("a/b:c", "ABC        ")]
    [InlineData("", "NO NAME    ")]
    public void Label_is_normalized(string input, string expected) => Assert.Equal(expected, Fat32Formatter.NormalizeLabel(input));

    [Fact]
    public void Unattend_contains_requested_keys()
    {
        var xml = Unattend.Build(new WindowsSetupTweaks(true, true, false));
        Assert.Contains("BypassTPMCheck", xml);
        Assert.Contains("BypassNRO", xml);
        Assert.DoesNotContain("PreventDeviceEncryption", xml);
        _ = System.Xml.Linq.XDocument.Parse(xml); // XML válido
    }
}
