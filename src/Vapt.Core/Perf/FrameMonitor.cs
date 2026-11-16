using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace Vapt.Core.Perf;

public sealed record FrameStats(int ProcessId, string ProcessName, double Fps, double FrameTimeMs, double OnePercentLow, IReadOnlyList<double> RecentFrameTimes);

/// <summary>
/// FPS de qualquer jogo pelo ETW (eventos Present do DXGI e do Direct3D 9, como o PresentMon),
/// sem injetar nada no jogo. Mede o processo da janela em primeiro plano. Precisa de administrador
/// (sessões de rastreamento em tempo real do Windows).
/// </summary>
public static class FrameMonitor
{
    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private const string SessionName = "Vapt-FPS";

    private static readonly Lock Gate = new();
    private static readonly Dictionary<int, Queue<double>> Presents = [];
    private static TraceEventSession? _session;
    private static int _users;

    public static bool Running => _session is not null;

    public static string? Error { get; private set; }

    public static void Acquire()
    {
        lock (Gate)
        {
            _users++;
            if (_session is not null)
            {
                return;
            }

            if (!Elevation.IsAdministrator)
            {
                Error = "O FPS precisa do Vapt aberto como administrador.";
                return;
            }

            try
            {
                var session = new TraceEventSession(SessionName) { StopOnDispose = true };
                session.EnableProvider(Dxgi, TraceEventLevel.Informational, ulong.MaxValue);
                session.EnableProvider(D3D9, TraceEventLevel.Informational, ulong.MaxValue);
                session.Source.AllEvents += OnEvent;
                _session = session;
                Error = null;
                new Thread(() =>
                {
                    try
                    {
                        session.Source.Process();
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("ETW de FPS parou: " + ex.Message);
                    }
                }) { IsBackground = true, Name = "Vapt.FPS" }.Start();
                Log.Info("Medição de FPS (ETW) ligada.");
            }
            catch (Exception ex)
            {
                Error = "Não deu para ligar a medição de FPS: " + ex.Message;
                Log.Error("ETW de FPS.", ex);
            }
        }
    }

    public static void Release()
    {
        lock (Gate)
        {
            _users = Math.Max(0, _users - 1);
            if (_users > 0 || _session is null)
            {
                return;
            }

            _session.Dispose();
            _session = null;
            Presents.Clear();
            Log.Info("Medição de FPS desligada.");
        }
    }

    private static void OnEvent(TraceEvent e)
    {
        // DXGI Present_Start = 42; D3D9 Present_Start = 1.
        var isPresent = (e.ProviderGuid == Dxgi && (int)e.ID == 42) || (e.ProviderGuid == D3D9 && (int)e.ID == 1);
        if (!isPresent)
        {
            return;
        }

        lock (Gate)
        {
            if (!Presents.TryGetValue(e.ProcessID, out var q))
            {
                Presents[e.ProcessID] = q = new Queue<double>(1024);
            }

            q.Enqueue(e.TimeStampRelativeMSec);
            // Guarda ~10 s de quadros (para o 1% low).
            while (q.Count > 0 && e.TimeStampRelativeMSec - q.Peek() > 10_000)
            {
                q.Dequeue();
            }
        }
    }

    /// <summary>Números do jogo em primeiro plano (ou do processo que mais desenha).</summary>
    public static FrameStats? Current()
    {
        var foreground = ForegroundProcessId();
        lock (Gate)
        {
            if (Presents.Count == 0)
            {
                return null;
            }

            var pid = Presents.ContainsKey(foreground) && Presents[foreground].Count > 5
                ? foreground
                : Presents.OrderByDescending(kv => kv.Value.Count).First().Key;
            var times = Presents[pid].ToArray();
            if (times.Length < 3)
            {
                return null;
            }

            var last = times[^1];
            var lastSecond = times.Count(t => last - t <= 1000);
            var deltas = new double[times.Length - 1];
            for (var i = 1; i < times.Length; i++)
            {
                deltas[i - 1] = times[i] - times[i - 1];
            }

            var sorted = deltas.OrderByDescending(d => d).ToArray();
            var worst = sorted.Take(Math.Max(1, sorted.Length / 100)).Average(); // 1% piores quadros
            var name = "";
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                name = p.ProcessName;
            }
            catch (ArgumentException)
            {
            }

            return new FrameStats(pid, name, lastSecond, deltas[^1], worst > 0 ? 1000 / worst : 0, deltas.TakeLast(120).ToList());
        }
    }

    private static int ForegroundProcessId()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        return (int)pid;
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
}
