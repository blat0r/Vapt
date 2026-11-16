using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Vapt.Core.Hardware;

namespace Vapt.Core.Net;

public sealed record PingStats(string Name, string Address, int Sent, int Lost, double Avg, double Min, double Max, double Jitter)
{
    public double LossPercent => Sent == 0 ? 0 : Lost * 100.0 / Sent;

    public bool Answered => Sent > Lost;
}

public sealed record NetworkResult(
    Health Verdict,
    string Title,
    IReadOnlyList<string> Reasons,
    PingStats? Router,
    IReadOnlyList<PingStats> Internet,
    double? DnsMs,
    double? DownloadMbps,
    double? UploadMbps,
    double? LoadedPingMs,
    string Adapter);

/// <summary>
/// Diagnóstico de lag: ping, jitter e perda até o roteador e
/// até a internet, tempo de DNS, velocidade pelo speed.cloudflare.com e ping com a linha cheia
/// (bufferbloat). O veredito diz se o problema está no Wi-Fi/roteador ou no provedor.
/// </summary>
public static class NetworkTest
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<NetworkResult> RunAsync(IProgress<(string Stage, double Fraction)>? progress, CancellationToken ct)
    {
        var (gateway, adapter) = Gateway();
        if (gateway is null)
        {
            return new NetworkResult(Health.Critical, "Sem conexão", ["Nenhuma placa de rede conectada a um roteador."], null, [], null, null, null, null, "");
        }

        // 1) Ping em paralelo: roteador e dois DNS públicos (30 pacotes cada).
        progress?.Report(("Medindo o ping…", 0.05));
        var targets = new[] { ("Roteador", gateway), ("Cloudflare", IPAddress.Parse("1.1.1.1")), ("Google", IPAddress.Parse("8.8.8.8")) };
        var pings = await Task.WhenAll(targets.Select(t => PingAsync(t.Item1, t.Item2, 30, 150, ct)));
        var router = pings[0];
        var internet = pings.Skip(1).ToList();

        // 2) DNS.
        progress?.Report(("Testando o DNS…", 0.35));
        // Nome aleatório: não está em cache, então mede a ida até o servidor DNS de verdade.
        // A resposta é "não existe" (exceção), mas o tempo até ela é o que importa.
        double? dns;
        var dnsWatch = Stopwatch.StartNew();
        try
        {
            await Dns.GetHostAddressesAsync($"vapt-{Guid.NewGuid():N}.example.com", ct);
        }
        catch (System.Net.Sockets.SocketException)
        {
        }

        dns = dnsWatch.Elapsed.TotalMilliseconds;

        // 3) Download, com ping ao mesmo tempo (bufferbloat).
        progress?.Report(("Medindo o download…", 0.45));
        using var loadStop = new CancellationTokenSource();
        var loaded = PingAsync("Sob carga", IPAddress.Parse("1.1.1.1"), 1000, 200, loadStop.Token);
        var down = await SpeedAsync(download: true, TimeSpan.FromSeconds(8), f => progress?.Report(("Medindo o download…", 0.45 + 0.25 * f)), ct);
        loadStop.Cancel();
        var loadedStats = await loaded;

        progress?.Report(("Medindo o upload…", 0.72));
        var up = await SpeedAsync(download: false, TimeSpan.FromSeconds(6), f => progress?.Report(("Medindo o upload…", 0.72 + 0.25 * f)), ct);
        progress?.Report(("Pronto", 1));

        return Judge(router, internet, dns, down, up, loadedStats.Answered ? loadedStats.Avg : null, adapter);
    }

    internal static NetworkResult Judge(PingStats router, List<PingStats> internet, double? dns, double? down, double? up, double? loaded, string adapter)
    {
        var reasons = new List<string>();
        var verdict = Health.Good;
        void Flag(Health h, string why)
        {
            reasons.Add(why);
            verdict = (Health)Math.Max((int)verdict, (int)h);
        }

        var net = internet.Where(p => p.Answered).OrderBy(p => p.Avg).FirstOrDefault();
        if (net is null)
        {
            Flag(Health.Critical, "A internet não respondeu. O roteador funciona, mas não há saída para a internet (fale com o provedor ou reinicie o modem).");
        }

        var lanBad = router.Answered && (router.LossPercent >= 2 || router.Jitter > 15 || router.Avg > 20);
        if (lanBad)
        {
            var wifi = adapter.Contains("Wi", StringComparison.OrdinalIgnoreCase) || adapter.Contains("Wireless", StringComparison.OrdinalIgnoreCase);
            Flag(Health.Warning, $"A rede de casa está instável (roteador: {router.Avg:0} ms, perda {router.LossPercent:0.#}%, jitter {router.Jitter:0} ms)." +
                (wifi ? " Você está no Wi-Fi: chegue mais perto do roteador, use 5 GHz ou passe um cabo." : " Confira o cabo e o roteador."));
        }

        if (net is not null)
        {
            if (net.LossPercent >= 2)
            {
                Flag(lanBad ? Health.Warning : Health.Critical, $"Perda de {net.LossPercent:0.#}% dos pacotes até a internet: causa teleporte e travadas em jogos online." + (lanBad ? "" : " Como a rede de casa está boa, o problema é do provedor."));
            }

            if (net.Jitter > 20)
            {
                Flag(Health.Warning, $"Jitter alto ({net.Jitter:0} ms): o ping oscila, o que dá engasgos em jogos e chamadas.");
            }

            if (net.Avg > 80)
            {
                Flag(Health.Warning, $"Ping alto até a internet ({net.Avg:0} ms).");
            }
        }

        if (loaded is { } l && net is not null && l - net.Avg > 60)
        {
            Flag(Health.Warning, $"Bufferbloat: com a internet em uso o ping sobe para {l:0} ms (+{l - net.Avg:0} ms). Ligue o QoS/SQM no roteador ou limite downloads enquanto joga.");
        }

        if (dns is > 150)
        {
            Flag(Health.Warning, $"DNS lento ({dns:0} ms). Troque para Cloudflare ou Google em Reparar → Servidor DNS.");
        }

        if (down is null)
        {
            reasons.Add("Não deu para medir a velocidade (speed.cloudflare.com bloqueado ou fora do ar).");
        }

        if (reasons.Count == 0)
        {
            reasons.Add("Conexão estável: ping baixo, sem perda e sem oscilação.");
        }

        var title = verdict switch
        {
            Health.Good => "Conexão estável",
            Health.Warning => "Conexão com problemas",
            _ => "Conexão ruim",
        };
        return new NetworkResult(verdict, title, reasons, router, internet, dns, down, up, loaded, adapter);
    }

    private static async Task<PingStats> PingAsync(string name, IPAddress ip, int count, int intervalMs, CancellationToken ct)
    {
        var rtts = new List<double>();
        var sent = 0;
        using var ping = new Ping();
        for (var i = 0; i < count && !ct.IsCancellationRequested; i++)
        {
            sent++;
            try
            {
                var reply = await ping.SendPingAsync(ip, 1500);
                if (reply.Status == IPStatus.Success)
                {
                    rtts.Add(reply.RoundtripTime);
                }
            }
            catch (PingException)
            {
            }

            try
            {
                await Task.Delay(intervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (rtts.Count == 0)
        {
            return new PingStats(name, ip.ToString(), sent, sent, 0, 0, 0, 0);
        }

        var jitter = rtts.Count > 1 ? rtts.Zip(rtts.Skip(1), (a, b) => Math.Abs(b - a)).Average() : 0;
        return new PingStats(name, ip.ToString(), sent, sent - rtts.Count, rtts.Average(), rtts.Min(), rtts.Max(), jitter);
    }

    /// <summary>Mb/s pelo servidor público da Cloudflare, com 4 conexões em paralelo.</summary>
    private static async Task<double?> SpeedAsync(bool download, TimeSpan duration, Action<double> progress, CancellationToken ct)
    {
        long bytes = 0;
        var sw = Stopwatch.StartNew();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(duration);
        var payload = new byte[4 << 20];
        Random.Shared.NextBytes(payload);
        var errors = 0;

        async Task Worker()
        {
            var buffer = new byte[1 << 16];
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    if (download)
                    {
                        using var resp = await Http.GetAsync("https://speed.cloudflare.com/__down?bytes=50000000", HttpCompletionOption.ResponseHeadersRead, stop.Token);
                        await using var s = await resp.Content.ReadAsStreamAsync(stop.Token);
                        int n;
                        while ((n = await s.ReadAsync(buffer, stop.Token)) > 0)
                        {
                            Interlocked.Add(ref bytes, n);
                        }
                    }
                    else
                    {
                        using var resp = await Http.PostAsync("https://speed.cloudflare.com/__up", new ByteArrayContent(payload), stop.Token);
                        if (resp.IsSuccessStatusCode)
                        {
                            Interlocked.Add(ref bytes, payload.Length);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpRequestException)
                {
                    if (Interlocked.Increment(ref errors) > 6)
                    {
                        break;
                    }
                }
            }
        }

        var ticker = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                progress(Math.Min(1, sw.Elapsed / duration));
                await Task.Delay(200);
            }
        });
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Worker()));
        await ticker;
        ct.ThrowIfCancellationRequested();
        var seconds = sw.Elapsed.TotalSeconds;
        return bytes == 0 ? null : bytes * 8 / seconds / 1_000_000;
    }

    private static (IPAddress? Gateway, string Adapter) Gateway()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            var gw = nic.GetIPProperties().GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            if (gw is not null)
            {
                var kind = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Cabo";
                return (gw, $"{kind} · {nic.Name}");
            }
        }

        return (null, "");
    }
}
