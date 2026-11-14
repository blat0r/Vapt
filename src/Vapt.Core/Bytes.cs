using System.Globalization;

namespace Vapt.Core;

public static class Bytes
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Tamanho legível em base 1024 ("1,5 GB"), como o Explorador mostra.</summary>
    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            bytes = 0;
        }

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = unit == 0 || value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##";
        return value.ToString(format, PtBr) + " " + Units[unit];
    }
}
