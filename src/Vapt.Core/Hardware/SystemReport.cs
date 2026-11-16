using System.Management;
using Microsoft.Win32;

namespace Vapt.Core.Hardware;

public sealed record InfoItem(string Label, string Value);

public sealed record RamModule(string Slot, long Bytes, string Type, int SpeedMts, int ConfiguredMts, string Manufacturer, string PartNumber, string FormFactor, double? Voltage);

public sealed record GpuInfo(string Name, long VramBytes, string Driver, string DriverDate, string Resolution);

public enum Health
{
    Good,
    Warning,
    Critical,
    Unknown,
}

public sealed record DiskHealthInfo(
    string Name,
    string Kind,
    string Bus,
    long Size,
    Health Verdict,
    IReadOnlyList<string> Reasons,
    double? Temperature,
    double? WearPercent,
    long? PowerOnHours,
    long? ReadErrors,
    long? WriteErrors,
    bool CountersAvailable);

public sealed record BatteryHealth(string Name, long DesignMWh, long FullMWh, long? Cycles, int ChargePercent, string State)
{
    public double WearPercent => DesignMWh > 0 ? Math.Clamp(100 - FullMWh * 100.0 / DesignMWh, 0, 100) : 0;

    public Health Verdict => WearPercent switch
    {
        >= 40 => Health.Critical,
        >= 20 => Health.Warning,
        _ => Health.Good,
    };
}

public sealed record SystemReport(
    IReadOnlyList<InfoItem> System,
    IReadOnlyList<InfoItem> Board,
    IReadOnlyList<InfoItem> Cpu,
    IReadOnlyList<InfoItem> RamSummary,
    IReadOnlyList<RamModule> RamModules,
    IReadOnlyList<GpuInfo> Gpus,
    IReadOnlyList<DiskHealthInfo> Disks,
    BatteryHealth? Battery,
    DateTime CreatedAt);

/// <summary>
/// Ficha do PC pelo WMI (como a aba Sistema do ResetExplode e o CPU-Z): SO, placa-mãe, CPU,
/// RAM por módulo, GPU, saúde dos discos (S.M.A.R.T. pelo Windows Storage) e bateria.
/// </summary>
public static class SystemReportBuilder
{
    public static Task<SystemReport> BuildAsync() => Task.Run(Build);

    private static SystemReport Build()
    {
        var system = new List<InfoItem>();
        var board = new List<InfoItem>();
        var cpu = new List<InfoItem>();
        var ramSummary = new List<InfoItem>();

        foreach (var os in Query(@"root\cimv2", "SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime, CSName FROM Win32_OperatingSystem"))
        {
            system.Add(new("Sistema", SystemInfo.WindowsVersion));
            system.Add(new("Arquitetura", S(os["OSArchitecture"])));
            system.Add(new("Nome do PC", S(os["CSName"])));
            if (Date(os["InstallDate"]) is { } installed)
            {
                system.Add(new("Instalado em", installed.ToString("dd/MM/yyyy")));
            }

            if (Date(os["LastBootUpTime"]) is { } boot)
            {
                system.Add(new("Ligado desde", boot.ToString("dd/MM/yyyy HH:mm")));
            }
        }

        foreach (var cs in Query(@"root\cimv2", "SELECT Manufacturer, Model, SystemType FROM Win32_ComputerSystem"))
        {
            board.Add(new("Fabricante do PC", S(cs["Manufacturer"])));
            board.Add(new("Modelo", S(cs["Model"])));
        }

        foreach (var b in Query(@"root\cimv2", "SELECT Manufacturer, Product, Version FROM Win32_BaseBoard"))
        {
            board.Add(new("Placa-mãe", $"{S(b["Manufacturer"])} {S(b["Product"])}".Trim()));
        }

        foreach (var bios in Query(@"root\cimv2", "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
        {
            var date = Date(bios["ReleaseDate"]);
            board.Add(new("BIOS", $"{S(bios["SMBIOSBIOSVersion"])}{(date is null ? "" : $" ({date:dd/MM/yyyy})")}"));
        }

        board.Add(new("Inicialização", IsUefi() ? "UEFI" : "BIOS legado"));
        board.Add(new("Secure Boot", SecureBoot() switch { true => "Ligado", false => "Desligado", _ => "Não disponível" }));

        foreach (var p in Query(@"root\cimv2", "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation, VirtualizationFirmwareEnabled FROM Win32_Processor"))
        {
            cpu.Add(new("Processador", S(p["Name"]).Trim()));
            cpu.Add(new("Núcleos / threads", $"{N(p["NumberOfCores"])} / {N(p["NumberOfLogicalProcessors"])}"));
            cpu.Add(new("Clock base", $"{N(p["MaxClockSpeed"]) / 1000.0:0.00} GHz"));
            var l2 = N(p["L2CacheSize"]);
            var l3 = N(p["L3CacheSize"]);
            cpu.Add(new("Cache", $"L2 {l2 / 1024.0:0.#} MB · L3 {l3 / 1024.0:0.#} MB"));
            cpu.Add(new("Soquete", S(p["SocketDesignation"])));
            cpu.Add(new("Virtualização", p["VirtualizationFirmwareEnabled"] is true ? "Ligada na BIOS" : "Desligada na BIOS"));
            break; // um processador basta (PCs com dois soquetes são raros)
        }

        // RAM por módulo.
        var modules = new List<RamModule>();
        foreach (var m in Query(@"root\cimv2", "SELECT DeviceLocator, BankLabel, Capacity, SMBIOSMemoryType, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, FormFactor, ConfiguredVoltage FROM Win32_PhysicalMemory"))
        {
            var volts = N(m["ConfiguredVoltage"]);
            modules.Add(new RamModule(
                S(m["DeviceLocator"]),
                (long)N(m["Capacity"]),
                MemoryType((int)N(m["SMBIOSMemoryType"])),
                (int)N(m["Speed"]),
                (int)N(m["ConfiguredClockSpeed"]),
                S(m["Manufacturer"]).Trim(),
                S(m["PartNumber"]).Trim(),
                N(m["FormFactor"]) switch { 8 => "DIMM", 12 => "SO-DIMM", _ => "" },
                volts > 0 ? volts / 1000.0 : null));
        }

        var slots = Query(@"root\cimv2", "SELECT MemoryDevices FROM Win32_PhysicalMemoryArray").Sum(a => N(a["MemoryDevices"]));
        var (total, _) = SystemInfo.Memory();
        ramSummary.Add(new("Total", Bytes.Format(modules.Count > 0 ? modules.Sum(m => m.Bytes) : total)));
        if (modules.Count > 0)
        {
            var speed = modules.Max(m => m.ConfiguredMts);
            var rated = modules.Max(m => m.SpeedMts);
            ramSummary.Add(new("Tipo", modules[0].Type));
            ramSummary.Add(new("Velocidade", speed > 0 ? $"{speed} MT/s" + (rated > speed ? $" (aguenta {rated} MT/s: ative o XMP/EXPO na BIOS)" : "") : "—"));
            ramSummary.Add(new("Módulos", $"{modules.Count} de {(slots > 0 ? slots : modules.Count)} slots" + (modules.Count == 1 && slots >= 2 ? " · um pente só roda em canal simples" : "")));
        }

        return new SystemReport(system, board, cpu, ramSummary, modules, Gpus(), Disks(), Battery(), DateTime.Now);
    }

    // ── GPU ──────────────────────────────────────────────────────────────

    private static List<GpuInfo> Gpus()
    {
        var vram = VramFromRegistry();
        var list = new List<GpuInfo>();
        foreach (var g in Query(@"root\cimv2", "SELECT Name, AdapterRAM, DriverVersion, DriverDate, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController"))
        {
            var name = S(g["Name"]);
            if (name.Contains("Remote", StringComparison.OrdinalIgnoreCase) || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) || name.Contains("Parsec", StringComparison.OrdinalIgnoreCase) || name.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // AdapterRAM do WMI para em 4 GB; o registro tem o valor real.
            var bytes = vram.TryGetValue(name, out var v) ? v : (long)N(g["AdapterRAM"]);
            var w = N(g["CurrentHorizontalResolution"]);
            var res = w > 0 ? $"{w}×{N(g["CurrentVerticalResolution"])} a {N(g["CurrentRefreshRate"])} Hz" : "";
            list.Add(new GpuInfo(name, bytes, S(g["DriverVersion"]), Date(g["DriverDate"])?.ToString("dd/MM/yyyy") ?? "", res));
        }

        return list;
    }

    private static Dictionary<string, long> VramFromRegistry()
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var sub in cls?.GetSubKeyNames() ?? [])
            {
                using var k = cls!.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is string desc && k.GetValue("HardwareInformation.qwMemorySize") is long mem)
                {
                    map[desc] = mem;
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        return map;
    }

    // ── Discos (S.M.A.R.T.) ──────────────────────────────────────────────

    private static List<DiskHealthInfo> Disks()
    {
        const string storage = @"root\Microsoft\Windows\Storage";
        var counters = new Dictionary<string, ManagementBaseObject>();
        foreach (var c in Query(storage, "SELECT DeviceId, Temperature, Wear, PowerOnHours, ReadErrorsTotal, WriteErrorsTotal, ReadErrorsUncorrected FROM MSFT_StorageReliabilityCounter"))
        {
            counters[S(c["DeviceId"])] = c;
        }

        var list = new List<DiskHealthInfo>();
        foreach (var d in Query(storage, "SELECT DeviceId, FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk"))
        {
            var id = S(d["DeviceId"]);
            var kind = N(d["MediaType"]) switch { 3 => "HD", 4 => "SSD", 5 => "SCM", _ => "Disco" };
            var bus = N(d["BusType"]) switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 12 => "SD", _ => "" };
            if (bus == "NVMe")
            {
                kind = "SSD";
            }

            counters.TryGetValue(id, out var c);
            double? temp = c is null ? null : NullIfZero(N(c["Temperature"]));
            double? wear = c is null || c["Wear"] is null ? null : N(c["Wear"]);
            long? hours = c is null || c["PowerOnHours"] is null ? null : (long)N(c["PowerOnHours"]);
            long? readErr = c is null || c["ReadErrorsUncorrected"] is null ? null : (long)N(c["ReadErrorsUncorrected"]);
            long? writeErr = c is null || c["WriteErrorsTotal"] is null ? null : (long)N(c["WriteErrorsTotal"]);

            var reasons = new List<string>();
            var verdict = Health.Good;
            void Flag(Health h, string why)
            {
                reasons.Add(why);
                if (h > verdict || verdict == Health.Unknown)
                {
                    verdict = h;
                }
            }

            switch (N(d["HealthStatus"]))
            {
                case 1:
                    Flag(Health.Warning, "O Windows marcou o disco com aviso de saúde.");
                    break;
                case 2:
                    Flag(Health.Critical, "O Windows marcou o disco como com defeito. Faça backup já.");
                    break;
            }

            if (wear is >= 90)
            {
                Flag(Health.Critical, $"SSD com {wear:0}% da vida útil gasta.");
            }
            else if (wear is >= 70)
            {
                Flag(Health.Warning, $"SSD com {wear:0}% da vida útil gasta.");
            }

            if (temp is >= 70)
            {
                Flag(Health.Warning, $"Temperatura alta: {temp:0} °C.");
            }

            if (readErr is > 0)
            {
                Flag(Health.Warning, $"{readErr} erros de leitura que não puderam ser corrigidos.");
            }

            if (reasons.Count == 0)
            {
                reasons.Add(c is null ? "Sem os contadores detalhados (abra como administrador para ver temperatura e desgaste)." : "Nenhum problema nos contadores do disco.");
            }

            list.Add(new DiskHealthInfo(S(d["FriendlyName"]), kind, bus, (long)N(d["Size"]), verdict, reasons, temp, wear, hours, readErr, writeErr, c is not null));
        }

        return list.OrderBy(d => d.Name).ToList();
    }

    // ── Bateria ──────────────────────────────────────────────────────────

    private static BatteryHealth? Battery()
    {
        var win = Query(@"root\cimv2", "SELECT Name, EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery").FirstOrDefault();
        if (win is null)
        {
            return null;
        }

        var design = Query(@"root\wmi", "SELECT DesignedCapacity FROM BatteryStaticData").Select(b => (long)N(b["DesignedCapacity"])).FirstOrDefault();
        var full = Query(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity").Select(b => (long)N(b["FullChargedCapacity"])).FirstOrDefault();
        long? cycles = Query(@"root\wmi", "SELECT CycleCount FROM BatteryCycleCount").Select(b => (long)N(b["CycleCount"])).FirstOrDefault() is var cc and > 0 ? cc : null;
        var state = N(win["BatteryStatus"]) switch
        {
            2 => "Na tomada",
            6 or 7 or 8 or 9 => "Carregando",
            1 => "Na bateria",
            _ => "",
        };
        return new BatteryHealth(S(win["Name"]), design, full, cycles, (int)N(win["EstimatedChargeRemaining"]), state);
    }

    // ── Firmware ─────────────────────────────────────────────────────────

    private static bool IsUefi()
    {
        // GetFirmwareEnvironmentVariable falha com ERROR_INVALID_FUNCTION (1) em BIOS legado.
        _ = GetFirmwareEnvironmentVariableW("", "{00000000-0000-0000-0000-000000000000}", 0, 0);
        return System.Runtime.InteropServices.Marshal.GetLastWin32Error() != 1;
    }

    private static bool? SecureBoot()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
        return key?.GetValue("UEFISecureBootEnabled") is int v ? v == 1 : null;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFirmwareEnvironmentVariableW(string name, string guid, nint buffer, uint size);

    // ── Utilidades ───────────────────────────────────────────────────────

    private static List<ManagementBaseObject> Query(string scope, string wql)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, wql);
            return searcher.Get().Cast<ManagementBaseObject>().ToList();
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Log.Warn($"WMI {scope}: {ex.Message}");
            return [];
        }
    }

    private static string S(object? o) => o?.ToString() ?? "";

    private static double N(object? o) => o is null ? 0 : Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture);

    private static double? NullIfZero(double v) => v > 0 ? v : null;

    private static DateTime? Date(object? o)
    {
        try
        {
            return o is string s && s.Length >= 14 ? ManagementDateTimeConverter.ToDateTime(s) : null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string MemoryType(int smbios) => smbios switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        27 => "LPDDR",
        28 => "LPDDR2",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => "RAM",
    };
}
