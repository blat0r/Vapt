using Microsoft.Win32;

namespace Vapt.Core.Startup;

public enum StartupSource
{
    UserRun,
    MachineRun,
    MachineRun32,
    UserFolder,
    CommonFolder,
}

public sealed record StartupEntry(
    string Name,
    string Command,
    StartupSource Source,
    bool Enabled,
    /// <summary>Executável resolvido (para ícone, editora e "abrir local").</summary>
    string? ExecutablePath,
    string? Publisher)
{
    public bool NeedsAdmin => Source is StartupSource.MachineRun or StartupSource.MachineRun32 or StartupSource.CommonFolder;

    public string SourceLabel => Source switch
    {
        StartupSource.UserRun => "Registro (sua conta)",
        StartupSource.MachineRun => "Registro (todos os usuários)",
        StartupSource.MachineRun32 => "Registro 32 bits (todos os usuários)",
        StartupSource.UserFolder => "Pasta Inicializar",
        _ => "Pasta Inicializar (todos os usuários)",
    };
}

/// <summary>
/// Apps que abrem com o Windows. Liga/desliga pelo mesmo lugar que o Gerenciador de Tarefas
/// (Explorer\StartupApproved, 12 bytes: 02 = ligado, 03 + data = desligado), então o estado
/// aparece igual nos dois e nada é apagado ao desligar.
/// </summary>
public static class StartupManager
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static IReadOnlyList<StartupEntry> List()
    {
        var list = new List<StartupEntry>();
        ReadRun(list, Registry.CurrentUser, Run, StartupSource.UserRun, Registry.CurrentUser, "Run");
        ReadRun(list, Registry.LocalMachine, Run, StartupSource.MachineRun, Registry.LocalMachine, "Run");
        ReadRun(list, Registry.LocalMachine, Run32, StartupSource.MachineRun32, Registry.LocalMachine, "Run32");
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupSource.UserFolder, Registry.CurrentUser);
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupSource.CommonFolder, Registry.LocalMachine);
        return list.OrderByDescending(e => e.Enabled).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void ReadRun(List<StartupEntry> list, RegistryKey root, string path, StartupSource source, RegistryKey approvedRoot, string approvedName)
    {
        using var key = root.OpenSubKey(path);
        if (key is null)
        {
            return;
        }

        foreach (var name in key.GetValueNames())
        {
            if (string.IsNullOrEmpty(name) || key.GetValue(name) is not string command || command.Length == 0)
            {
                continue;
            }

            var exe = ExtractExecutable(command);
            list.Add(new StartupEntry(name, command, source, IsEnabled(approvedRoot, approvedName, name), exe, Publisher(exe)));
        }
    }

    private static void ReadFolder(List<StartupEntry> list, string folder, StartupSource source, RegistryKey approvedRoot)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            list.Add(new StartupEntry(Path.GetFileNameWithoutExtension(file), file, source, IsEnabled(approvedRoot, "StartupFolder", name), file, Publisher(file)));
        }
    }

    private static bool IsEnabled(RegistryKey root, string approvedName, string valueName)
    {
        using var key = root.OpenSubKey($@"{Approved}\{approvedName}");
        // Sem registro = ligado. Primeiro byte ímpar (03, 07…) = desligado.
        return key?.GetValue(valueName) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
    }

    public static void SetEnabled(StartupEntry entry, bool enabled)
    {
        var (root, approvedName, valueName) = ApprovedLocation(entry);
        using var key = root.CreateSubKey($@"{Approved}\{approvedName}", writable: true);
        var data = new byte[12];
        data[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled)
        {
            BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        }

        key.SetValue(valueName, data, RegistryValueKind.Binary);
        Log.Info($"Inicialização: {entry.Name} {(enabled ? "ligado" : "desligado")}.");
    }

    public sealed record RemovedEntry(string Name, string Command, StartupSource Source, string? BackupFile, DateTime When);

    private static string RemovedFile => Path.Combine(AppPaths.DataDirectory, "startup-removed.json");

    private static string BackupFolder => Path.Combine(AppPaths.DataDirectory, "startup-backup");

    public static IReadOnlyList<RemovedEntry> Removed()
    {
        try
        {
            return File.Exists(RemovedFile)
                ? System.Text.Json.JsonSerializer.Deserialize<List<RemovedEntry>>(File.ReadAllText(RemovedFile)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static void SaveRemoved(List<RemovedEntry> list)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        File.WriteAllText(RemovedFile, System.Text.Json.JsonSerializer.Serialize(list));
    }

    /// <summary>Volta um item removido para a inicialização (como estava).</summary>
    public static void Restore(RemovedEntry removed)
    {
        switch (removed.Source)
        {
            case StartupSource.UserFolder or StartupSource.CommonFolder:
                if (removed.BackupFile is { } backup && File.Exists(backup))
                {
                    File.Copy(backup, removed.Command, overwrite: true);
                }

                break;
            default:
                var root = removed.Source == StartupSource.UserRun ? Registry.CurrentUser : Registry.LocalMachine;
                using (var key = root.CreateSubKey(removed.Source == StartupSource.MachineRun32 ? Run32 : Run, writable: true))
                {
                    key.SetValue(removed.Name, removed.Command, RegistryValueKind.String);
                }

                break;
        }

        SaveRemoved(Removed().Where(r => !(r.Name == removed.Name && r.Source == removed.Source)).ToList());
        Log.Info($"Inicialização: {removed.Name} restaurado.");
    }

    /// <summary>Tira o item da inicialização (com cópia para "Restaurar removidos").</summary>
    public static void Remove(StartupEntry entry)
    {
        string? backup = null;
        if (entry.Source is StartupSource.UserFolder or StartupSource.CommonFolder && File.Exists(entry.Command))
        {
            Directory.CreateDirectory(BackupFolder);
            backup = Path.Combine(BackupFolder, $"{DateTime.Now:yyyyMMddHHmmss}-{Path.GetFileName(entry.Command)}");
            File.Copy(entry.Command, backup, overwrite: true);
        }

        var list = Removed().ToList();
        list.Add(new RemovedEntry(entry.Name, entry.Command, entry.Source, backup, DateTime.Now));
        SaveRemoved(list);

        switch (entry.Source)
        {
            case StartupSource.UserFolder or StartupSource.CommonFolder:
                File.Delete(entry.Command);
                break;
            default:
                var root = entry.Source == StartupSource.UserRun ? Registry.CurrentUser : Registry.LocalMachine;
                using (var key = root.OpenSubKey(entry.Source == StartupSource.MachineRun32 ? Run32 : Run, writable: true))
                {
                    key?.DeleteValue(entry.Name, throwOnMissingValue: false);
                }

                break;
        }

        var (approvedRoot, approvedName, valueName) = ApprovedLocation(entry);
        using var approved = approvedRoot.OpenSubKey($@"{Approved}\{approvedName}", writable: true);
        approved?.DeleteValue(valueName, throwOnMissingValue: false);
        Log.Info($"Inicialização: {entry.Name} removido ({entry.Command}).");
    }

    private static (RegistryKey Root, string ApprovedName, string ValueName) ApprovedLocation(StartupEntry e) => e.Source switch
    {
        StartupSource.UserRun => (Registry.CurrentUser, "Run", e.Name),
        StartupSource.MachineRun => (Registry.LocalMachine, "Run", e.Name),
        StartupSource.MachineRun32 => (Registry.LocalMachine, "Run32", e.Name),
        StartupSource.UserFolder => (Registry.CurrentUser, "StartupFolder", Path.GetFileName(e.Command)),
        _ => (Registry.LocalMachine, "StartupFolder", Path.GetFileName(e.Command)),
    };

    /// <summary>Pega o executável de uma linha de comando ("C:\x\a.exe" -min ou C:\x\a.exe /b).</summary>
    public static string? ExtractExecutable(string command)
    {
        var c = Environment.ExpandEnvironmentVariables(command.Trim());
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : null;
        }

        var exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0)
        {
            return c[..(exe + 4)];
        }

        var space = c.IndexOf(' ');
        return space > 0 ? c[..space] : c;
    }

    private static string? Publisher(string? path)
    {
        try
        {
            if (path is null || !File.Exists(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            return string.IsNullOrWhiteSpace(info.CompanyName) ? null : info.CompanyName.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
