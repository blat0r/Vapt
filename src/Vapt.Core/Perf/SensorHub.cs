using LibreHardwareMonitor.Hardware;

namespace Vapt.Core.Perf;

public enum SensorKind
{
    Temperature,
    Clock,
    Load,
    Power,
    Fan,
    Voltage,
    Data,
    Other,
}

public sealed record SensorReading(
    string Hardware,
    string HardwareKind,
    string Name,
    SensorKind Kind,
    float Value,
    float Min,
    float Max)
{
    public string Unit => Kind switch
    {
        SensorKind.Temperature => "°C",
        SensorKind.Clock => "MHz",
        SensorKind.Load => "%",
        SensorKind.Power => "W",
        SensorKind.Fan => "RPM",
        SensorKind.Voltage => "V",
        SensorKind.Data => "GB",
        _ => "",
    };

    public string Format(float v) => Kind switch
    {
        SensorKind.Voltage => $"{v:0.000} V",
        SensorKind.Power => $"{v:0.0} W",
        SensorKind.Data => $"{v:0.0} GB",
        SensorKind.Fan or SensorKind.Clock => $"{v:0} {Unit}",
        _ => $"{v:0} {Unit}",
    };
}

/// <summary>Os números principais (para os blocos do topo, o overlay e o stress test).</summary>
public sealed record SensorSummary(
    float? CpuTemp,
    float? CpuLoad,
    float? CpuClock,
    float? CpuPower,
    string CpuName,
    float? GpuTemp,
    float? GpuHotspot,
    float? GpuLoad,
    float? GpuClock,
    float? GpuMemClock,
    float? GpuPower,
    float? VramUsedGb,
    float? VramTotalGb,
    string GpuName,
    float? RamLoad);

/// <summary>
/// Sensores do PC pelo LibreHardwareMonitor (MPL-2.0), lidos uma vez por segundo numa thread
/// própria e compartilhados por todas as telas. Liga quando alguém usa e desliga sozinho depois.
/// Temperatura e consumo da CPU precisam do driver PawnIO (o WinRing0 antigo é bloqueado pelo
/// Defender); a GPU, a RAM e os discos funcionam sem ele.
/// </summary>
public static class SensorHub
{
    private static readonly Lock Gate = new();
    private static Thread? _thread;
    private static int _users;
    private static volatile IReadOnlyList<SensorReading> _readings = [];
    private static volatile SensorSummary? _summary;

    public static IReadOnlyList<SensorReading> Readings => _readings;

    public static SensorSummary? Summary => _summary;

    public static bool Ready => _summary is not null;

    /// <summary>Disparado (fora da thread da interface) a cada leitura.</summary>
    public static event Action? Updated;

    /// <summary>O driver que dá acesso à temperatura e ao consumo da CPU.</summary>
    public static bool PawnIoInstalled
    {
        get
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            return key is not null;
        }
    }

    /// <summary>Começa a ler (contagem de uso: cada tela chama Acquire/Release).</summary>
    public static void Acquire()
    {
        lock (Gate)
        {
            _users++;
            if (_thread is { IsAlive: true })
            {
                return;
            }

            _thread = new Thread(Loop) { IsBackground = true, Name = "Vapt.Sensors", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }
    }

    public static void Release()
    {
        lock (Gate)
        {
            _users = Math.Max(0, _users - 1);
        }
    }

    private static void Loop()
    {
        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
            IsControllerEnabled = true,
        };
        try
        {
            computer.Open();
            Log.Info($"Sensores abertos ({computer.Hardware.Count} componentes, PawnIO: {PawnIoInstalled}).");
            var idleSince = DateTime.MaxValue;
            while (true)
            {
                lock (Gate)
                {
                    // Sem ninguém olhando por 15 s: desliga (não fica gastando CPU à toa).
                    if (_users == 0)
                    {
                        if (idleSince == DateTime.MaxValue)
                        {
                            idleSince = DateTime.UtcNow;
                        }
                        else if (DateTime.UtcNow - idleSince > TimeSpan.FromSeconds(15))
                        {
                            _thread = null;
                            break;
                        }
                    }
                    else
                    {
                        idleSince = DateTime.MaxValue;
                    }
                }

                Read(computer);
                Updated?.Invoke();
                Thread.Sleep(1000);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Sensores pararam.", ex);
            lock (Gate)
            {
                _thread = null;
            }
        }
        finally
        {
            try
            {
                computer.Close();
            }
            catch (Exception)
            {
            }

            _summary = null;
        }
    }

    private static void Read(Computer computer)
    {
        var list = new List<SensorReading>(256);
        foreach (var hw in computer.Hardware)
        {
            Collect(hw, list);
        }

        _readings = list;
        _summary = Summarize(list);
    }

    private static void Collect(IHardware hw, List<SensorReading> list)
    {
        try
        {
            hw.Update();
        }
        catch (Exception ex)
        {
            Log.Warn($"Sensor {hw.Name}: {ex.Message}");
            return;
        }

        foreach (var sub in hw.SubHardware)
        {
            Collect(sub, list);
        }

        foreach (var s in hw.Sensors)
        {
            if (s.Value is not { } value || float.IsNaN(value))
            {
                continue;
            }

            var kind = s.SensorType switch
            {
                SensorType.Temperature => SensorKind.Temperature,
                SensorType.Clock => SensorKind.Clock,
                SensorType.Load => SensorKind.Load,
                SensorType.Power => SensorKind.Power,
                SensorType.Fan => SensorKind.Fan,
                SensorType.Voltage => SensorKind.Voltage,
                SensorType.Data or SensorType.SmallData => SensorKind.Data,
                _ => SensorKind.Other,
            };
            if (kind == SensorKind.Other)
            {
                continue;
            }

            // SmallData vem em MB: padroniza em GB.
            var scale = s.SensorType == SensorType.SmallData ? 1f / 1024 : 1f;
            list.Add(new SensorReading(
                hw.Name,
                Friendly(hw.HardwareType),
                s.Name,
                kind,
                value * scale,
                (s.Min ?? value) * scale,
                (s.Max ?? value) * scale));
        }
    }

    private static string Friendly(HardwareType t) => t switch
    {
        HardwareType.Cpu => "Processador",
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "Placa de vídeo",
        HardwareType.Memory => "Memória",
        HardwareType.Motherboard or HardwareType.SuperIO => "Placa-mãe",
        HardwareType.Storage => "Disco",
        HardwareType.Battery => "Bateria",
        HardwareType.Cooler => "Refrigeração",
        _ => "Outros",
    };

    internal static SensorSummary Summarize(IReadOnlyList<SensorReading> r)
    {
        float? Find(string kindName, SensorKind kind, params string[] names)
        {
            foreach (var n in names)
            {
                var hit = r.FirstOrDefault(x => x.HardwareKind == kindName && x.Kind == kind && Usable(x) && x.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
                    ?? r.FirstOrDefault(x => x.HardwareKind == kindName && x.Kind == kind && Usable(x) && x.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
                if (hit is not null)
                {
                    return hit.Value;
                }
            }

            return null;
        }

        const string cpu = "Processador", gpu = "Placa de vídeo";
        var cpuClocks = r.Where(x => x.HardwareKind == cpu && x.Kind == SensorKind.Clock && x.Name.StartsWith("Core", StringComparison.OrdinalIgnoreCase) && x.Value > 0).Select(x => x.Value).ToList();

        // Com GPU integrada e dedicada, a dedicada (a que tem mais VRAM) é a que importa.
        var gpuName = r.Where(x => x.HardwareKind == gpu)
            .GroupBy(x => x.Hardware)
            .OrderByDescending(g => g.Where(x => x.Kind == SensorKind.Data && x.Name.Contains("Total", StringComparison.OrdinalIgnoreCase)).Select(x => x.Value).DefaultIfEmpty(0).Max())
            .ThenByDescending(g => g.Key.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || g.Key.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase))
            .Select(g => g.Key)
            .FirstOrDefault() ?? "";
        var gpuReadings = r.Where(x => x.Hardware == gpuName).ToList();
        float? G(SensorKind k, params string[] names) => Find2(gpuReadings, k, names);

        return new SensorSummary(
            Find(cpu, SensorKind.Temperature, "Core (Tctl/Tdie)", "CPU Package", "Core Average", "Tctl", "Package"),
            Find(cpu, SensorKind.Load, "CPU Total"),
            cpuClocks.Count > 0 ? cpuClocks.Average() : null,
            Find(cpu, SensorKind.Power, "Package", "CPU Package"),
            r.FirstOrDefault(x => x.HardwareKind == cpu)?.Hardware ?? "",
            G(SensorKind.Temperature, "GPU Core"),
            G(SensorKind.Temperature, "GPU Hot Spot", "Hot Spot"),
            G(SensorKind.Load, "GPU Core"),
            G(SensorKind.Clock, "GPU Core"),
            G(SensorKind.Clock, "GPU Memory"),
            G(SensorKind.Power, "GPU Package", "GPU Power", "GPU Core"),
            G(SensorKind.Data, "GPU Memory Used", "D3D Dedicated Memory Used"),
            G(SensorKind.Data, "GPU Memory Total"),
            gpuName,
            Find("Memória", SensorKind.Load, "Memory"));
    }

    /// <summary>Sem o driver, o LHM devolve 0 °C / 0 W / 0 MHz: isso é "sem leitura", não zero.</summary>
    private static bool Usable(SensorReading x) => x.Kind is SensorKind.Load or SensorKind.Data || x.Value > 0;

    private static float? Find2(List<SensorReading> r, SensorKind kind, string[] names)
    {
        foreach (var n in names)
        {
            var hit = r.FirstOrDefault(x => x.Kind == kind && Usable(x) && x.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
                ?? r.FirstOrDefault(x => x.Kind == kind && Usable(x) && x.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
            {
                return hit.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Instala o PawnIO em silêncio pelo instalador oficial que vem junto com o Vapt
    /// (PawnIO_setup.exe -install -silent). Pede administrador (UAC) se o Vapt não estiver elevado.
    /// Sem o instalador ao lado do exe, tenta pelo winget.
    /// </summary>
    public static async Task<bool> InstallPawnIoAsync()
    {
        var setup = Path.Combine(AppContext.BaseDirectory, "PawnIO_setup.exe");
        if (File.Exists(setup))
        {
            try
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(setup, "-install -silent")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                });
                if (p is null)
                {
                    return PawnIoInstalled;
                }

                await p.WaitForExitAsync();
                Log.Info($"PawnIO: instalador código {p.ExitCode}");
                return p.ExitCode is 0 or 3010 || PawnIoInstalled; // 3010 = instalado, reiniciar depois
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Log.Info($"PawnIO: instalação cancelada ({ex.Message})"); // UAC recusado
                return false;
            }
        }

        if (Apps.WingetUpdates.WingetPath is not { } winget)
        {
            return false;
        }

        var r = await ProcessRunner.RunAsync(winget, "install --id namazso.PawnIO --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity");
        Log.Info($"PawnIO: winget código {r.ExitCode}");
        return r.ExitCode is 0 or -1978335189 || PawnIoInstalled;
    }
}
