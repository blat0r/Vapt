using System.Text.Json;

namespace Vapt.Core.Debloat;

public sealed record InstalledBloat(BloatApp App, string? PackageFullName, bool Provisioned, string? Version);

public sealed record TaskState(string Path, bool Exists, bool Enabled);

/// <summary>
/// Remove apps pré-instalados (Remove-AppxPackage, como o Win11Debloat), muda serviços pelo
/// diário do TweakEngine e liga/desliga tarefas agendadas. Tudo reversível: apps voltam pela
/// Loja; serviços e tarefas voltam pelo Vapt.
/// </summary>
public static class DebloatEngine
{
    private static string RemovedFile => Path.Combine(AppPaths.DataDirectory, "removed-apps.json");

    /// <summary>Quais apps do catálogo estão instalados nesta conta (e provisionados, se admin).</summary>
    public static async Task<IReadOnlyList<InstalledBloat>> DetectAsync(CancellationToken cancellation = default)
    {
        var provisioned = Elevation.IsAdministrator
            ? "$prov = @(Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue | ForEach-Object { $_.DisplayName })"
            : "$prov = @()";
        var script = $$"""
            {{provisioned}}
            $pk = @(Get-AppxPackage -ErrorAction SilentlyContinue | ForEach-Object { [pscustomobject]@{ N = $_.Name; F = $_.PackageFullName; V = [string]$_.Version } })
            [pscustomobject]@{ Packages = $pk; Provisioned = $prov; OneDrive = [bool](Test-Path "$env:LOCALAPPDATA\Microsoft\OneDrive\OneDrive.exe") -or [bool](Test-Path "$env:ProgramFiles\Microsoft OneDrive\OneDrive.exe") } | ConvertTo-Json -Depth 3 -Compress
            """;
        var result = await ProcessRunner.PowerShellAsync(script, cancellation: cancellation);
        if (!result.Ok)
        {
            throw new InvalidOperationException("Não foi possível listar os apps: " + result.Error.Trim());
        }

        using var doc = JsonDocument.Parse(result.Output.Trim());
        var packages = new Dictionary<string, (string Full, string Version)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in doc.RootElement.GetProperty("Packages").EnumerateArray())
        {
            packages.TryAdd(p.GetProperty("N").GetString()!, (p.GetProperty("F").GetString()!, p.GetProperty("V").GetString() ?? ""));
        }

        var prov = new HashSet<string>(doc.RootElement.GetProperty("Provisioned").EnumerateArray().Select(e => e.GetString() ?? ""), StringComparer.OrdinalIgnoreCase);
        var oneDrive = doc.RootElement.GetProperty("OneDrive").GetBoolean();

        var found = new List<InstalledBloat>();
        foreach (var app in DebloatCatalog.Apps)
        {
            if (app.Method == "WinGet")
            {
                if (app.Id == "Microsoft.OneDrive" && oneDrive)
                {
                    found.Add(new InstalledBloat(app, null, false, null));
                }

                continue;
            }

            var installed = packages.TryGetValue(app.Id, out var pkg);
            var isProvisioned = prov.Contains(app.Id);
            if (installed || isProvisioned)
            {
                found.Add(new InstalledBloat(app, installed ? pkg.Full : null, isProvisioned, installed ? pkg.Version : null));
            }
        }

        return found;
    }

    /// <summary>
    /// Remove os apps. Como administrador, tira de todas as contas e também o pacote
    /// provisionado (senão ele volta para contas novas e às vezes após grandes atualizações).
    /// </summary>
    public static async Task<(int Removed, List<string> Failed)> RemoveAsync(IReadOnlyList<InstalledBloat> apps, IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        var removed = 0;
        var failed = new List<string>();
        var admin = Elevation.IsAdministrator;
        foreach (var item in apps)
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Report(item.App.Name);
            bool ok;
            if (item.App.Id == "Microsoft.OneDrive")
            {
                ok = await RemoveOneDriveAsync(cancellation);
            }
            else
            {
                var id = ProcessRunner.PsQuote(item.App.Id);
                var script = admin
                    ? $"""
                      Get-AppxPackage -AllUsers -Name {id} | Remove-AppxPackage -AllUsers -ErrorAction SilentlyContinue
                      Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq {id} | Remove-AppxProvisionedPackage -Online -AllUsers -ErrorAction SilentlyContinue | Out-Null
                      if (Get-AppxPackage -Name {id}) {"{"} throw 'ainda instalado' {"}"}
                      """
                    : $"""
                      Get-AppxPackage -Name {id} | Remove-AppxPackage
                      if (Get-AppxPackage -Name {id}) {"{"} throw 'ainda instalado' {"}"}
                      """;
                var r = await ProcessRunner.PowerShellAsync(script, cancellation: cancellation);
                ok = r.Ok;
                if (!ok)
                {
                    Log.Warn($"Debloat: {item.App.Id}: {r.Error.Trim()}");
                }
            }

            if (ok)
            {
                removed++;
                RememberRemoved(item.App);
                Log.Info($"Debloat: removido {item.App.Id}");
            }
            else
            {
                failed.Add(item.App.Name);
            }
        }

        return (removed, failed);
    }

    private static async Task<bool> RemoveOneDriveAsync(CancellationToken cancellation)
    {
        // O próprio desinstalador do OneDrive (o mesmo que o winget chama). Os arquivos da pasta OneDrive ficam.
        var candidates = new[]
        {
            Path.Combine(ProcessRunner.System32, "OneDriveSetup.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "OneDriveSetup.exe"),
        }.Concat(Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\OneDrive"))
            ? Directory.EnumerateFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\OneDrive"), "OneDriveSetup.exe", SearchOption.AllDirectories)
            : []);
        var setup = candidates.FirstOrDefault(File.Exists);
        if (setup is null)
        {
            var w = await ProcessRunner.RunAsync("winget", "uninstall --id Microsoft.OneDrive -e --silent --accept-source-agreements", cancellation: cancellation);
            return w.Ok;
        }

        await ProcessRunner.RunAsync(Path.Combine(ProcessRunner.System32, "taskkill.exe"), "/f /im OneDrive.exe", cancellation: cancellation);
        var r = await ProcessRunner.RunAsync(setup, "/uninstall", cancellation: cancellation);
        return r.Ok;
    }

    // ── Apps removidos (para mostrar como reinstalar) ────────────────────

    public sealed record RemovedApp(string Id, string Name, DateTime When);

    public static IReadOnlyList<RemovedApp> Removed()
    {
        try
        {
            return File.Exists(RemovedFile) ? JsonSerializer.Deserialize<List<RemovedApp>>(File.ReadAllText(RemovedFile)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return [];
        }
    }

    private static void RememberRemoved(BloatApp app)
    {
        var list = Removed().Where(r => r.Id != app.Id).ToList();
        list.Add(new RemovedApp(app.Id, app.Name, DateTime.Now));
        Directory.CreateDirectory(AppPaths.DataDirectory);
        File.WriteAllText(RemovedFile, JsonSerializer.Serialize(list));
    }

    public static void ForgetRemoved(string id)
    {
        var list = Removed().Where(r => r.Id != id).ToList();
        File.WriteAllText(RemovedFile, JsonSerializer.Serialize(list));
    }

    /// <summary>Link da Loja para reinstalar (busca pelo nome).</summary>
    public static string StoreLink(RemovedApp app) => $"ms-windows-store://search/?query={Uri.EscapeDataString(app.Name)}";

    // ── Tarefas agendadas ────────────────────────────────────────────────

    public static async Task<IReadOnlyList<TaskState>> TaskStatesAsync(CancellationToken cancellation = default)
    {
        var paths = string.Join(",", DebloatCatalog.Tasks.Select(t => ProcessRunner.PsQuote(t.Path)));
        var script = $$"""
            $out = foreach ($p in @({{paths}})) {
              $i = $p.LastIndexOf('\'); $folder = $p.Substring(0, $i + 1); $name = $p.Substring($i + 1)
              $t = Get-ScheduledTask -TaskPath $folder -TaskName $name -ErrorAction SilentlyContinue
              [pscustomobject]@{ Path = $p; Exists = [bool]$t; Enabled = [bool]($t -and $t.State -ne 'Disabled') }
            }
            ConvertTo-Json -InputObject @($out) -Compress
            """;
        var r = await ProcessRunner.PowerShellAsync(script, cancellation: cancellation);
        if (!r.Ok)
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<TaskState>>(r.Output.Trim()) ?? [];
    }

    public static async Task SetTaskEnabledAsync(BloatTask task, bool enabled)
    {
        if (!Elevation.IsAdministrator)
        {
            throw new UnauthorizedAccessException("Mudar tarefas do sistema precisa do Vapt aberto como administrador.");
        }

        var i = task.Path.LastIndexOf('\\');
        var verb = enabled ? "Enable-ScheduledTask" : "Disable-ScheduledTask";
        var r = await ProcessRunner.PowerShellAsync($"{verb} -TaskPath {ProcessRunner.PsQuote(task.Path[..(i + 1)])} -TaskName {ProcessRunner.PsQuote(task.Path[(i + 1)..])} | Out-Null");
        if (!r.Ok)
        {
            throw new InvalidOperationException(r.Error.Trim().Split('\n')[0]);
        }

        Log.Info($"Tarefa {task.Path} {(enabled ? "ligada" : "desligada")}");
    }
}
