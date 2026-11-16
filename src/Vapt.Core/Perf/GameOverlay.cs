using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Vapt.Core.Perf;

public enum OverlayCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>O que aparece no overlay (salvo nas configurações).</summary>
public sealed class OverlayOptions
{
    public bool Fps { get; set; } = true;
    public bool FrameTime { get; set; } = true;
    public bool OnePercentLow { get; set; } = true;
    public bool Graph { get; set; } = true;
    public bool Gpu { get; set; } = true;
    public bool GpuClock { get; set; }
    public bool GpuPower { get; set; } = true;
    public bool Vram { get; set; }
    public bool Cpu { get; set; } = true;
    public bool CpuClock { get; set; }
    public bool Ram { get; set; }
    public bool Time { get; set; }
    public OverlayCorner Corner { get; set; } = OverlayCorner.TopLeft;
    public float Scale { get; set; } = 1f;
}

/// <summary>
/// Overlay de jogos (estilo RTSS/Afterburner): janela em camadas, transparente, sempre no topo e
/// que ignora cliques, no monitor da janela em primeiro plano. Ctrl+Shift+O liga e desliga.
/// Não aparece em jogos em tela cheia exclusiva (só em janela e janela sem borda).
/// </summary>
public static class GameOverlay
{
    private const int HotkeyId = 0x5641;
    private static Thread? _thread;
    private static volatile bool _stop;
    private static volatile bool _visible = true;

    public static OverlayOptions Options { get; set; } = new();

    public static bool Running => _thread is { IsAlive: true };

    public static void Start()
    {
        if (Running)
        {
            return;
        }

        _stop = false;
        _visible = true;
        SensorHub.Acquire();
        FrameMonitor.Acquire();
        _thread = new Thread(Loop) { IsBackground = true, Name = "Vapt.Overlay" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public static void Stop()
    {
        if (!Running)
        {
            return;
        }

        _stop = true;
        _thread?.Join(2000);
        _thread = null;
        SensorHub.Release();
        FrameMonitor.Release();
    }

    private static void Loop()
    {
        var hwnd = CreateOverlayWindow();
        RegisterHotKey(hwnd, HotkeyId, 0x0002 | 0x0004 | 0x4000 /* Ctrl+Shift, sem repetir */, 0x4F /* O */);
        try
        {
            var next = DateTime.UtcNow;
            while (!_stop)
            {
                while (PeekMessageW(out var msg, 0, 0, 0, 1))
                {
                    if (msg.message == 0x0312 /* WM_HOTKEY */ && msg.wParam == HotkeyId)
                    {
                        _visible = !_visible;
                        ShowWindow(hwnd, _visible ? 4 /* SW_SHOWNOACTIVATE */ : 0);
                    }

                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }

                if (DateTime.UtcNow >= next)
                {
                    next = DateTime.UtcNow.AddMilliseconds(250);
                    if (_visible)
                    {
                        Render(hwnd);
                    }
                }

                Thread.Sleep(15);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Overlay.", ex);
        }
        finally
        {
            UnregisterHotKey(hwnd, HotkeyId);
            DestroyWindow(hwnd);
        }
    }

    // ── Conteúdo ─────────────────────────────────────────────────────────

    private readonly record struct Line(string Label, string Value, Color Color);

    internal static List<(string Label, string Value)> BuildLines(OverlayOptions o, FrameStats? f, SensorSummary? s, string? fpsError)
    {
        var lines = new List<(string, string)>();
        if (o.Fps || o.FrameTime || o.OnePercentLow)
        {
            if (f is null)
            {
                lines.Add(("FPS", fpsError is null ? "—" : "abra o Vapt como admin"));
            }
            else
            {
                var parts = new List<string>();
                if (o.Fps) parts.Add($"{f.Fps:0}");
                if (o.FrameTime) parts.Add($"{f.FrameTimeMs:0.0} ms");
                if (o.OnePercentLow) parts.Add($"1% {f.OnePercentLow:0}");
                lines.Add(("FPS", string.Join("  ", parts)));
            }
        }

        string T(float? v) => v is { } x ? $"{x:0}°" : "—";
        string P(float? v) => v is { } x ? $"{x:0}%" : "—";
        if (o.Gpu && s is not null)
        {
            var gpu = $"{P(s.GpuLoad)}  {T(s.GpuTemp)}";
            if (o.GpuClock && s.GpuClock is { } gc) gpu += $"  {gc:0} MHz";
            if (o.GpuPower && s.GpuPower is { } gp) gpu += $"  {gp:0} W";
            lines.Add(("GPU", gpu));
        }

        if (o.Vram && s?.VramUsedGb is { } vu)
        {
            lines.Add(("VRAM", s.VramTotalGb is { } vt ? $"{vu:0.0} / {vt:0} GB" : $"{vu:0.0} GB"));
        }

        if (o.Cpu && s is not null)
        {
            var cpu = $"{P(s.CpuLoad)}  {T(s.CpuTemp)}";
            if (o.CpuClock && s.CpuClock is { } cc) cpu += $"  {cc:0} MHz";
            lines.Add(("CPU", cpu));
        }

        if (o.Ram)
        {
            var (total, avail) = SystemInfo.Memory();
            lines.Add(("RAM", total > 0 ? $"{(total - avail) / 1073741824.0:0.0} / {total / 1073741824.0:0} GB" : "—"));
        }

        if (o.Time)
        {
            lines.Add(("Hora", DateTime.Now.ToString("HH:mm")));
        }

        return lines;
    }

    private static void Render(nint hwnd)
    {
        var o = Options;
        var f = FrameMonitor.Current();
        var lines = BuildLines(o, f, SensorHub.Summary, FrameMonitor.Error);
        var scale = Math.Clamp(o.Scale, 0.75f, 2f) * DpiScale();
        using var font = new Font("Segoe UI Semibold", 11f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        using var labelFont = new Font("Segoe UI", 10f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        var pad = (int)(10 * scale);
        var lineH = (int)(18 * scale);
        var labelW = (int)(42 * scale);
        var graphH = o.Graph && f is not null ? (int)(34 * scale) : 0;

        // Largura pela linha mais comprida.
        int width;
        using (var measure = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(measure))
        {
            width = lines.Count == 0 ? 120 : (int)lines.Max(l => g.MeasureString(l.Value, font).Width) + labelW + pad * 2;
        }

        width = Math.Max(width, (int)(150 * scale));
        var height = pad * 2 + lines.Count * lineH + (graphH > 0 ? graphH + (int)(6 * scale) : 0);

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using (var bg = new SolidBrush(Color.FromArgb(200, 12, 12, 16)))
            using (var path = Rounded(new Rectangle(0, 0, width - 1, height - 1), (int)(8 * scale)))
            {
                g.FillPath(bg, path);
            }

            using var label = new SolidBrush(Color.FromArgb(200, 170, 180, 200));
            using var value = new SolidBrush(Color.White);
            var y = pad;
            foreach (var (l, v) in lines)
            {
                g.DrawString(l, labelFont, label, pad, y + 1 * scale);
                g.DrawString(v, font, value, pad + labelW, y);
                y += lineH;
            }

            // Gráfico de frametime: picos = engasgos.
            if (graphH > 0 && f!.RecentFrameTimes.Count > 1)
            {
                var area = new RectangleF(pad, y + 4 * scale, width - pad * 2, graphH);
                var max = Math.Max(f.RecentFrameTimes.Max(), 1000 / 30.0);
                var pts = f.RecentFrameTimes.Select((t, i) => new PointF(
                    area.Left + area.Width * i / (f.RecentFrameTimes.Count - 1),
                    area.Bottom - (float)(t / max) * area.Height)).ToArray();
                using var pen = new Pen(Color.FromArgb(230, 120, 220, 140), 1.5f * scale);
                g.DrawLines(pen, pts);
            }
        }

        // Canto escolhido do monitor da janela em primeiro plano.
        var monitor = MonitorFromWindow(GetForegroundWindow(), 2);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(monitor, ref info);
        var m = (int)(16 * scale);
        var r = info.rcMonitor;
        var x = o.Corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft ? r.Left + m : r.Right - width - m;
        var yPos = o.Corner is OverlayCorner.TopLeft or OverlayCorner.TopRight ? r.Top + m : r.Bottom - height - m;
        Present(hwnd, bmp, x, yPos);
        // Volta para cima (jogos em janela sem borda também se marcam "sempre no topo").
        SetWindowPos(hwnd, -1 /* HWND_TOPMOST */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /* NOSIZE|NOMOVE|NOACTIVATE */);
    }

    private static float DpiScale()
    {
        var hdc = GetDC(0);
        var dpi = GetDeviceCaps(hdc, 88 /* LOGPIXELSX */);
        ReleaseDC(0, hdc);
        return dpi / 96f;
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        var d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Copia o bitmap (com alfa) para a janela em camadas.</summary>
    private static void Present(nint hwnd, Bitmap bmp, int x, int y)
    {
        var screen = GetDC(0);
        var mem = CreateCompatibleDC(screen);
        var hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(mem, hBitmap);
        var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
        var src = new POINT();
        var dst = new POINT { x = x, y = y };
        var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
        UpdateLayeredWindow(hwnd, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
        SelectObject(mem, old);
        DeleteObject(hBitmap);
        DeleteDC(mem);
        ReleaseDC(0, screen);
    }

    // ── Janela ───────────────────────────────────────────────────────────

    private delegate nint WndProc(nint hwnd, uint msg, nint w, nint l);

    private static readonly WndProc Proc = (h, m, w, l) => DefWindowProcW(h, m, w, l);
    private static bool _registered;

    private static nint CreateOverlayWindow()
    {
        var inst = GetModuleHandleW(null);
        if (!_registered)
        {
            var wc = new WNDCLASSEXW { cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc), hInstance = inst, lpszClassName = "VaptOverlay" };
            RegisterClassExW(ref wc);
            _registered = true;
        }

        // Em camadas + transparente a cliques + sempre no topo + fora da barra de tarefas + nunca ativa.
        const uint exStyle = 0x00080000 | 0x00000020 | 0x00000008 | 0x00000080 | 0x08000000;
        var hwnd = CreateWindowExW(exStyle, "VaptOverlay", "Vapt Overlay", 0x80000000 /* WS_POPUP */, 0, 0, 1, 1, 0, 0, inst, 0);
        ShowWindow(hwnd, 4);
        return hwnd;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public nint hwnd; public uint message; public nint wParam; public nint lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(nint hwnd, nint hdcDst, ref POINT dst, ref SIZE size, nint hdcSrc, ref POINT src, uint key, ref BLENDFUNCTION blend, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint hdc);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(nint monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out MSG msg, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(ref MSG msg);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(nint hdc, int index);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
}
