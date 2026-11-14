using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Vapt.Core.Disk;

/// <summary>
/// Varre uma pasta ou unidade em paralelo com FindFirstFileExW (FindExInfoBasic +
/// FIND_FIRST_EX_LARGE_FETCH, o caminho rápido da API). Cada pasta é listada por uma única
/// thread, que publica os filhos de uma vez e soma os tamanhos para cima. Pontos de
/// reparação (junções, links simbólicos) não são seguidos, para não contar nada duas vezes.
/// </summary>
public sealed class DiskScanner
{
    private readonly ConcurrentQueue<DiskNode> _queue = new();
    private int _pending;
    private long _scannedFiles;
    private long _scannedDirs;

    public DiskScanner(string rootPath)
    {
        RootPath = Path.GetFullPath(rootPath);
        var name = RootPath;
        if (name.Length > 3)
        {
            name = name.TrimEnd('\\');
        }

        Root = new DiskNode(name, NodeKind.Directory, null);
    }

    public string RootPath { get; }

    public DiskNode Root { get; }

    public long ScannedFiles => Interlocked.Read(ref _scannedFiles);

    public long ScannedDirectories => Interlocked.Read(ref _scannedDirs);

    /// <summary>Bytes em uso no volume (para a porcentagem), quando a raiz é uma unidade.</summary>
    public long VolumeUsed { get; private set; }

    public long VolumeFree { get; private set; }

    public bool IsDriveRoot { get; private set; }

    public bool Finished { get; private set; }

    public TimeSpan Elapsed { get; private set; }

    /// <summary>Porcentagem aproximada (só dá para estimar em unidades inteiras).</summary>
    public double Progress => Finished ? 1 : VolumeUsed <= 0 ? 0 : Math.Min(0.99, Root.Size / (double)VolumeUsed);

    public Task RunAsync(CancellationToken cancellation = default) => Task.Run(() => Run(cancellation), CancellationToken.None);

    private void Run(CancellationToken cancellation)
    {
        var started = DateTime.UtcNow;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(RootPath)!);
            if (drive.IsReady && string.Equals(drive.RootDirectory.FullName.TrimEnd('\\'), RootPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                IsDriveRoot = true;
                VolumeFree = drive.TotalFreeSpace;
                VolumeUsed = drive.TotalSize - drive.TotalFreeSpace;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        _pending = 1;
        _queue.Enqueue(Root);
        var workers = Math.Clamp(Environment.ProcessorCount, 2, 16);
        var threads = new Thread[workers];
        for (var i = 0; i < workers; i++)
        {
            threads[i] = new Thread(() => Worker(cancellation)) { IsBackground = true, Name = "Vapt.Scan" };
            threads[i].Start();
        }

        foreach (var t in threads)
        {
            t.Join();
        }

        Elapsed = DateTime.UtcNow - started;
        Finished = !cancellation.IsCancellationRequested;
        Log.Info($"Varredura de {RootPath}: {ScannedFiles:N0} arquivos, {ScannedDirectories:N0} pastas em {Elapsed.TotalSeconds:0.0}s.");
    }

    private void Worker(CancellationToken cancellation)
    {
        var spin = new SpinWait();
        while (!cancellation.IsCancellationRequested && Volatile.Read(ref _pending) > 0)
        {
            if (!_queue.TryDequeue(out var dir))
            {
                spin.SpinOnce(sleep1Threshold: 20);
                continue;
            }

            spin.Reset();
            try
            {
                ListDirectory(dir);
            }
            catch (Exception ex)
            {
                Log.Warn($"Varredura: {dir.FullPath}: {ex.Message}");
                dir.Publish([]);
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        }
    }

    private void ListDirectory(DiskNode dir)
    {
        var path = dir.FullPath;
        var search = (path.EndsWith('\\') ? path : path + "\\") + "*";
        // Prefixo \\?\ libera caminhos acima de 260 caracteres.
        if (!search.StartsWith(@"\\", StringComparison.Ordinal))
        {
            search = @"\\?\" + search;
        }

        var handle = Native.FindFirstFileExW(search, Native.FindExInfoBasic, out var data, 0, 0, Native.FIND_FIRST_EX_LARGE_FETCH);
        if (handle == Native.InvalidHandle)
        {
            dir.Denied = true;
            dir.Publish([]);
            return;
        }

        var children = new List<DiskNode>();
        long bytes = 0;
        long files = 0;
        try
        {
            do
            {
                var name = data.cFileName;
                if (name is "." or "..")
                {
                    continue;
                }

                var modified = DateTime.FromFileTimeUtc((long)(((ulong)data.ftLastWriteTimeHigh << 32) | data.ftLastWriteTimeLow));
                var isDir = (data.dwFileAttributes & Native.FILE_ATTRIBUTE_DIRECTORY) != 0;
                var isReparse = (data.dwFileAttributes & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
                if (isDir)
                {
                    var child = new DiskNode(name, NodeKind.Directory, dir) { Modified = modified };
                    children.Add(child);
                    if (isReparse)
                    {
                        child.Publish([]);
                    }
                    else
                    {
                        Interlocked.Increment(ref _pending);
                        _queue.Enqueue(child);
                    }
                }
                else
                {
                    var size = (long)(((ulong)data.nFileSizeHigh << 32) | data.nFileSizeLow);
                    // Arquivos só na nuvem (OneDrive) não ocupam o disco.
                    if ((data.dwFileAttributes & (Native.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | Native.FILE_ATTRIBUTE_RECALL_ON_OPEN)) != 0)
                    {
                        size = 0;
                    }

                    children.Add(new DiskNode(name, NodeKind.File, dir, size) { Modified = modified });
                    bytes += size;
                    files++;
                }
            }
            while (Native.FindNextFileW(handle, out data));
        }
        finally
        {
            Native.FindClose(handle);
        }

        dir.Publish([.. children]);
        Interlocked.Add(ref _scannedFiles, files);
        Interlocked.Increment(ref _scannedDirs);
        if (bytes != 0 || files != 0)
        {
            dir.AddUp(bytes, files);
        }
    }

    private static class Native
    {
        public const int FindExInfoBasic = 1;
        public const int FIND_FIRST_EX_LARGE_FETCH = 2;
        public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
        public const uint FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x40000;
        public const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x400000;
        public static readonly nint InvalidHandle = -1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WIN32_FIND_DATAW
        {
            public uint dwFileAttributes;
            public uint ftCreationTimeLow;
            public uint ftCreationTimeHigh;
            public uint ftLastAccessTimeLow;
            public uint ftLastAccessTimeHigh;
            public uint ftLastWriteTimeLow;
            public uint ftLastWriteTimeHigh;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string cAlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern nint FindFirstFileExW(string lpFileName, int fInfoLevelId, out WIN32_FIND_DATAW lpFindFileData, int fSearchOp, nint lpSearchFilter, int dwAdditionalFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FindNextFileW(nint hFindFile, out WIN32_FIND_DATAW lpFindFileData);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FindClose(nint hFindFile);
    }
}
