namespace Vapt.Core;

/// <summary>Pastas do Vapt em %LOCALAPPDATA%\Vapt.</summary>
public static class AppPaths
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vapt");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Valores originais guardados antes de aplicar uma otimização (para desfazer).</summary>
    public static string BackupFile => Path.Combine(DataDirectory, "backup.json");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
}
