using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Vapt.Core.Apps;

public enum LeftoverKind
{
    Folder,
    File,
    RegistryKey,
}

public sealed record Leftover(LeftoverKind Kind, string Path, long Size, string Reason, bool Confident)
{
    public string KindLabel => Kind switch
    {
        LeftoverKind.Folder => "Pasta",
        LeftoverKind.File => "Atalho",
        _ => "Registro",
    };
}

/// <summary>
/// Procura o que um programa deixou para trás depois de desinstalado, como o Revo Uninstaller e
/// o Bulk Crap Uninstaller. Conservador de propósito: só olha lugares conhecidos (Arquivos de
/// Programas, ProgramData, AppData, menu Iniciar, chaves Software) e só aceita nomes que batem
/// com o programa. Pastas vão para a Lixeira; chaves do registro são exportadas para um .reg
/// antes de apagar.
/// </summary>
public static partial class Leftovers
{
    /// <summary>Nomes que nunca são sobra de um programa (são do Windows ou compartilhados).</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Classes", "Clients", "Policies", "RegisteredApplications", "WOW6432Node",
        "Common Files", "Microsoft Shared", "Windows NT", "Windows Defender", "WindowsApps", "Packages",
        "Temp", "Programs", "Startup", "Start Menu", "Intel", "Google", "Mozilla", "NVIDIA Corporation",
        "AMD", "Realtek", "Dell", "HP", "Lenovo", "ASUS", "Steam", "Valve", "Epic Games", "Adobe", "Oracle",
        "Python", "Git", "nodejs", "dotnet", "Package Cache", "Installer", "ODBC", "Khronos", "Wow6432Node",
    };

    public static string BackupDirectory => System.IO.Path.Combine(AppPaths.DataDirectory, "reg-backups");

    /// <summary>Nome do programa sem versão nem arquitetura: "Cheat Engine 7.6 (x64)" → "Cheat Engine".</summary>
    public static string CleanName(string name)
    {
        var n = VersionRegex().Replace(name, " ");
        n = ArchRegex().Replace(n, " ");
        n = Regex.Replace(n, @"\s{2,}", " ").Trim(' ', '-', '_', '.', ',');
        return n;
    }

    private static string Normalize(string s) => NonAlnumRegex().Replace(s, "").ToLowerInvariant();

    /// <summary>A pasta/chave tem o nome do programa (ignorando espaços, traços e versão).</summary>
    internal static bool NameMatches(string candidate, string appName)
    {
        var a = Normalize(CleanName(appName));
        var c = Normalize(CleanName(candidate));
        if (a.Length < 4 || c.Length < 4)
        {
            return false; // nomes curtos demais dão falso positivo
        }

        return c == a || (c.StartsWith(a, StringComparison.Ordinal) && c.Length - a.Length <= 3) || (a.StartsWith(c, StringComparison.Ordinal) && c.Length >= 6 && a.Length - c.Length <= 4);
    }

    public static IReadOnlyList<Leftover> Find(InstalledApp app, IReadOnlyCollection<InstalledApp>? stillInstalled = null)
    {
        var results = new List<Leftover>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var name = CleanName(app.Name);
        var publisher = app.Publisher is { Length: > 0 } p ? CleanPublisher(p) : null;

        // Pastas de programas que continuam instalados nunca entram.
        var keep = (stillInstalled ?? [])
            .Where(i => !ReferenceEquals(i, app) && !string.IsNullOrWhiteSpace(i.InstallLocation))
            .Select(i => i.InstallLocation!.TrimEnd('\\'))
            .ToList();

        void AddFolder(string path, string reason, bool confident)
        {
            path = path.TrimEnd('\\');
            if (!Directory.Exists(path) || !seen.Add(path) || Protected.Contains(System.IO.Path.GetFileName(path)))
            {
                return;
            }

            // Não apaga uma pasta que contém (ou é) a instalação de outro programa.
            if (keep.Any(k => k.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase) || k.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            results.Add(new Leftover(LeftoverKind.Folder, path, FolderSize(path), reason, confident));
        }

        // 1) A pasta de instalação que o próprio programa informou.
        if (!string.IsNullOrWhiteSpace(app.InstallLocation) && IsUnderKnownRoot(app.InstallLocation))
        {
            AddFolder(app.InstallLocation, "Pasta de instalação", true);
        }

        // 2) Pastas com o nome do programa (ou Editora\Programa) nos lugares de sempre.
        foreach (var root in FolderRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var dir in SafeDirectories(root))
            {
                var folder = System.IO.Path.GetFileName(dir);
                if (NameMatches(folder, name))
                {
                    AddFolder(dir, $"Pasta com o nome do programa em {Friendly(root)}", true);
                }
                else if (publisher is not null && NameMatches(folder, publisher))
                {
                    foreach (var sub in SafeDirectories(dir))
                    {
                        if (NameMatches(System.IO.Path.GetFileName(sub), name))
                        {
                            AddFolder(sub, $"Pasta em {folder}", true);
                        }
                    }
                }
            }
        }

        // 3) Atalhos que ficaram no menu Iniciar.
        foreach (var menu in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Programs), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms) })
        {
            if (!Directory.Exists(menu))
            {
                continue;
            }

            foreach (var dir in SafeDirectories(menu).Where(d => NameMatches(System.IO.Path.GetFileName(d), name)))
            {
                AddFolder(dir, "Pasta no menu Iniciar", true);
            }

            foreach (var lnk in SafeFiles(menu, "*.lnk").Where(f => NameMatches(System.IO.Path.GetFileNameWithoutExtension(f), name)))
            {
                if (seen.Add(lnk))
                {
                    results.Add(new Leftover(LeftoverKind.File, lnk, new FileInfo(lnk).Length, "Atalho no menu Iniciar", true));
                }
            }
        }

        // 4) Chaves Software\Programa e Software\Editora\Programa.
        foreach (var (hive, prefix, label) in RegistryRoots())
        {
            using var software = hive.OpenSubKey(prefix);
            if (software is null)
            {
                continue;
            }

            foreach (var sub in SafeSubKeys(software))
            {
                if (Protected.Contains(sub))
                {
                    continue;
                }

                if (NameMatches(sub, name))
                {
                    AddKey($"{label}\\{prefix}\\{sub}", "Configurações do programa no registro");
                }
                else if (publisher is not null && NameMatches(sub, publisher))
                {
                    using var pubKey = software.OpenSubKey(sub);
                    foreach (var child in pubKey is null ? [] : SafeSubKeys(pubKey))
                    {
                        if (NameMatches(child, name))
                        {
                            AddKey($"{label}\\{prefix}\\{sub}\\{child}", $"Configurações em {sub}");
                        }
                    }
                }
            }
        }

        void AddKey(string path, string reason)
        {
            if (seen.Add(path))
            {
                results.Add(new Leftover(LeftoverKind.RegistryKey, path, 0, reason, true));
            }
        }

        // 5) A própria entrada da lista de programas, se ficou (programa quebrado).
        if (app.KeyPath is { } keyPath && OpenKey(keyPath) is { } entry)
        {
            entry.Dispose();
            AddKey(keyPath, "Entrada na lista de programas instalados");
        }

        return results;
    }

    private static string CleanPublisher(string publisher)
    {
        var p = Regex.Replace(publisher, @",?\s*(Inc\.?|LLC|Ltd\.?|Limited|Corp\.?|Corporation|GmbH|S\.A\.|Co\.)\b", "", RegexOptions.IgnoreCase);
        return p.Trim(' ', ',', '.');
    }

    private static IEnumerable<string> FolderRoots()
    {
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var local = F(Environment.SpecialFolder.LocalApplicationData);
        yield return F(Environment.SpecialFolder.ProgramFiles);
        yield return F(Environment.SpecialFolder.ProgramFilesX86);
        yield return F(Environment.SpecialFolder.CommonApplicationData);
        yield return local;
        yield return System.IO.Path.Combine(local, "Programs");
        yield return F(Environment.SpecialFolder.ApplicationData);
        yield return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(local)!, "LocalLow");
    }

    private static bool IsUnderKnownRoot(string path)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(path).TrimEnd('\\');
            return FolderRoots().Any(r => full.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string Friendly(string root)
    {
        var name = System.IO.Path.GetFileName(root.TrimEnd('\\'));
        return name switch
        {
            "Program Files" => "Arquivos de Programas",
            "Program Files (x86)" => "Arquivos de Programas (x86)",
            "Local" or "Roaming" or "LocalLow" => "AppData\\" + name,
            _ => name,
        };
    }

    private static IEnumerable<(RegistryKey Hive, string Prefix, string Label)> RegistryRoots()
    {
        yield return (Registry.CurrentUser, "Software", "HKCU");
        yield return (Registry.LocalMachine, "SOFTWARE", "HKLM");
        yield return (Registry.LocalMachine, @"SOFTWARE\WOW6432Node", "HKLM");
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path, "*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeFiles(string path, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(path, pattern, new EnumerationOptions { IgnoreInaccessible = true }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string[] SafeSubKeys(RegistryKey key)
    {
        try
        {
            return key.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    private static long FolderSize(string path)
    {
        long total = 0;
        try
        {
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                total += f.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return total;
    }

    private static RegistryKey? OpenKey(string path, bool writable = false)
    {
        var i = path.IndexOf('\\');
        var hive = path[..i] == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
        return hive.OpenSubKey(path[(i + 1)..], writable);
    }

    /// <summary>Apaga as sobras escolhidas. Devolve quantas saíram e o espaço liberado.</summary>
    public static async Task<(int Removed, long Freed, List<string> Failed)> RemoveAsync(IEnumerable<Leftover> items)
    {
        var removed = 0;
        long freed = 0;
        var failed = new List<string>();
        Directory.CreateDirectory(BackupDirectory);
        foreach (var item in items)
        {
            try
            {
                switch (item.Kind)
                {
                    case LeftoverKind.Folder:
                        await Task.Run(() => Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(item.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin));
                        break;
                    case LeftoverKind.File:
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(item.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        break;
                    default:
                        await DeleteKeyWithBackupAsync(item.Path);
                        break;
                }

                removed++;
                freed += item.Size;
                Log.Info($"Sobra removida ({item.Kind}): {item.Path}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or OperationCanceledException)
            {
                Log.Warn($"Sobra não removida: {item.Path}: {ex.Message}");
                failed.Add(item.Path);
            }
        }

        return (removed, freed, failed);
    }

    /// <summary>Exporta a chave para %LOCALAPPDATA%\Vapt\reg-backups antes de apagar (dá para voltar com dois cliques).</summary>
    public static async Task DeleteKeyWithBackupAsync(string keyPath)
    {
        Directory.CreateDirectory(BackupDirectory);
        var safeName = Regex.Replace(keyPath.Split('\\').Last(), @"[^\w\-. ]", "_");
        var file = System.IO.Path.Combine(BackupDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeName}.reg");
        var export = await ProcessRunner.RunAsync(System.IO.Path.Combine(ProcessRunner.System32, "reg.exe"), $"export \"{keyPath}\" \"{file}\" /y");
        if (!export.Ok)
        {
            throw new InvalidOperationException("Não deu para fazer o backup da chave; nada foi apagado.");
        }

        var i = keyPath.IndexOf('\\');
        var hive = keyPath[..i] == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
        hive.DeleteSubKeyTree(keyPath[(i + 1)..], throwOnMissingSubKey: false);
    }

    [GeneratedRegex(@"\b[vV]?\d+(\.\d+)+[a-z]?\b|\bv?\d{4}\b|\(\s*\d[\d.\s]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"\(?\b(x64|x86|64-bit|32-bit|64 bits|32 bits|amd64|arm64|win64|win32)\b\)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArchRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}]")]
    private static partial Regex NonAlnumRegex();
}
