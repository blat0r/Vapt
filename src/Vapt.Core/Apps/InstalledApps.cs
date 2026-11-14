using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;

namespace Vapt.Core.Apps;

public sealed record InstalledApp(
    string Name,
    string? Publisher,
    string? Version,
    long SizeBytes,
    DateTime? InstallDate,
    string? UninstallCommand,
    string? QuietUninstallCommand,
    string? InstallLocation,
    string? IconPath,
    bool IsStoreApp,
    string? PackageFullName,
    /// <summary>Chave do registro da entrada ("HKLM\...\Uninstall\{id}").</summary>
    string? KeyPath = null,
    /// <summary>O desinstalador não existe mais (programa apagado na mão): só sobrou a entrada.</summary>
    bool Broken = false)
{
    /// <summary>MSI: dá para desinstalar em silêncio com msiexec /x.</summary>
    public string? MsiProductCode =>
        UninstallCommand is { } u && u.Contains("msiexec", StringComparison.OrdinalIgnoreCase)
            && System.Text.RegularExpressions.Regex.Match(u, @"\{[0-9A-Fa-f\-]{36}\}") is { Success: true } m ? m.Value : null;

    public bool CanUninstallSilently => QuietUninstallCommand is not null || MsiProductCode is not null;
}

/// <summary>Programas instalados (chaves Uninstall do registro) e apps da Loja (Appx).</summary>
public static class InstalledApps
{
    private static readonly (RegistryKey Root, string Path)[] Sources =
    [
        (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
    ];

    public static IReadOnlyList<InstalledApp> ListDesktop()
    {
        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, path) in Sources)
        {
            using var key = root.OpenSubKey(path);
            if (key is null)
            {
                continue;
            }

            foreach (var sub in key.GetSubKeyNames())
            {
                using var app = key.OpenSubKey(sub);
                if (app is null)
                {
                    continue;
                }

                var name = (app.GetValue("DisplayName") as string)?.Trim();
                // Componentes do sistema e atualizações aparecem escondidos no Painel de Controle também.
                if (string.IsNullOrEmpty(name)
                    || app.GetValue("SystemComponent") is 1
                    || app.GetValue("ParentKeyName") is string
                    || app.GetValue("ReleaseType") is "Update" or "Hotfix" or "Security Update"
                    || app.GetValue("UninstallString") is not string uninstall)
                {
                    continue;
                }

                var size = app.GetValue("EstimatedSize") is int kb ? kb * 1024L : 0;
                DateTime? date = app.GetValue("InstallDate") is string d && DateTime.TryParseExact(d, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
                var icon = (app.GetValue("DisplayIcon") as string)?.Split(',')[0].Trim('"');
                var keyPath = (root == Registry.LocalMachine ? "HKLM\\" : "HKCU\\") + path + "\\" + sub;
                var entry = new InstalledApp(
                    name,
                    (app.GetValue("Publisher") as string)?.Trim(),
                    app.GetValue("DisplayVersion") as string,
                    size,
                    date,
                    uninstall,
                    app.GetValue("QuietUninstallString") as string,
                    app.GetValue("InstallLocation") as string,
                    icon,
                    false,
                    null,
                    keyPath,
                    IsBroken(uninstall));
                // Mesmo app em 32 e 64 bits: fica um.
                apps.TryAdd(name + "|" + entry.Version, entry);
            }
        }

        return apps.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Desinstalador apontando para um arquivo que não existe (ignora msiexec, rundll32 etc.).</summary>
    internal static bool IsBroken(string uninstall)
    {
        var exe = Startup.StartupManager.ExtractExecutable(uninstall);
        if (string.IsNullOrWhiteSpace(exe) || !Path.IsPathRooted(exe))
        {
            return false; // comando do sistema (MsiExec.exe, rundll32...) resolvido pelo PATH
        }

        var file = Path.GetFileName(exe);
        if (file.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) || file.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            // HD externo desconectado: o programa pode estar inteiro lá, então não é "quebrado".
            var root = Path.GetPathRoot(exe);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                return false;
            }

            return !File.Exists(exe);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Apps da Loja que o usuário pode remover (sem frameworks nem pacotes do sistema).</summary>
    public static async Task<IReadOnlyList<InstalledApp>> ListStoreAsync(CancellationToken cancellation = default)
    {
        const string script = """
            $apps = @(Get-AppxPackage -PackageTypeFilter Main | Where-Object { -not $_.IsFramework -and -not $_.NonRemovable -and $_.SignatureKind -eq 'Store' } | ForEach-Object {
              $name = $_.Name
              try { $m = Get-AppxPackageManifest $_; $dn = [string]$m.Package.Properties.DisplayName; if ($dn -and -not $dn.StartsWith('ms-resource')) { $name = $dn } } catch {}
              [pscustomobject]@{ Name = $name; Publisher = [string]$_.PublisherId; Version = [string]$_.Version; Full = $_.PackageFullName; Location = [string]$_.InstallLocation }
            })
            ConvertTo-Json -InputObject $apps -Compress
            """;
        var result = await ProcessRunner.PowerShellAsync(script, cancellation: cancellation);
        if (!result.Ok || result.Output.Trim().Length == 0)
        {
            return [];
        }

        using var doc = JsonDocument.Parse(result.Output.Trim());
        var list = new List<InstalledApp>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var name = e.GetProperty("Name").GetString() ?? "";
            var location = e.GetProperty("Location").GetString();
            list.Add(new InstalledApp(PrettyStoreName(name), null, e.GetProperty("Version").GetString(), 0, null, null, null, location, null, true, e.GetProperty("Full").GetString()));
        }

        return list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>"Microsoft.BingNews" → "Bing News" quando o manifesto não tem nome legível.</summary>
    private static string PrettyStoreName(string name)
    {
        if (name.Contains(' '))
        {
            return name;
        }

        var last = name.Split('.').Last();
        return System.Text.RegularExpressions.Regex.Replace(last, "(?<=[a-z])(?=[A-Z])", " ");
    }

    /// <summary>
    /// Roda o desinstalador. silent: usa o comando silencioso do próprio programa ou msiexec /x
    /// /qn (como o Bulk Crap Uninstaller); sem isso, abre o desinstalador normal.
    /// </summary>
    public static async Task<bool> UninstallAsync(InstalledApp app, bool silent = false)
    {
        if (app.IsStoreApp)
        {
            var r = await ProcessRunner.PowerShellAsync($"Remove-AppxPackage -Package {ProcessRunner.PsQuote(app.PackageFullName!)}");
            Log.Info($"Remover app da Loja {app.Name}: {(r.Ok ? "ok" : r.Error.Trim())}");
            return r.Ok;
        }

        if (app.Broken)
        {
            return false; // não há desinstalador; o caminho é "Remover entrada" + sobras
        }

        var command = app.UninstallCommand!;
        if (silent && app.QuietUninstallCommand is { } quiet)
        {
            command = quiet;
        }
        else if (silent && app.MsiProductCode is { } code)
        {
            command = $"MsiExec.exe /x {code} /qn /norestart";
        }
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(ProcessRunner.System32, "cmd.exe"), $"/c \"{command}\"")
            {
                UseShellExecute = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is not null)
            {
                await p.WaitForExitAsync();
            }

            Log.Info($"Desinstalador de {app.Name} terminou ({command}).");
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error($"Desinstalar {app.Name}.", ex);
            return false;
        }
    }
}
