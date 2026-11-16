using System.Runtime.InteropServices;
using System.Text;

namespace Vapt.Core.Perf;

public sealed record NvidiaGpu(
    int Index,
    string Name,
    uint TempC,
    uint LoadPercent,
    uint CoreMHz,
    uint MemMHz,
    uint MaxCoreMHz,
    uint MaxMemMHz,
    double PowerW,
    double PowerLimitW,
    double PowerMinW,
    double PowerMaxW,
    double PowerDefaultW,
    uint FanPercent,
    ulong VramUsed,
    ulong VramTotal,
    string Throttle,
    uint PState);

/// <summary>
/// NVIDIA Management Library (nvml.dll, vem com o driver): leitura da GPU e o que dá para ajustar
/// com segurança: limite de energia e travar o clock do núcleo/memória numa faixa. Ajustar exige
/// administrador; tudo volta ao padrão com Reset.
/// </summary>
public static class Nvml
{
    private const string Dll = "nvml.dll";
    private static bool? _ok;

    public static bool Available
    {
        get
        {
            if (_ok is { } ok)
            {
                return ok;
            }

            try
            {
                _ok = nvmlInit_v2() == 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                _ok = false;
            }

            return _ok.Value;
        }
    }

    public static IReadOnlyList<NvidiaGpu> Read()
    {
        if (!Available || nvmlDeviceGetCount_v2(out var count) != 0)
        {
            return [];
        }

        var list = new List<NvidiaGpu>();
        for (var i = 0; i < count; i++)
        {
            if (nvmlDeviceGetHandleByIndex_v2((uint)i, out var d) != 0)
            {
                continue;
            }

            var name = new StringBuilder(96);
            nvmlDeviceGetName(d, name, 96);
            nvmlDeviceGetTemperature(d, 0, out var temp);
            nvmlDeviceGetUtilizationRates(d, out var util);
            nvmlDeviceGetClockInfo(d, 0, out var core);
            nvmlDeviceGetClockInfo(d, 2, out var mem);
            nvmlDeviceGetMaxClockInfo(d, 0, out var maxCore);
            nvmlDeviceGetMaxClockInfo(d, 2, out var maxMem);
            nvmlDeviceGetPowerUsage(d, out var power);
            nvmlDeviceGetPowerManagementLimit(d, out var limit);
            nvmlDeviceGetPowerManagementLimitConstraints(d, out var min, out var max);
            nvmlDeviceGetPowerManagementDefaultLimit(d, out var def);
            nvmlDeviceGetFanSpeed(d, out var fan);
            nvmlDeviceGetMemoryInfo(d, out var memInfo);
            ulong reasons;
            try
            {
                nvmlDeviceGetCurrentClocksEventReasons(d, out reasons);
            }
            catch (EntryPointNotFoundException)
            {
                nvmlDeviceGetCurrentClocksThrottleReasons(d, out reasons); // drivers anteriores ao 535
            }

            nvmlDeviceGetPerformanceState(d, out var pstate);
            list.Add(new NvidiaGpu(i, name.ToString(), temp, util.Gpu, core, mem, maxCore, maxMem,
                power / 1000.0, limit / 1000.0, min / 1000.0, max / 1000.0, def / 1000.0, fan,
                memInfo.Used, memInfo.Total, Throttle(reasons), pstate));
        }

        return list;
    }

    /// <summary>O que está segurando o clock agora (o "PerfCap" do GPU-Z).</summary>
    private static string Throttle(ulong r)
    {
        if (r == 0 || r == 1)
        {
            return r == 1 ? "Ociosa" : "Nenhum";
        }

        var parts = new List<string>();
        if ((r & 0x4) != 0) parts.Add("travada pelo app");
        if ((r & 0x8) != 0 || (r & 0x80) != 0) parts.Add("limite de energia");
        if ((r & 0x20) != 0 || (r & 0x40) != 0) parts.Add("temperatura");
        if ((r & 0x100) != 0) parts.Add("sincronização de clock");
        if ((r & 0x2) != 0) parts.Add("configuração do app");
        return parts.Count == 0 ? "Outro" : string.Join(", ", parts);
    }

    private static nint Handle(int index)
    {
        if (!Available || nvmlDeviceGetHandleByIndex_v2((uint)index, out var d) != 0)
        {
            throw new InvalidOperationException("Placa NVIDIA não encontrada.");
        }

        return d;
    }

    private static void Check(int code, string what)
    {
        if (code == 0)
        {
            return;
        }

        var message = code switch
        {
            3 => "esta placa não permite (não suportado)",
            4 => "precisa abrir o Vapt como administrador",
            2 => "valor fora da faixa permitida",
            _ => $"erro {code} do driver",
        };
        throw new InvalidOperationException($"Não deu para {what}: {message}.");
    }

    /// <summary>Limite de energia em watts (dentro do mínimo e máximo da placa).</summary>
    public static void SetPowerLimit(int index, double watts)
    {
        Check(nvmlDeviceSetPowerManagementLimit(Handle(index), (uint)Math.Round(watts * 1000)), "mudar o limite de energia");
        Log.Info($"NVML: limite de energia → {watts:0} W");
    }

    /// <summary>Trava o clock do núcleo entre min e max MHz (subvolt/estabilidade).</summary>
    public static void LockCoreClock(int index, uint minMHz, uint maxMHz)
    {
        Check(nvmlDeviceSetGpuLockedClocks(Handle(index), minMHz, maxMHz), "travar o clock");
        Log.Info($"NVML: clock travado em {minMHz}-{maxMHz} MHz");
    }

    /// <summary>Tudo de volta ao padrão de fábrica (energia e clocks).</summary>
    public static void Reset(int index)
    {
        var d = Handle(index);
        nvmlDeviceResetGpuLockedClocks(d);
        nvmlDeviceResetMemoryLockedClocks(d);
        if (nvmlDeviceGetPowerManagementDefaultLimit(d, out var def) == 0)
        {
            Check(nvmlDeviceSetPowerManagementLimit(d, def), "restaurar o limite de energia");
        }

        Log.Info("NVML: GPU restaurada ao padrão.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [DllImport(Dll)] private static extern int nvmlInit_v2();
    [DllImport(Dll)] private static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport(Dll)] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out nint device);
    [DllImport(Dll, CharSet = CharSet.Ansi)] private static extern int nvmlDeviceGetName(nint device, StringBuilder name, uint length);
    [DllImport(Dll)] private static extern int nvmlDeviceGetTemperature(nint device, int sensor, out uint temp);
    [DllImport(Dll)] private static extern int nvmlDeviceGetUtilizationRates(nint device, out Utilization util);
    [DllImport(Dll)] private static extern int nvmlDeviceGetClockInfo(nint device, int type, out uint mhz);
    [DllImport(Dll)] private static extern int nvmlDeviceGetMaxClockInfo(nint device, int type, out uint mhz);
    [DllImport(Dll)] private static extern int nvmlDeviceGetPowerUsage(nint device, out uint milliwatts);
    [DllImport(Dll)] private static extern int nvmlDeviceGetPowerManagementLimit(nint device, out uint milliwatts);
    [DllImport(Dll)] private static extern int nvmlDeviceGetPowerManagementLimitConstraints(nint device, out uint min, out uint max);
    [DllImport(Dll)] private static extern int nvmlDeviceGetPowerManagementDefaultLimit(nint device, out uint milliwatts);
    [DllImport(Dll)] private static extern int nvmlDeviceGetFanSpeed(nint device, out uint percent);
    [DllImport(Dll)] private static extern int nvmlDeviceGetMemoryInfo(nint device, out MemoryInfo info);
    [DllImport(Dll)] private static extern int nvmlDeviceGetCurrentClocksEventReasons(nint device, out ulong reasons);
    [DllImport(Dll)] private static extern int nvmlDeviceGetCurrentClocksThrottleReasons(nint device, out ulong reasons);
    [DllImport(Dll)] private static extern int nvmlDeviceGetPerformanceState(nint device, out uint pstate);
    [DllImport(Dll)] private static extern int nvmlDeviceSetPowerManagementLimit(nint device, uint milliwatts);
    [DllImport(Dll)] private static extern int nvmlDeviceSetGpuLockedClocks(nint device, uint min, uint max);
    [DllImport(Dll)] private static extern int nvmlDeviceResetGpuLockedClocks(nint device);
    [DllImport(Dll)] private static extern int nvmlDeviceResetMemoryLockedClocks(nint device);
}
