using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Vapt.Core.Perf;

public enum GpuMode
{
    /// <summary>Cena fixa por um tempo fixo: a nota é o FPS médio × 10.</summary>
    Benchmark,
    /// <summary>Mesma cena, mas várias passadas por quadro e mais passos: carga máxima, como o FurMark.</summary>
    Stress,
}

public sealed record GpuRunResult(GpuMode Mode, int Width, int Height, double AvgFps, double MinFps, long Frames, double Seconds, string Adapter, bool Completed)
{
    public double Score => AvgFps * 10;
}

/// <summary>
/// Benchmark 3D e stress de GPU em Direct3D 11 (Vortice, MIT): uma cena por raymarching
/// (esferas e blocos refletindo, sombra suave, oclusão de ambiente e neblina) calculada inteira
/// na GPU, numa janela própria, sem limite de FPS. ESC ou fechar a janela encerra.
/// </summary>
public static class GpuRenderer
{
    public static Task<GpuRunResult> RunAsync(GpuMode mode, int width, int height, TimeSpan duration, IProgress<(double Fraction, double Fps)>? progress, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<GpuRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(Run(mode, width, height, duration, progress, ct));
            }
            catch (Exception ex)
            {
                Log.Error("Renderizador de GPU.", ex);
                tcs.SetException(ex);
            }
        }) { IsBackground = true, Name = "Vapt.Gpu" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private const string Shader = """
        cbuffer Frame : register(b0) { float Time; float Steps; float2 Res; float Samples; float3 Pad; };

        float4 VS(uint id : SV_VertexID) : SV_Position {
            float2 p = float2((id << 1) & 2, id & 2);
            return float4(p * float2(2, -2) + float2(-1, 1), 0, 1);
        }

        float sdBox(float3 p, float3 b) { float3 q = abs(p) - b; return length(max(q, 0)) + min(max(q.x, max(q.y, q.z)), 0) - 0.05; }

        float2 Map(float3 p) {
            float ground = p.y + 1.0 + 0.08 * sin(p.x * 1.3) * sin(p.z * 1.1);
            float3 q = p; q.xz = fmod(abs(q.xz) + 2.0, 4.0) - 2.0;
            float s = length(q - float3(0, 0.1 + 0.35 * sin(Time + p.x * 0.3), 0)) - 0.8;
            float b = sdBox(q - float3(1.3, -0.4, 1.3), float3(0.35, 0.6 + 0.2 * sin(Time * 0.7 + p.z), 0.35));
            float d = min(s, b);
            return d < ground ? float2(d, s < b ? 1 : 2) : float2(ground, 0);
        }

        float3 Normal(float3 p) {
            float2 e = float2(0.001, 0);
            return normalize(float3(Map(p + e.xyy).x - Map(p - e.xyy).x, Map(p + e.yxy).x - Map(p - e.yxy).x, Map(p + e.yyx).x - Map(p - e.yyx).x));
        }

        float Shadow(float3 ro, float3 rd) {
            float res = 1, t = 0.02;
            [loop] for (int i = 0; i < 80; i++) { float h = Map(ro + rd * t).x; res = min(res, 10 * h / t); t += clamp(h, 0.02, 0.3); if (h < 0.001 || t > 25) break; }
            return saturate(res);
        }

        float Occlusion(float3 p, float3 n) {
            float o = 0, w = 1;
            [unroll] for (int i = 1; i <= 6; i++) { float h = 0.06 * i; o += w * (h - Map(p + n * h).x); w *= 0.75; }
            return saturate(1 - 2.5 * o);
        }

        float4 March(float3 ro, float3 rd, out float3 hitPos, out float3 n, out float mat) {
            float t = 0; mat = -1; hitPos = ro; n = float3(0, 1, 0);
            [loop] for (int i = 0; i < (int)Steps; i++) {
                float2 h = Map(ro + rd * t);
                if (h.x < 0.0005 * t) { mat = h.y; break; }
                t += h.x * 0.9;
                if (t > 60) break;
            }
            hitPos = ro + rd * t;
            if (mat >= 0) n = Normal(hitPos);
            return float4(t, 0, 0, 0);
        }

        float3 Shade(float3 p, float3 n, float3 rd, float mat, float t) {
            float3 sky = lerp(float3(0.55, 0.65, 0.8), float3(0.15, 0.2, 0.35), saturate(rd.y * 2));
            if (mat < 0) return sky;
            float3 light = normalize(float3(0.6, 0.8, -0.4));
            float3 albedo = mat == 0 ? lerp(float3(0.25, 0.27, 0.3), float3(0.4, 0.42, 0.45), step(0.5, frac(floor(p.x) * 0.5 + floor(p.z) * 0.5)))
                          : mat == 1 ? float3(0.85, 0.35, 0.2) : float3(0.25, 0.5, 0.9);
            float dif = saturate(dot(n, light)) * Shadow(p + n * 0.01, light);
            float ao = Occlusion(p, n);
            float spec = pow(saturate(dot(reflect(rd, n), light)), 48);
            float3 col = albedo * (0.15 * ao + 1.1 * dif) + spec * 0.6;
            return lerp(col, sky, 1 - exp(-0.0025 * t * t));
        }

        float3 Render(float2 pix) {
            float2 uv = (pix * 2 - Res) / Res.y; uv.y = -uv.y;
            float a = Time * 0.15;
            float3 ro = float3(6 * sin(a), 1.6 + 0.5 * sin(Time * 0.3), 6 * cos(a) - Time * 1.5);
            float3 ta = ro + float3(-sin(a), -0.25, -cos(a) - 1);
            float3 ww = normalize(ta - ro), uu = normalize(cross(float3(0, 1, 0), ww)), vv = cross(ww, uu);
            float3 rd = normalize(uv.x * uu + uv.y * vv + 1.6 * ww);

            float3 p, n; float mat;
            float t = March(ro, rd, p, n, mat).x;
            float3 col = Shade(p, n, rd, mat, t);
            float k = 0.35;
            [loop] for (int b = 0; b < 2 && mat >= 1; b++) { // até dois reflexos nas esferas e blocos
                float3 rr = reflect(rd, n), p2, n2; float m2;
                float t2 = March(p + n * 0.02, rr, p2, n2, m2).x;
                col = lerp(col, Shade(p2, n2, rr, m2, t2), k);
                p = p2; n = n2; rd = rr; mat = m2; k *= 0.5;
            }
            return col;
        }

        float4 PS(float4 pos : SV_Position) : SV_Target {
            // Antisserrilhado por supersampling: várias amostras por pixel (custo proporcional).
            float3 col = 0;
            int s = (int)Samples;
            [loop] for (int i = 0; i < s; i++) {
                float2 o = float2(frac(i * 0.618034), frac(i * 0.381966 + 0.25)) - 0.5;
                col += Render(pos.xy + o);
            }
            return float4(pow(col / s, 1 / 2.2), 1);
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameData
    {
        public float Time;
        public float Steps;
        public float ResX;
        public float ResY;
        public float Samples;
        public float Pad0;
        public float Pad1;
        public float Pad2;
    }

    private static GpuRunResult Run(GpuMode mode, int width, int height, TimeSpan duration, IProgress<(double Fraction, double Fps)>? progress, CancellationToken ct)
    {
        var title = mode == GpuMode.Benchmark ? "Vapt · Benchmark 3D" : "Vapt · Stress de GPU (ESC para parar)";
        var hwnd = Win32Window.Create(title, width, height);
        try
        {
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, new[] { FeatureLevel.Level_11_0 }, out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
            using var dev = device;
            using var ctx = context;
            using var dxgiDevice = dev.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            var adapterName = adapter.Description.Description;
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
            var desc = new SwapChainDescription1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = Format.B8G8R8A8_UNorm,
                BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = new SampleDescription(1, 0),
                SwapEffect = SwapEffect.FlipDiscard,
                Scaling = Scaling.Stretch,
                AlphaMode = AlphaMode.Ignore,
            };
            using var swap = factory.CreateSwapChainForHwnd(dev, hwnd, desc);
            using var back = swap.GetBuffer<ID3D11Texture2D>(0);
            using var rtv = dev.CreateRenderTargetView(back);
            var vsCode = Compiler.Compile(Shader, "VS", "vapt.hlsl", "vs_5_0");
            var psCode = Compiler.Compile(Shader, "PS", "vapt.hlsl", "ps_5_0");
            using var vs = dev.CreateVertexShader(vsCode.Span);
            using var ps = dev.CreatePixelShader(psCode.Span);
            using var cb = dev.CreateBuffer(new BufferDescription(32, BindFlags.ConstantBuffer, ResourceUsage.Default));

            ctx.OMSetRenderTargets(rtv);
            ctx.RSSetViewport(0, 0, width, height);
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            ctx.VSSetShader(vs);
            ctx.PSSetShader(ps);
            ctx.PSSetConstantBuffer(0, cb);

            // Stress: mais passos e 3 desenhos por quadro (overdraw), como o FurMark.
            var steps = mode == GpuMode.Stress ? 256f : 160f;
            var samples = mode == GpuMode.Stress ? 6f : 4f;
            var passes = mode == GpuMode.Stress ? 2 : 1;
            var sw = Stopwatch.StartNew();
            var lastSecond = 0.0;
            long frames = 0, framesThisSecond = 0;
            var minFps = double.MaxValue;
            var warmup = mode == GpuMode.Benchmark ? 2.0 : 0; // os primeiros segundos não contam (shaders compilando)
            long measuredFrames = 0;
            var measureStart = 0.0;
            var completed = false;

            while (!ct.IsCancellationRequested && Win32Window.Pump(hwnd))
            {
                var now = sw.Elapsed.TotalSeconds;
                if (now >= duration.TotalSeconds + warmup)
                {
                    completed = true;
                    break;
                }

                // A cena do benchmark depende só do tempo: igual em todo PC.
                var data = new FrameData { Time = (float)now, Steps = steps, ResX = width, ResY = height, Samples = samples };
                ctx.UpdateSubresource(in data, cb);
                for (var i = 0; i < passes; i++)
                {
                    ctx.Draw(3, 0);
                }

                swap.Present(0, PresentFlags.None);
                frames++;
                framesThisSecond++;
                if (now >= warmup)
                {
                    if (measuredFrames == 0)
                    {
                        measureStart = now;
                    }

                    measuredFrames++;
                }

                if (now - lastSecond >= 1)
                {
                    var fps = framesThisSecond / (now - lastSecond);
                    if (now > warmup + 1)
                    {
                        minFps = Math.Min(minFps, fps);
                    }

                    Win32Window.SetTitle(hwnd, $"{title} · {fps:0} FPS");
                    progress?.Report((Math.Min(1, now / (duration.TotalSeconds + warmup)), fps));
                    lastSecond = now;
                    framesThisSecond = 0;
                }
            }

            var measured = Math.Max(0.001, sw.Elapsed.TotalSeconds - measureStart);
            var avg = measuredFrames / measured;
            Log.Info($"GPU {mode} {width}×{height}: {avg:0.0} FPS médios em {measured:0.0} s ({adapterName}).");
            return new GpuRunResult(mode, width, height, avg, minFps == double.MaxValue ? avg : minFps, frames, measured, adapterName, completed);
        }
        finally
        {
            Win32Window.Destroy(hwnd);
        }
    }
}

/// <summary>Janela Win32 mínima para o Direct3D desenhar (com laço de mensagens próprio).</summary>
internal static class Win32Window
{
    private const uint WS_OVERLAPPED = 0x00000000, WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000, WS_MINIMIZEBOX = 0x00020000, WS_VISIBLE = 0x10000000;
    private const uint WM_CLOSE = 0x0010, WM_KEYDOWN = 0x0100, WM_DESTROY = 0x0002, PM_REMOVE = 1;
    private static readonly WndProc Proc = WindowProc;
    private static readonly HashSet<nint> Closed = [];
    private static bool _registered;

    private delegate nint WndProc(nint hwnd, uint msg, nint w, nint l);

    public static nint Create(string title, int width, int height)
    {
        var hInstance = GetModuleHandleW(null);
        if (!_registered)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
                hInstance = hInstance,
                hCursor = LoadCursorW(0, 32512),
                lpszClassName = "VaptGpuWindow",
            };
            RegisterClassExW(ref wc);
            _registered = true;
        }

        var style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_VISIBLE;
        var rect = new RECT { Right = width, Bottom = height };
        AdjustWindowRect(ref rect, style, false);
        var hwnd = CreateWindowExW(0, "VaptGpuWindow", title, style, 80, 80, rect.Right - rect.Left, rect.Bottom - rect.Top, 0, 0, hInstance, 0);
        SetForegroundWindow(hwnd);
        return hwnd;
    }

    /// <summary>Processa as mensagens pendentes. Falso quando a janela foi fechada.</summary>
    public static bool Pump(nint hwnd)
    {
        while (PeekMessageW(out var msg, 0, 0, 0, PM_REMOVE))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        lock (Closed)
        {
            return !Closed.Contains(hwnd);
        }
    }

    public static void SetTitle(nint hwnd, string title) => SetWindowTextW(hwnd, title);

    public static void Destroy(nint hwnd)
    {
        DestroyWindow(hwnd);
        lock (Closed)
        {
            Closed.Remove(hwnd);
        }
    }

    private static nint WindowProc(nint hwnd, uint msg, nint w, nint l)
    {
        if (msg == WM_CLOSE || (msg == WM_KEYDOWN && w == 0x1B /* ESC */))
        {
            lock (Closed)
            {
                Closed.Add(hwnd);
            }

            return 0;
        }

        return DefWindowProcW(hwnd, msg, w, l);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int x, y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out MSG msg, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool AdjustWindowRect(ref RECT rect, uint style, bool menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowTextW(nint hwnd, string text);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint LoadCursorW(nint inst, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
}
