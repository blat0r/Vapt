using System.Text.Json;
using Microsoft.Win32;

namespace Vapt.Core.Tweaks;

/// <summary>
/// Aplica e desfaz otimizações. Antes de escrever qualquer valor, guarda o original (ou "não
/// existia") em %LOCALAPPDATA%\Vapt\backup.json — o desfazer volta exatamente ao que era,
/// em vez de chutar um padrão como o Optimizer faz.
/// </summary>
public static class TweakEngine
{
    private sealed class Journal
    {
        /// <summary>Valor original por "caminho\nome": null = não existia.</summary>
        public Dictionary<string, JournalValue?> Registry { get; set; } = [];

        /// <summary>Start original de cada serviço.</summary>
        public Dictionary<string, int> Services { get; set; } = [];

        public HashSet<string> Applied { get; set; } = [];
    }

    private sealed record JournalValue(string Kind, string Data);

    private static readonly Lock Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static Journal Load()
    {
        try
        {
            if (File.Exists(AppPaths.BackupFile))
            {
                return JsonSerializer.Deserialize<Journal>(File.ReadAllText(AppPaths.BackupFile)) ?? new();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error("backup.json ilegível.", ex);
        }

        return new();
    }

    private static void Save(Journal journal)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var temp = AppPaths.BackupFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(journal, Json));
        File.Move(temp, AppPaths.BackupFile, overwrite: true);
    }

    public static async Task<bool> IsAppliedAsync(Tweak tweak)
    {
        if (tweak.IsAppliedCustom is not null)
        {
            try
            {
                return await tweak.IsAppliedCustom();
            }
            catch (Exception ex)
            {
                Log.Warn($"Estado de {tweak.Id}: {ex.Message}");
                return false;
            }
        }

        foreach (var op in tweak.Registry)
        {
            if (!Matches(op))
            {
                return false;
            }
        }

        foreach (var service in tweak.DisableServices)
        {
            if (ServiceStart(service) is int start && start != 4)
            {
                return false;
            }
        }

        return tweak.Registry.Count > 0 || tweak.DisableServices.Count > 0;
    }

    public static async Task ApplyAsync(Tweak tweak)
    {
        if (tweak.NeedsAdmin && !Elevation.IsAdministrator)
        {
            throw new UnauthorizedAccessException("Esta otimização precisa do Vapt aberto como administrador.");
        }

        lock (Gate)
        {
            var journal = Load();
            foreach (var op in tweak.Registry)
            {
                // Só o primeiro original vale: aplicar duas vezes não perde o valor de fábrica.
                if (!journal.Registry.ContainsKey(op.Id))
                {
                    journal.Registry[op.Id] = Read(op);
                }
            }

            foreach (var service in tweak.DisableServices)
            {
                if (!journal.Services.ContainsKey(service) && ServiceStart(service) is int start)
                {
                    journal.Services[service] = start;
                }
            }

            journal.Applied.Add(tweak.Id);
            Save(journal);

            foreach (var op in tweak.Registry)
            {
                Write(op);
            }
        }

        foreach (var service in tweak.DisableServices)
        {
            await SetServiceStartAsync(service, 4, stop: true);
        }

        if (tweak.ApplyCustom is not null && !await tweak.ApplyCustom())
        {
            throw new InvalidOperationException($"O Windows recusou \"{tweak.Name}\".");
        }

        Log.Info($"Otimização aplicada: {tweak.Id}");
    }

    public static async Task RevertAsync(Tweak tweak)
    {
        if (tweak.NeedsAdmin && !Elevation.IsAdministrator)
        {
            throw new UnauthorizedAccessException("Desfazer esta otimização precisa do Vapt aberto como administrador.");
        }

        Dictionary<string, int> services;
        lock (Gate)
        {
            var journal = Load();
            foreach (var op in tweak.Registry)
            {
                if (journal.Registry.TryGetValue(op.Id, out var original))
                {
                    Restore(op, original);
                    journal.Registry.Remove(op.Id);
                }
                else
                {
                    // Sem diário (aplicado fora do Vapt): o melhor palpite é apagar o valor.
                    Restore(op, null);
                }
            }

            services = tweak.DisableServices.ToDictionary(s => s, s => journal.Services.GetValueOrDefault(s, 3));
            foreach (var s in tweak.DisableServices)
            {
                journal.Services.Remove(s);
            }

            journal.Applied.Remove(tweak.Id);
            Save(journal);
        }

        foreach (var (service, start) in services)
        {
            await SetServiceStartAsync(service, start, stop: false);
        }

        if (tweak.RevertCustom is not null && !await tweak.RevertCustom())
        {
            throw new InvalidOperationException($"O Windows recusou desfazer \"{tweak.Name}\".");
        }

        Log.Info($"Otimização desfeita: {tweak.Id}");
    }

    /// <summary>Otimizações marcadas como aplicadas pelo Vapt (para "Desfazer tudo").</summary>
    public static IReadOnlySet<string> AppliedIds()
    {
        lock (Gate)
        {
            return Load().Applied;
        }
    }

    // ── Registro ─────────────────────────────────────────────────────────

    private static bool Matches(RegOp op)
    {
        using var key = op.Root.OpenSubKey(op.SubKey);
        var current = key?.GetValue(op.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (op.Value is null)
        {
            return current is null;
        }

        return current switch
        {
            null => false,
            int i when op.Value is int v => i == v,
            long l when op.Value is long v => l == v,
            string s when op.Value is string v => s == v,
            _ => Equals(current.ToString(), op.Value.ToString()),
        };
    }

    private static JournalValue? Read(RegOp op)
    {
        using var key = op.Root.OpenSubKey(op.SubKey);
        if (key?.GetValue(op.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not { } value)
        {
            return null;
        }

        var kind = key.GetValueKind(op.Name);
        var data = value switch
        {
            byte[] bytes => Convert.ToBase64String(bytes),
            string[] lines => string.Join('\0', lines),
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
        };
        return new JournalValue(kind.ToString(), data);
    }

    private static void Write(RegOp op)
    {
        if (op.Value is null)
        {
            using var key = op.Root.OpenSubKey(op.SubKey, writable: true);
            key?.DeleteValue(op.Name, throwOnMissingValue: false);
            return;
        }

        using var k = op.Root.CreateSubKey(op.SubKey, writable: true);
        k.SetValue(op.Name, op.Value, op.Kind);
    }

    private static void Restore(RegOp op, JournalValue? original)
    {
        if (original is null)
        {
            using var key = op.Root.OpenSubKey(op.SubKey, writable: true);
            key?.DeleteValue(op.Name, throwOnMissingValue: false);
            return;
        }

        var kind = Enum.Parse<RegistryValueKind>(original.Kind);
        object value = kind switch
        {
            RegistryValueKind.DWord => int.Parse(original.Data, System.Globalization.CultureInfo.InvariantCulture),
            RegistryValueKind.QWord => long.Parse(original.Data, System.Globalization.CultureInfo.InvariantCulture),
            RegistryValueKind.Binary => Convert.FromBase64String(original.Data),
            RegistryValueKind.MultiString => original.Data.Split('\0'),
            _ => original.Data,
        };
        using var k = op.Root.CreateSubKey(op.SubKey, writable: true);
        k.SetValue(op.Name, value, kind);
    }

    // ── Serviços ─────────────────────────────────────────────────────────

    /// <summary>Muda o tipo de início de um serviço guardando o original no diário.</summary>
    public static async Task SetServiceAsync(string service, int start)
    {
        if (!Elevation.IsAdministrator)
        {
            throw new UnauthorizedAccessException("Mudar serviços precisa do Vapt aberto como administrador.");
        }

        lock (Gate)
        {
            var journal = Load();
            if (!journal.Services.ContainsKey(service) && ServiceStart(service) is int current)
            {
                journal.Services[service] = current;
                Save(journal);
            }
        }

        await SetServiceStartAsync(service, start, stop: start == 4);
        if (ServiceStart(service) != start)
        {
            throw new InvalidOperationException("O Windows não deixou mudar este serviço (é protegido).");
        }

        Log.Info($"Serviço {service} → Start={start}");
    }

    /// <summary>Volta o serviço ao tipo de início de antes do Vapt (ou ao padrão informado).</summary>
    public static async Task RestoreServiceAsync(string service, int fallback)
    {
        if (!Elevation.IsAdministrator)
        {
            throw new UnauthorizedAccessException("Mudar serviços precisa do Vapt aberto como administrador.");
        }

        int start;
        lock (Gate)
        {
            var journal = Load();
            start = journal.Services.GetValueOrDefault(service, fallback);
            journal.Services.Remove(service);
            Save(journal);
        }

        await SetServiceStartAsync(service, start, stop: false);
        Log.Info($"Serviço {service} restaurado → Start={start}");
    }

    /// <summary>Start do serviço no registro (2 automático, 3 manual, 4 desativado) ou nulo se não existe.</summary>
    public static int? ServiceStart(string service)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}");
        return key?.GetValue("Start") as int?;
    }

    private static async Task SetServiceStartAsync(string service, int start, bool stop)
    {
        if (ServiceStart(service) is null)
        {
            return; // não existe nesta edição do Windows
        }

        var type = start switch
        {
            2 => "auto",
            3 => "demand",
            4 => "disabled",
            _ => "demand",
        };
        await ProcessRunner.RunAsync(Path.Combine(ProcessRunner.System32, "sc.exe"), $"config \"{service}\" start= {type}");
        if (stop)
        {
            await ProcessRunner.RunAsync(Path.Combine(ProcessRunner.System32, "sc.exe"), $"stop \"{service}\"");
        }
    }

    // ── Ponto de restauração ─────────────────────────────────────────────

    /// <summary>
    /// Cria um ponto de restauração. O Windows só deixa um a cada 24 h por padrão, então zera
    /// esse intervalo durante a criação (como o WinUtil) e devolve o valor original depois.
    /// </summary>
    public static async Task<bool> CreateRestorePointAsync(string description)
    {
        const string script = """
            $k = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'
            $old = (Get-ItemProperty -Path $k -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue).SystemRestorePointCreationFrequency
            Set-ItemProperty -Path $k -Name SystemRestorePointCreationFrequency -Value 0 -Type DWord
            try { Checkpoint-Computer -Description $env:VAPT_RP -RestorePointType MODIFY_SETTINGS }
            finally { if ($null -eq $old) { Remove-ItemProperty -Path $k -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue } else { Set-ItemProperty -Path $k -Name SystemRestorePointCreationFrequency -Value $old -Type DWord } }
            """;
        Environment.SetEnvironmentVariable("VAPT_RP", description);
        var result = await ProcessRunner.PowerShellAsync(script);
        if (!result.Ok)
        {
            Log.Warn("Ponto de restauração falhou: " + result.Error.Trim());
        }

        return result.Ok;
    }

    public static async Task RestartExplorerAsync()
    {
        await ProcessRunner.RunAsync(Path.Combine(ProcessRunner.System32, "taskkill.exe"), "/f /im explorer.exe");
        await Task.Delay(800);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = true });
    }
}
