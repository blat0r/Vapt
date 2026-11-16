using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Vapt.Core;

/// <summary>Dados do PC para a tela inicial (sem WMI: tudo por API e registro, instantâneo).</summary>
public static class SystemInfo
{
    public static string CpuName
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Processador";
        }
    }

    public static string WindowsVersion
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var build = key?.GetValue("CurrentBuild") as string ?? "";
            var ubr = key?.GetValue("UBR") is int u ? $".{u}" : "";
            var display = key?.GetValue("DisplayVersion") as string ?? "";
            var edition = (key?.GetValue("EditionID") as string ?? "") switch
            {
                "Professional" => "Pro",
                "Core" => "Home",
                var e => e,
            };
            var name = int.TryParse(build, out var b) && b >= 22000 ? "Windows 11" : "Windows 10";
            return $"{name} {edition} {display} (build {build}{ubr})".Replace("  ", " ");
        }
    }

    public static TimeSpan Uptime => TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>Build do Windows (19045 = Windows 10 22H2, 22000+ = Windows 11).</summary>
    public static int Build { get; } = Environment.OSVersion.Version.Build;

    public static bool IsWindows11 => Build >= 22000;

    /// <summary>Notebook/tablet (tem bateria): GetSystemPowerStatus, BatteryFlag 128 = sem bateria.</summary>
    public static bool HasBattery => GetSystemPowerStatus(out var s) && s.BatteryFlag != 128 && s.BatteryFlag != 255;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    public static (long Total, long Available) Memory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? ((long)status.ullTotalPhys, (long)status.ullAvailPhys) : (0, 0);
    }

    public static DriveInfo SystemDrive => new(Path.GetPathRoot(Environment.SystemDirectory)!);

    /// <summary>Uso de CPU entre duas chamadas (GetSystemTimes).</summary>
    public sealed class CpuMeter
    {
        private ulong _idle;
        private ulong _total;

        public CpuMeter() => Sample();

        public double Sample()
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user))
            {
                return 0;
            }

            var total = kernel + user; // kernel já inclui o ocioso
            var dIdle = idle - _idle;
            var dTotal = total - _total;
            _idle = idle;
            _total = total;
            return dTotal == 0 ? 0 : Math.Clamp(1 - dIdle / (double)dTotal, 0, 1);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}
