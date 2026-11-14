namespace Vapt.Core.Cleanup;

/// <summary>Mede e apaga os itens da limpeza. Arquivo em uso é pulado, nunca derruba a limpeza.</summary>
public static class CleanupEngine
{
    public static async Task<CleanupScan> ScanAsync(CleanupItem item, CancellationToken cancellation = default)
    {
        if (item.NeedsAdmin && !Elevation.IsAdministrator)
        {
            return new CleanupScan(0, 0);
        }

        if (item.CustomScan is not null)
        {
            try
            {
                return await item.CustomScan(cancellation);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"Medir {item.Id}: {ex.Message}");
                return new CleanupScan(0, 0);
            }
        }

        return await Task.Run(() =>
        {
            long bytes = 0, files = 0;
            foreach (var file in Files(item, cancellation))
            {
                bytes += file.Length;
                files++;
            }

            return new CleanupScan(bytes, files);
        }, cancellation);
    }

    public static async Task<CleanupResult> CleanAsync(CleanupItem item, CancellationToken cancellation = default)
    {
        if (item.NeedsAdmin && !Elevation.IsAdministrator)
        {
            return new CleanupResult(0, 0, 0);
        }

        if (item.Before is not null)
        {
            await item.Before();
        }

        try
        {
            if (item.CustomClean is not null)
            {
                return await item.CustomClean(cancellation);
            }

            return await Task.Run(() =>
            {
                long freed = 0, deleted = 0, skipped = 0;
                foreach (var file in Files(item, cancellation).ToList())
                {
                    try
                    {
                        var size = file.Length;
                        if (file.IsReadOnly)
                        {
                            file.IsReadOnly = false;
                        }

                        file.Delete();
                        freed += size;
                        deleted++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        skipped++; // em uso ou sem permissão
                    }
                }

                foreach (var folder in item.Folders)
                {
                    RemoveEmptySubfolders(folder);
                }

                Log.Info($"Limpeza {item.Id}: {Bytes.Format(freed)} em {deleted} arquivos, {skipped} pulados.");
                return new CleanupResult(freed, deleted, skipped);
            }, cancellation);
        }
        finally
        {
            if (item.After is not null)
            {
                await item.After();
            }
        }
    }

    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint, // nunca seguir junções/links
        ReturnSpecialDirectories = false,
    };

    private static IEnumerable<FileInfo> Files(CleanupItem item, CancellationToken cancellation)
    {
        var limit = DateTime.UtcNow - item.MinAge;
        foreach (var folder in item.Folders)
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            IEnumerable<FileInfo> files;
            try
            {
                files = new DirectoryInfo(folder).EnumerateFiles("*", Recursive);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var f in files)
            {
                cancellation.ThrowIfCancellationRequested();
                if (item.MinAge == TimeSpan.Zero || f.LastWriteTimeUtc < limit)
                {
                    yield return f;
                }
            }
        }

        foreach (var pattern in item.FilePatterns)
        {
            var dir = Path.GetDirectoryName(pattern)!;
            if (!Directory.Exists(dir))
            {
                continue;
            }

            IEnumerable<FileInfo> files;
            try
            {
                files = new DirectoryInfo(dir).EnumerateFiles(Path.GetFileName(pattern), new EnumerationOptions { IgnoreInaccessible = true });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var f in files)
            {
                if (item.MinAge == TimeSpan.Zero || f.LastWriteTimeUtc < limit)
                {
                    yield return f;
                }
            }
        }
    }

    private static void RemoveEmptySubfolders(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        try
        {
            // Das mais fundas para cima; a própria pasta raiz fica.
            foreach (var dir in Directory.EnumerateDirectories(root, "*", Recursive).OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Limpeza dos componentes do Windows (WinSxS) pelo caminho suportado: DISM.</summary>
    public static Task<ProcessResult> ComponentCleanupAsync(Action<string>? onLine, CancellationToken cancellation) =>
        ProcessRunner.RunAsync(Path.Combine(ProcessRunner.System32, "dism.exe"), "/Online /Cleanup-Image /StartComponentCleanup", onLine, cancellation);
}
