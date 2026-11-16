using System.Text.Json;

namespace Vapt.Core.Perf;

public sealed record HardwareError(DateTime When, string Source, int Id, string Message)
{
    /// <summary>Explicação curta em português.</summary>
    public string Meaning => Source switch
    {
        "Microsoft-Windows-WHEA-Logger" when Id is 18 or 1 => "Erro grave do processador ou da memória (overclock/undervolt instável ou RAM com XMP alto demais).",
        "Microsoft-Windows-WHEA-Logger" when Id is 19 or 47 => "Erro corrigido pelo hardware: sinal de que o overclock está no limite.",
        "Microsoft-Windows-WHEA-Logger" => "Erro de hardware registrado pelo Windows.",
        "nvlddmkm" or "Display" or "amdkmdag" or "amduw23g" => "O driver de vídeo travou e foi reiniciado (overclock da GPU instável ou driver com problema).",
        _ => "Erro de hardware.",
    };
}

/// <summary>
/// Erros de hardware no log do Windows: WHEA (processador, memória, PCIe) e reinício do driver de
/// vídeo (TDR). É o que diz se um overclock é estável de verdade, além de "não travou".
/// </summary>
public static class HardwareErrors
{
    public static async Task<IReadOnlyList<HardwareError>> SinceAsync(DateTime since)
    {
        var start = since.ToString("o");
        var script = $$"""
            $f = @{ LogName = 'System'; StartTime = [datetime]'{{start}}'; ProviderName = @('Microsoft-Windows-WHEA-Logger', 'nvlddmkm', 'Display', 'amdkmdag', 'amduw23g') }
            $e = @(Get-WinEvent -FilterHashtable $f -ErrorAction SilentlyContinue | Select-Object -First 50 | ForEach-Object {
              [pscustomobject]@{ When = $_.TimeCreated.ToString('o'); Source = $_.ProviderName; Id = $_.Id; Message = ([string]$_.Message).Split("`n")[0] }
            })
            ConvertTo-Json -InputObject $e -Compress
            """;
        var r = await ProcessRunner.PowerShellAsync(script);
        if (!r.Ok || r.Output.Trim().Length == 0)
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(r.Output.Trim());
            return doc.RootElement.EnumerateArray().Select(e => new HardwareError(
                DateTime.Parse(e.GetProperty("When").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                e.GetProperty("Source").GetString() ?? "",
                e.GetProperty("Id").GetInt32(),
                e.GetProperty("Message").GetString() ?? "")).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
