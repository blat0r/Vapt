using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Vapt.Core.Usb;

/// <summary>Acesso de baixo nível a volumes e discos físicos (precisa de administrador).</summary>
internal static class NativeDisk
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;

    public const uint FSCTL_LOCK_VOLUME = 0x00090018;
    public const uint FSCTL_UNLOCK_VOLUME = 0x0009001C;
    public const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
    public const uint FSCTL_ALLOW_EXTENDED_DASD_IO = 0x00090083;
    public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
    public const uint IOCTL_DISK_GET_PARTITION_INFO_EX = 0x00070048;
    public const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
    public const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x00070140;

    public static SafeFileHandle Open(string path, bool write)
    {
        var handle = CreateFileW(
            path,
            GENERIC_READ | (write ? GENERIC_WRITE : 0),
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            0,
            OPEN_EXISTING,
            write ? FILE_FLAG_WRITE_THROUGH : 0,
            0);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Não foi possível abrir {path}");
        }

        return handle;
    }

    public static bool Control(SafeFileHandle handle, uint code)
    {
        return DeviceIoControl(handle, code, 0, 0, 0, 0, out _, 0);
    }

    public static byte[] Query(SafeFileHandle handle, uint code, int size)
    {
        var buffer = new byte[size];
        unsafe
        {
            fixed (byte* p = buffer)
            {
                if (!DeviceIoControl(handle, code, 0, 0, (nint)p, size, out _, 0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
        }

        return buffer;
    }

    public static int BytesPerSector(SafeFileHandle handle)
    {
        // DISK_GEOMETRY_EX começa com DISK_GEOMETRY: BytesPerSector fica no byte 20.
        var geo = Query(handle, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, 256);
        var bps = BitConverter.ToInt32(geo, 20);
        return bps is 512 or 1024 or 2048 or 4096 ? bps : 512;
    }

    public static long Length(SafeFileHandle handle) => BitConverter.ToInt64(Query(handle, IOCTL_DISK_GET_LENGTH_INFO, 8), 0);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, nint inBuffer, int inSize, nint outBuffer, int outSize, out int returned, nint overlapped);
}

/// <summary>
/// Volume aberto e travado para escrita direta (FAT32 grande). Ao fechar, desmonta para o
/// Windows reler o sistema de arquivos novo.
/// </summary>
internal sealed class LockedVolume : ISectorDevice, IDisposable
{
    private readonly SafeFileHandle _handle;

    public LockedVolume(string volumePath)
    {
        // CreateFile quer "\\?\Volume{guid}" sem a barra final.
        var path = volumePath.TrimEnd('\\');
        _handle = NativeDisk.Open(path, write: true);
        var locked = false;
        for (var i = 0; i < 20 && !(locked = NativeDisk.Control(_handle, NativeDisk.FSCTL_LOCK_VOLUME)); i++)
        {
            Thread.Sleep(250);
        }

        if (!locked)
        {
            _handle.Dispose();
            throw new IOException("O volume está em uso por outro programa. Feche janelas do Explorador abertas no pendrive.");
        }

        NativeDisk.Control(_handle, NativeDisk.FSCTL_ALLOW_EXTENDED_DASD_IO);
        BytesPerSector = NativeDisk.BytesPerSector(_handle);
        var info = NativeDisk.Query(_handle, NativeDisk.IOCTL_DISK_GET_PARTITION_INFO_EX, 256);
        // PARTITION_INFORMATION_EX: estilo (4) + alinhamento (4), início (8), tamanho (8).
        var start = BitConverter.ToInt64(info, 8);
        var length = BitConverter.ToInt64(info, 16);
        HiddenSectors = start / BytesPerSector;
        TotalSectors = length / BytesPerSector;
    }

    public int BytesPerSector { get; }

    public long TotalSectors { get; }

    public long HiddenSectors { get; }

    public void Write(long sector, ReadOnlySpan<byte> data) => RandomAccess.Write(_handle, data, sector * BytesPerSector);

    public void Dispose()
    {
        NativeDisk.Control(_handle, NativeDisk.FSCTL_DISMOUNT_VOLUME);
        NativeDisk.Control(_handle, NativeDisk.FSCTL_UNLOCK_VOLUME);
        _handle.Dispose();
    }
}

/// <summary>Grava uma imagem (.img / ISO híbrida) byte a byte num disco físico.</summary>
internal static class RawImageWriter
{
    public static void Write(int diskNumber, string imagePath, bool verify, IProgress<UsbProgress> progress, CancellationToken cancellation)
    {
        using var disk = NativeDisk.Open($@"\\.\PhysicalDrive{diskNumber}", write: true);
        var bps = NativeDisk.BytesPerSector(disk);
        var diskLength = NativeDisk.Length(disk);
        using var image = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var total = image.Length;
        if (total > diskLength)
        {
            throw new InvalidOperationException($"A imagem ({Bytes.Format(total)}) é maior que o pendrive ({Bytes.Format(diskLength)}).");
        }

        const int Chunk = 4 << 20;
        var buffer = new byte[Chunk];
        long offset = 0;
        var started = DateTime.UtcNow;
        while (offset < total)
        {
            cancellation.ThrowIfCancellationRequested();
            var read = image.ReadAtLeast(buffer, Math.Min(Chunk, (int)Math.Min(int.MaxValue, total - offset)), throwOnEndOfStream: false);
            if (read == 0)
            {
                break;
            }

            // O último pedaço é completado com zeros até fechar um setor.
            var aligned = (read + bps - 1) / bps * bps;
            if (aligned > read)
            {
                Array.Clear(buffer, read, aligned - read);
            }

            RandomAccess.Write(disk, buffer.AsSpan(0, aligned), offset);
            offset += read;
            var speed = offset / Math.Max(0.5, (DateTime.UtcNow - started).TotalSeconds);
            progress.Report(new UsbProgress((verify ? 0.5 : 1.0) * offset / total * 100, $"Gravando imagem… {Bytes.Format(offset)} de {Bytes.Format(total)} · {Bytes.Format((long)speed)}/s"));
        }

        if (!verify)
        {
            return;
        }

        image.Position = 0;
        var check = new byte[Chunk];
        offset = 0;
        while (offset < total)
        {
            cancellation.ThrowIfCancellationRequested();
            var read = image.ReadAtLeast(buffer, Math.Min(Chunk, (int)Math.Min(int.MaxValue, total - offset)), throwOnEndOfStream: false);
            if (read == 0)
            {
                break;
            }

            var aligned = (read + bps - 1) / bps * bps;
            RandomAccess.Read(disk, check.AsSpan(0, aligned), offset);
            if (!buffer.AsSpan(0, read).SequenceEqual(check.AsSpan(0, read)))
            {
                throw new IOException($"A verificação falhou perto de {Bytes.Format(offset)}: o pendrive pode estar com defeito ou ser falsificado.");
            }

            offset += read;
            progress.Report(new UsbProgress(50 + 50.0 * offset / total, $"Verificando… {Bytes.Format(offset)} de {Bytes.Format(total)}"));
        }
    }
}
