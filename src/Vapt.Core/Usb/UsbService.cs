using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vapt.Core.Usb;

public sealed record ImageInfo(
    string Path,
    long Size,
    bool IsIso,
    /// <summary>Tem tabela de partição no início (ISO híbrida ou .img): dá para gravar em modo Raw.</summary>
    bool IsHybrid,
    bool IsWindows,
    string? VolumeLabel,
    /// <summary>Maior arquivo dentro da ISO (FAT32 não aceita arquivos acima de 4 GB).</summary>
    long LargestFile,
    string? LargestFileName)
{
    public bool NeedsSplit => LargestFile >= 4L * 1024 * 1024 * 1024;
}

/// <summary>
/// Pendrive no estilo Rufus. Discos via cmdlets de armazenamento do Windows (Get-Disk,
/// Clear-Disk, Initialize-Disk, New-Partition, Format-Volume); FAT32 acima de 32 GB com o
/// formatador próprio; imagens Raw direto no \\.\PhysicalDriveN com o disco offline.
/// </summary>
public static partial class UsbService
{
    private const string BasicDataGuid = "{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}";

    public static async Task<IReadOnlyList<UsbDisk>> ListAsync(CancellationToken cancellation = default)
    {
        // Só USB, SD e MMC; nunca o disco de boot/sistema.
        const string script = """
            $list = @(Get-Disk | Where-Object { @('USB','SD','MMC') -contains [string]$_.BusType -and -not $_.IsBoot -and -not $_.IsSystem } | ForEach-Object {
              $letters = @(Get-Partition -DiskNumber $_.Number -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter } | ForEach-Object { [string]$_.DriveLetter }) -join ','
              [pscustomobject]@{ Number = [int]$_.Number; UniqueId = [string]$_.UniqueId; Name = [string]$_.FriendlyName; Size = [long]$_.Size; BusType = [string]$_.BusType; PartitionStyle = [string]$_.PartitionStyle; Letters = $letters; ReadOnly = [bool]$_.IsReadOnly }
            })
            ConvertTo-Json -InputObject $list -Compress
            """;
        var result = await ProcessRunner.PowerShellAsync(script, cancellation: cancellation);
        if (!result.Ok)
        {
            throw new InvalidOperationException("Não foi possível listar os discos: " + result.Error.Trim());
        }

        var json = result.Output.Trim();
        if (json.Length == 0)
        {
            return [];
        }

        var disks = JsonSerializer.Deserialize<List<UsbDisk>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        var systemLetter = char.ToUpperInvariant(Environment.SystemDirectory[0]).ToString();
        return disks.Where(d => d.Size > 0 && !d.Letters.Split(',').Contains(systemLetter)).OrderBy(d => d.Number).ToList();
    }

    // ── Imagem ───────────────────────────────────────────────────────────

    public static async Task<ImageInfo> InspectAsync(string path, CancellationToken cancellation = default)
    {
        var size = new FileInfo(path).Length;
        var isIso = path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase);
        var hybrid = HasPartitionTable(path);
        if (!isIso)
        {
            return new ImageInfo(path, size, false, true, false, null, 0, null);
        }

        // Monta a ISO (somente leitura) para ver o conteúdo.
        var script = $$"""
            $img = Mount-DiskImage -ImagePath {{ProcessRunner.PsQuote(path)}} -StorageType ISO -Access ReadOnly -PassThru
            try {
              Start-Sleep -Milliseconds 300
              $vol = $img | Get-Volume
              $root = "$($vol.DriveLetter):\"
              $big = Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue | Sort-Object Length -Descending | Select-Object -First 1
              $win = (Test-Path (Join-Path $root 'sources\install.wim')) -or (Test-Path (Join-Path $root 'sources\install.esd'))
              [pscustomobject]@{ Label = [string]$vol.FileSystemLabel; Windows = [bool]$win; Largest = [long]$big.Length; LargestName = [string]$big.FullName.Substring(3) } | ConvertTo-Json -Compress
            } finally { Dismount-DiskImage -ImagePath {{ProcessRunner.PsQuote(path)}} | Out-Null }
            """;
        var result = await ProcessRunner.PowerShellAsync(script, cancellation: cancellation);
        if (!result.Ok)
        {
            Log.Warn($"Não deu para abrir a ISO {path}: {result.Error.Trim()}");
            return new ImageInfo(path, size, true, hybrid, false, null, 0, null);
        }

        using var doc = JsonDocument.Parse(result.Output.Trim());
        var r = doc.RootElement;
        return new ImageInfo(
            path,
            size,
            true,
            hybrid,
            r.GetProperty("Windows").GetBoolean(),
            r.GetProperty("Label").GetString(),
            r.GetProperty("Largest").GetInt64(),
            r.GetProperty("LargestName").GetString());
    }

    /// <summary>Assinatura 55 AA no fim do primeiro setor = há MBR/GPT protetor (imagem gravável em Raw).</summary>
    private static bool HasPartitionTable(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var sector = new byte[512];
            return f.Read(sector) == 512 && sector[510] == 0x55 && sector[511] == 0xAA;
        }
        catch (IOException)
        {
            return false;
        }
    }

    // ── Execução ─────────────────────────────────────────────────────────

    public static async Task<string?> RunAsync(UsbJob job, IProgress<UsbProgress> progress, CancellationToken cancellation)
    {
        if (!Elevation.IsAdministrator)
        {
            throw new UnauthorizedAccessException("Formatar pendrive precisa do Vapt aberto como administrador.");
        }

        progress.Report(new UsbProgress(0, "Conferindo o pendrive…"));
        var disk = await ConfirmDiskAsync(job.Disk, cancellation);
        Log.Info($"Pendrive: disco {disk.Number} \"{disk.Name}\" {Bytes.Format(disk.Size)}; {job.FileSystem}, {job.Scheme}, imagem={job.ImagePath ?? "-"} ({job.Mode}).");

        if (job.ImagePath is not null && job.Mode == ImageMode.Raw)
        {
            await WriteRawAsync(disk, job, progress, cancellation);
            return null;
        }

        var letter = await PartitionAndFormatAsync(disk, job, progress, job.ImagePath is null ? 100 : 15, cancellation);
        if (job.ImagePath is not null)
        {
            await CopyImageAsync(job, letter, progress, cancellation);
        }

        progress.Report(new UsbProgress(100, "Pronto"));
        return letter;
    }

    /// <summary>Relê a lista e confere que o número ainda aponta para o mesmo pendrive.</summary>
    private static async Task<UsbDisk> ConfirmDiskAsync(UsbDisk chosen, CancellationToken cancellation)
    {
        var now = await ListAsync(cancellation);
        var same = now.FirstOrDefault(d => d.UniqueId == chosen.UniqueId && d.Size == chosen.Size);
        if (same is null)
        {
            throw new InvalidOperationException("O pendrive escolhido não está mais conectado (ou mudou). Atualize a lista e escolha de novo.");
        }

        if (same.ReadOnly)
        {
            throw new InvalidOperationException("O pendrive está protegido contra gravação.");
        }

        return same;
    }

    private static async Task WriteRawAsync(UsbDisk disk, UsbJob job, IProgress<UsbProgress> progress, CancellationToken cancellation)
    {
        progress.Report(new UsbProgress(1, "Apagando partições…"));
        // Offline: o Windows não monta nada no meio da gravação.
        await PsOrThrow($$"""
            $d = Get-Disk -Number {{disk.Number}}
            if ($d.IsReadOnly) { Set-Disk -Number {{disk.Number}} -IsReadOnly $false }
            if ($d.PartitionStyle -ne 'RAW') { Clear-Disk -Number {{disk.Number}} -RemoveData -RemoveOEM -Confirm:$false }
            Set-Disk -Number {{disk.Number}} -IsOffline $true
            """, cancellation);
        try
        {
            await Task.Run(() => RawImageWriter.Write(disk.Number, job.ImagePath!, job.Verify, progress, cancellation), cancellation);
        }
        finally
        {
            await ProcessRunner.PowerShellAsync($"Set-Disk -Number {disk.Number} -IsOffline $false; Update-HostStorageCache", cancellation: CancellationToken.None);
        }

        progress.Report(new UsbProgress(100, "Pronto"));
    }

    private static async Task<string> PartitionAndFormatAsync(UsbDisk disk, UsbJob job, IProgress<UsbProgress> progress, double weight, CancellationToken cancellation)
    {
        progress.Report(new UsbProgress(2, "Apagando partições…"));
        var style = job.Scheme == PartitionScheme.Gpt ? "GPT" : "MBR";
        var partition = job.Scheme == PartitionScheme.Gpt
            ? $"New-Partition -DiskNumber {disk.Number} -UseMaximumSize -GptType '{BasicDataGuid}'"
            : $"New-Partition -DiskNumber {disk.Number} -UseMaximumSize -IsActive -MbrType {(job.FileSystem == UsbFileSystem.Fat32 ? "FAT32" : "IFS")}";
        // Sem letra por enquanto: um volume RAW com letra faz o Windows abrir "Formatar disco?".
        var prepared = await PsOrThrow($$"""
            $d = Get-Disk -Number {{disk.Number}}
            if ($d.IsOffline) { Set-Disk -Number {{disk.Number}} -IsOffline $false }
            if ($d.IsReadOnly) { Set-Disk -Number {{disk.Number}} -IsReadOnly $false }
            if ($d.PartitionStyle -ne 'RAW') { Clear-Disk -Number {{disk.Number}} -RemoveData -RemoveOEM -Confirm:$false }
            Initialize-Disk -Number {{disk.Number}} -PartitionStyle {{style}} -ErrorAction SilentlyContinue
            $p = {{partition}}
            Start-Sleep -Milliseconds 500
            $p = Get-Partition -DiskNumber {{disk.Number}} -PartitionNumber $p.PartitionNumber
            if ($p.DriveLetter) { Remove-PartitionAccessPath -DiskNumber {{disk.Number}} -PartitionNumber $p.PartitionNumber -AccessPath "$($p.DriveLetter):\" -ErrorAction SilentlyContinue }
            $guid = @($p.AccessPaths | Where-Object { $_ -like '\\?\Volume*' })[0]
            [pscustomobject]@{ Number = [int]$p.PartitionNumber; Volume = [string]$guid; Size = [long]$p.Size } | ConvertTo-Json -Compress
            """, cancellation);
        using var doc = JsonDocument.Parse(prepared.Trim());
        var partNumber = doc.RootElement.GetProperty("Number").GetInt32();
        var volume = doc.RootElement.GetProperty("Volume").GetString();
        var partSize = doc.RootElement.GetProperty("Size").GetInt64();

        var label = CleanLabel(job.Label, job.FileSystem);
        const long WindowsFat32Limit = 32L * 1024 * 1024 * 1024;
        if (job.FileSystem == UsbFileSystem.Fat32 && partSize > WindowsFat32Limit)
        {
            if (string.IsNullOrEmpty(volume))
            {
                throw new InvalidOperationException("O Windows não informou o volume novo; tente de novo.");
            }

            progress.Report(new UsbProgress(5, "Formatando em FAT32 (grande)…"));
            var p = new Progress<double>(f => progress.Report(new UsbProgress(5 + f * (weight - 10), "Formatando em FAT32…")));
            await Task.Run(() =>
            {
                using var dev = new LockedVolume(volume);
                Fat32Formatter.Format(dev, label, job.ClusterSize, p, cancellation);
            }, cancellation);
        }
        else
        {
            progress.Report(new UsbProgress(5, job.QuickFormat ? "Formatando…" : "Formatando (completa, pode demorar)…"));
            var fs = job.FileSystem switch
            {
                UsbFileSystem.Fat32 => "FAT32",
                UsbFileSystem.ExFat => "exFAT",
                _ => "NTFS",
            };
            var cluster = job.ClusterSize > 0 ? $" -AllocationUnitSize {job.ClusterSize}" : "";
            var full = job.QuickFormat ? "" : " -Full";
            await PsOrThrow($"Get-Partition -DiskNumber {disk.Number} -PartitionNumber {partNumber} | Format-Volume -FileSystem {fs} -NewFileSystemLabel {ProcessRunner.PsQuote(label)}{cluster}{full} -Force -Confirm:$false | Out-Null", cancellation);
        }

        progress.Report(new UsbProgress(weight - 3, "Montando o pendrive…"));
        var letterOut = await PsOrThrow($$"""
            Start-Sleep -Milliseconds 800
            $p = Get-Partition -DiskNumber {{disk.Number}} -PartitionNumber {{partNumber}}
            if (-not $p.DriveLetter) { $p | Add-PartitionAccessPath -AssignDriveLetter; Start-Sleep -Milliseconds 800; $p = Get-Partition -DiskNumber {{disk.Number}} -PartitionNumber {{partNumber}} }
            [string]$p.DriveLetter
            """, cancellation);
        var letter = letterOut.Trim();
        if (letter.Length != 1)
        {
            throw new InvalidOperationException("Formatado, mas o Windows não deu uma letra ao pendrive. Tire e coloque de novo.");
        }

        progress.Report(new UsbProgress(weight, "Formatado"));
        return letter;
    }

    private static async Task CopyImageAsync(UsbJob job, string letter, IProgress<UsbProgress> progress, CancellationToken cancellation)
    {
        var target = letter + @":\";
        progress.Report(new UsbProgress(16, "Abrindo a imagem…"));
        var mounted = await PsOrThrow($"(Mount-DiskImage -ImagePath {ProcessRunner.PsQuote(job.ImagePath!)} -StorageType ISO -Access ReadOnly -PassThru | Get-Volume).DriveLetter", cancellation);
        var source = mounted.Trim() + @":\";
        try
        {
            var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f)).ToList();
            var fat32 = job.FileSystem == UsbFileSystem.Fat32;
            const long FatMax = 4L * 1024 * 1024 * 1024 - 1;
            var tooBig = fat32 ? files.Where(f => f.Length > FatMax).ToList() : [];
            var splitWim = tooBig.FirstOrDefault(f => f.Name.Equals("install.wim", StringComparison.OrdinalIgnoreCase));
            var other = tooBig.Where(f => f != splitWim).ToList();
            if (other.Count > 0)
            {
                throw new InvalidOperationException($"\"{other[0].Name}\" tem mais de 4 GB e não cabe em FAT32. Escolha NTFS ou exFAT (só para dados) ou use o modo de gravar imagem.");
            }

            var toCopy = files.Where(f => f != splitWim).ToList();
            var total = Math.Max(1, toCopy.Sum(f => f.Length) + (splitWim?.Length ?? 0));
            long done = 0;
            var buffer = new byte[4 << 20];
            var started = DateTime.UtcNow;
            foreach (var file in toCopy)
            {
                cancellation.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(source, file.FullName);
                var dest = Path.Combine(target, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                await using (var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan | FileOptions.Asynchronous))
                await using (var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous))
                {
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellation)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
                        done += read;
                        var speed = done / Math.Max(0.5, (DateTime.UtcNow - started).TotalSeconds);
                        progress.Report(new UsbProgress(16 + 80.0 * done / total, $"Copiando {rel}… {Bytes.Format((long)speed)}/s"));
                    }
                }

                File.SetAttributes(dest, FileAttributes.Normal);
            }

            if (splitWim is not null)
            {
                // Mesmo método da Microsoft para FAT32: install.wim dividido em .swm de 3,8 GB.
                var rel = Path.GetRelativePath(source, splitWim.FullName);
                var swm = Path.Combine(target, Path.ChangeExtension(rel, ".swm"));
                Directory.CreateDirectory(Path.GetDirectoryName(swm)!);
                var baseDone = done;
                var result = await ProcessRunner.RunAsync(
                    Path.Combine(ProcessRunner.System32, "dism.exe"),
                    $"/Split-Image /ImageFile:\"{splitWim.FullName}\" /SWMFile:\"{swm}\" /FileSize:3800",
                    line =>
                    {
                        var m = DismPercent().Match(line);
                        if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pct))
                        {
                            progress.Report(new UsbProgress(16 + 80.0 * (baseDone + splitWim.Length * pct / 100) / total, $"Dividindo install.wim para caber em FAT32… {pct:0}%"));
                        }
                    },
                    cancellation);
                if (!result.Ok)
                {
                    throw new InvalidOperationException("O DISM não conseguiu dividir o install.wim: " + result.Output.Trim().Split('\n').LastOrDefault());
                }
            }

            // BIOS antigo (MBR): código de boot do próprio Windows.
            if (job.Scheme == PartitionScheme.Mbr && File.Exists(Path.Combine(source, "boot", "bootsect.exe")))
            {
                progress.Report(new UsbProgress(97, "Gravando o código de boot…"));
                await ProcessRunner.RunAsync(Path.Combine(source, "boot", "bootsect.exe"), $"/nt60 {letter}: /force /mbr", cancellation: cancellation);
            }

            if (job.SetupTweaks is { Any: true } tweaks && File.Exists(Path.Combine(source, "sources", "boot.wim")))
            {
                await File.WriteAllTextAsync(Path.Combine(target, "autounattend.xml"), Unattend.Build(tweaks), new UTF8Encoding(false), cancellation);
            }
        }
        finally
        {
            await ProcessRunner.PowerShellAsync($"Dismount-DiskImage -ImagePath {ProcessRunner.PsQuote(job.ImagePath!)} | Out-Null", cancellation: CancellationToken.None);
        }
    }

    public static string CleanLabel(string label, UsbFileSystem fs)
    {
        if (fs == UsbFileSystem.Fat32)
        {
            return Fat32Formatter.NormalizeLabel(label).TrimEnd();
        }

        var max = fs == UsbFileSystem.ExFat ? 15 : 32;
        var clean = new string((label ?? "").Where(c => !"\"*/:<>?\\|".Contains(c) && !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > max ? clean[..max] : clean;
    }

    private static async Task<string> PsOrThrow(string script, CancellationToken cancellation)
    {
        var result = await ProcessRunner.PowerShellAsync(script, cancellation: cancellation);
        if (!result.Ok)
        {
            var message = result.Error.Trim();
            Log.Error($"PowerShell falhou: {message}\n{script}");
            throw new InvalidOperationException(FirstLine(message));
        }

        return result.Output;
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "erro desconhecido";
        return line.Length > 300 ? line[..300] : line;
    }

    [GeneratedRegex(@"([0-9]+(?:\.[0-9]+)?)%")]
    private static partial Regex DismPercent();
}

/// <summary>autounattend.xml com as mesmas chaves que o Rufus documenta (LabConfig, BypassNRO...).</summary>
public static class Unattend
{
    public static string Build(WindowsSetupTweaks t)
    {
        var pe = new List<string>();
        if (t.BypassRequirements)
        {
            foreach (var v in new[] { "BypassTPMCheck", "BypassSecureBootCheck", "BypassRAMCheck", "BypassStorageCheck", "BypassCPUCheck" })
            {
                pe.Add($@"reg add HKLM\SYSTEM\Setup\LabConfig /v {v} /t REG_DWORD /d 1 /f");
            }
        }

        var special = new List<string>();
        if (t.AllowLocalAccount)
        {
            special.Add(@"reg add HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE /v BypassNRO /t REG_DWORD /d 1 /f");
        }

        if (t.DisableBitLocker)
        {
            special.Add(@"reg add HKLM\SYSTEM\CurrentControlSet\Control\BitLocker /v PreventDeviceEncryption /t REG_DWORD /d 1 /f");
        }

        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
        sb.AppendLine("""<unattend xmlns="urn:schemas-microsoft-com:unattend" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State">""");
        void Pass(string pass, string component, List<string> commands)
        {
            if (commands.Count == 0)
            {
                return;
            }

            sb.AppendLine($"""  <settings pass="{pass}">""");
            foreach (var arch in new[] { "amd64", "arm64" })
            {
                sb.AppendLine($"""    <component name="{component}" processorArchitecture="{arch}" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">""");
                sb.AppendLine("      <RunSynchronous>");
                for (var i = 0; i < commands.Count; i++)
                {
                    sb.AppendLine("""        <RunSynchronousCommand wcm:action="add">""");
                    sb.AppendLine($"          <Order>{i + 1}</Order>");
                    sb.AppendLine($"          <Path>{System.Security.SecurityElement.Escape(commands[i])}</Path>");
                    sb.AppendLine("        </RunSynchronousCommand>");
                }

                sb.AppendLine("      </RunSynchronous>");
                sb.AppendLine("    </component>");
            }

            sb.AppendLine("  </settings>");
        }

        Pass("windowsPE", "Microsoft-Windows-Setup", pe);
        Pass("specialize", "Microsoft-Windows-Deployment", special);
        if (t.AllowLocalAccount)
        {
            sb.AppendLine("""  <settings pass="oobeSystem">""");
            foreach (var arch in new[] { "amd64", "arm64" })
            {
                sb.AppendLine($"""    <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="{arch}" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS">""");
                sb.AppendLine("      <OOBE><HideOnlineAccountScreens>true</HideOnlineAccountScreens></OOBE>");
                sb.AppendLine("    </component>");
            }

            sb.AppendLine("  </settings>");
        }

        sb.AppendLine("</unattend>");
        return sb.ToString();
    }
}
