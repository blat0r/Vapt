namespace Vapt.Core;

/// <summary>Log em arquivo simples em %LOCALAPPDATA%\Vapt\logs.</summary>
public static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly Lock Gate = new();
    private static string? _file;

    public static void Initialize(string name)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            var file = Path.Combine(AppPaths.LogDirectory, $"{name}.log");
            if (File.Exists(file) && new FileInfo(file).Length > MaxBytes)
            {
                File.Move(file, Path.Combine(AppPaths.LogDirectory, $"{name}.old.log"), overwrite: true);
            }

            _file = file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _file = null;
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERRO", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        if (_file is null)
        {
            return;
        }

        lock (Gate)
        {
            try
            {
                File.AppendAllText(_file, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Log nunca deve derrubar o app.
            }
        }
    }
}
