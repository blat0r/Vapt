using System.Runtime.InteropServices;

namespace Vapt.Core.Perf;

/// <summary>Como a RAM está dividida agora. Standby = cache de arquivos que o Windows guarda na RAM sobrando.</summary>
public sealed record MemoryLists(long TotalBytes, long FreeBytes, long StandbyBytes, long ModifiedBytes)
{
    public double FreePercent => TotalBytes == 0 ? 0 : FreeBytes * 100.0 / TotalBytes;
}

public sealed record PurgeResult(bool Ok, long FreedBytes, string Message);

/// <summary>
/// Limpa só a lista standby da memória (o mesmo que o "Empty Standby List" do RAMMap, da Microsoft).
/// Não mexe na memória dos programas (working set), que é o que os "limpadores de RAM" fazem e
/// deixa tudo mais lento. Só vale a pena em PC com pouca RAM que engasga em jogo: quando a memória
/// realmente livre acaba, o Windows às vezes demora a reaproveitar o cache e o jogo dá travadinhas.
/// Fora desse caso o cache é bom (deixa os programas abrirem mais rápido) e o Windows já o libera sozinho.
/// Precisa de administrador.
/// </summary>
public static class StandbyCache
{
    private const int SystemMemoryListInformation = 80;
    private const int MemoryPurgeStandbyList = 4;

    /// <summary>Lê as listas de memória. Null sem administrador (o Windows só deixa ler com privilégio).</summary>
    public static MemoryLists? Query()
    {
        if (!EnablePrivilege())
        {
            return null;
        }

        var info = new SystemMemoryListInfo();
        var status = NtQuerySystemInformation(SystemMemoryListInformation, ref info, Marshal.SizeOf<SystemMemoryListInfo>(), out _);
        if (status != 0)
        {
            Log.Warn($"Memória: NtQuerySystemInformation 0x{status:X8}");
            return null;
        }

        var page = (long)Environment.SystemPageSize;
        long standby = 0;
        for (var i = 0; i < 8; i++)
        {
            standby += (long)info.PageCountByPriority[i];
        }

        var mem = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        GlobalMemoryStatusEx(ref mem);
        return new MemoryLists(
            (long)mem.TotalPhys,
            ((long)info.ZeroPageCount + (long)info.FreePageCount) * page,
            standby * page,
            (long)info.ModifiedPageCount * page);
    }

    /// <summary>
    /// Vale a pena limpar? Só quando a memória de verdade livre está acabando (menos de 10% ou de 1 GB)
    /// e há um cache grande para devolver. Com RAM sobrando, limpar só faz o PC ler de novo do disco.
    /// </summary>
    public static bool WorthIt(MemoryLists m) =>
        (m.FreePercent < 10 || m.FreeBytes < 1L << 30) && m.StandbyBytes > 1L << 30;

    /// <summary>Esvazia a lista standby e diz quanto ficou livre.</summary>
    public static PurgeResult Purge()
    {
        if (!EnablePrivilege())
        {
            return new PurgeResult(false, 0, "Precisa abrir o Vapt como administrador.");
        }

        var before = Query();
        var command = MemoryPurgeStandbyList;
        var status = NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
        if (status != 0)
        {
            Log.Warn($"Memória: limpar standby 0x{status:X8}");
            return new PurgeResult(false, 0, $"O Windows recusou (código 0x{status:X8}).");
        }

        var after = Query();
        var freed = before is not null && after is not null ? Math.Max(0, after.FreeBytes - before.FreeBytes) : 0;
        Log.Info($"Memória: cache standby limpo, {Bytes.Format(freed)} livres a mais.");
        return new PurgeResult(true, freed, $"{Bytes.Format(freed)} de cache devolvidos como memória livre.");
    }

    private static bool EnablePrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008 /* ADJUST_PRIVILEGES | QUERY */, out var token))
        {
            return false;
        }

        try
        {
            if (!LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out var luid))
            {
                return false;
            }

            var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 0x2 /* SE_PRIVILEGE_ENABLED */ };
            // AdjustTokenPrivileges devolve true mesmo sem o privilégio; o erro 1300 diz que não foi.
            return AdjustTokenPrivileges(token, false, ref tp, 0, 0, 0) && Marshal.GetLastWin32Error() == 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemMemoryListInfo
    {
        public nuint ZeroPageCount;
        public nuint FreePageCount;
        public nuint ModifiedPageCount;
        public nuint ModifiedNoWritePageCount;
        public nuint BadPageCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public nuint[] PageCountByPriority;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public nuint[] RepurposedPagesByPriority;
        public nuint ModifiedPageCountPageFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public uint Count;
        public long Luid;
        public uint Attributes;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, ref SystemMemoryListInfo info, int length, out int returned);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(nint token, bool disableAll, ref TokenPrivileges state, int bufferLength, nint previous, nint returnLength);
}
