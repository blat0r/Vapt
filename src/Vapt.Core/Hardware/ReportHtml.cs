using System.Net;
using System.Text;

namespace Vapt.Core.Hardware;

/// <summary>Relatório do PC em HTML (um arquivo só, abre em qualquer navegador, bom para mandar a um técnico).</summary>
public static class ReportHtml
{
    public static string Build(SystemReport r)
    {
        var sb = new StringBuilder();
        string E(string s) => WebUtility.HtmlEncode(s);
        void Section(string title, IEnumerable<InfoItem> items)
        {
            sb.Append($"<section><h2>{E(title)}</h2><table>");
            foreach (var i in items)
            {
                sb.Append($"<tr><th>{E(i.Label)}</th><td>{E(i.Value)}</td></tr>");
            }

            sb.Append("</table></section>");
        }

        sb.Append("""
            <!doctype html><html lang="pt-BR"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Relatório do PC · Vapt</title>
            <style>
            :root{color-scheme:light dark;--bg:#f7f7f8;--card:#fff;--line:#e3e3e6;--muted:#666;--ok:#107c10;--warn:#9d5d00;--bad:#c42b1c}
            @media (prefers-color-scheme:dark){:root{--bg:#1c1c1e;--card:#262628;--line:#3a3a3d;--muted:#aaa;--ok:#6ccb5f;--warn:#fce100;--bad:#ff99a4}}
            body{margin:0;background:var(--bg);font:15px/1.5 "Segoe UI Variable Text","Segoe UI",sans-serif;color:CanvasText}
            main{max-width:900px;margin:0 auto;padding:32px 16px}
            h1{font-size:28px;margin:0}h2{font-size:15px;margin:0 0 8px}
            .sub{color:var(--muted);margin:4px 0 24px}
            section{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:16px;margin-bottom:12px}
            table{width:100%;border-collapse:collapse}th,td{text-align:left;padding:5px 0;vertical-align:top;border-top:1px solid var(--line)}
            tr:first-child th,tr:first-child td{border-top:0}th{width:34%;font-weight:600;color:var(--muted)}
            .Good{color:var(--ok)}.Warning{color:var(--warn)}.Critical{color:var(--bad)}.Unknown{color:var(--muted)}
            footer{color:var(--muted);font-size:13px;margin-top:24px}
            </style></head><body><main>
            """);
        sb.Append($"<h1>Relatório do PC</h1><p class=\"sub\">Gerado pelo Vapt em {r.CreatedAt:dd/MM/yyyy HH:mm}</p>");
        Section("Sistema", r.System);
        Section("Placa-mãe e firmware", r.Board);
        Section("Processador", r.Cpu);
        Section("Memória RAM", r.RamSummary.Concat(r.RamModules.Select(m => new InfoItem(m.Slot,
            $"{Bytes.Format(m.Bytes)} {m.Type} {m.FormFactor} · {m.ConfiguredMts} MT/s · {m.Manufacturer} {m.PartNumber}".Trim()))));
        Section("Placa de vídeo", r.Gpus.SelectMany(g => new[]
        {
            new InfoItem(g.Name, $"{Bytes.Format(g.VramBytes)} de memória"),
            new InfoItem("Driver", $"{g.Driver} ({g.DriverDate})"),
            new InfoItem("Tela", g.Resolution),
        }));

        sb.Append("<section><h2>Saúde dos discos</h2><table>");
        foreach (var d in r.Disks)
        {
            var extra = string.Join(" · ", new[]
            {
                d.Temperature is { } t ? $"{t:0} °C" : null,
                d.WearPercent is { } w ? $"{w:0}% de desgaste" : null,
                d.PowerOnHours is { } h ? $"{h:N0} h ligado" : null,
            }.Where(s => s is not null));
            sb.Append($"<tr><th>{E(d.Name)}<br><small>{E($"{d.Kind} {d.Bus} · {Bytes.Format(d.Size)}")}</small></th><td><b class=\"{d.Verdict}\">{E(Label(d.Verdict))}</b> {E(extra)}<br>{E(string.Join(" ", d.Reasons))}</td></tr>");
        }

        sb.Append("</table></section>");
        if (r.Battery is { } b)
        {
            Section("Bateria", [
                new("Bateria", b.Name),
                new("Capacidade", $"{b.FullMWh:N0} de {b.DesignMWh:N0} mWh ({b.WearPercent:0}% de desgaste)"),
                new("Ciclos", b.Cycles?.ToString("N0") ?? "—"),
                new("Carga", $"{b.ChargePercent}% · {b.State}"),
            ]);
        }

        sb.Append("<footer>Vapt · por blat0r · github.com/blat0r/Vapt</footer></main></body></html>");
        return sb.ToString();
    }

    public static string Label(Health h) => h switch
    {
        Health.Good => "Saudável",
        Health.Warning => "Atenção",
        Health.Critical => "Crítico",
        _ => "Sem dados",
    };
}
