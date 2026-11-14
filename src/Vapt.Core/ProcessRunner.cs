using System.Diagnostics;
using System.Text;

namespace Vapt.Core;

public sealed record ProcessResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>Roda programas do sistema (powershell, dism, sc...) sem janela e captura a saída.</summary>
public static class ProcessRunner
{
    public static string System32 => Environment.GetFolderPath(Environment.SpecialFolder.System);

    public static string PowerShellExe => Path.Combine(System32, "WindowsPowerShell", "v1.0", "powershell.exe");

    public static async Task<ProcessResult> RunAsync(
        string file,
        string arguments,
        Action<string>? onLine = null,
        CancellationToken cancellation = default,
        Encoding? encoding = null)
    {
        var psi = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding ?? Encoding.UTF8,
            StandardErrorEncoding = encoding ?? Encoding.UTF8,
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (output)
            {
                output.AppendLine(e.Data);
            }

            onLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (error)
                {
                    error.AppendLine(e.Data);
                }
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellation);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        return new ProcessResult(process.ExitCode, output.ToString(), error.ToString());
    }

    /// <summary>
    /// Roda um script do PowerShell. O script vai em Base64 (-EncodedCommand), então aspas e
    /// caracteres especiais não precisam de escape. A saída sai em UTF-8.
    /// </summary>
    public static Task<ProcessResult> PowerShellAsync(
        string script,
        Action<string>? onLine = null,
        CancellationToken cancellation = default)
    {
        var full = "$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8;$ErrorActionPreference='Stop';" + script;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        return RunAsync(PowerShellExe, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}", onLine, cancellation);
    }

    /// <summary>Aspas simples do PowerShell: só o apóstrofo precisa dobrar.</summary>
    public static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";
}
