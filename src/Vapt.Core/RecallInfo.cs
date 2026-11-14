namespace Vapt.Core;

/// <summary>
/// Estado do Windows Recall (só existe em PCs Copilot+). As capturas ficam em
/// %LOCALAPPDATA%\CoreAIPlatform.00\UKP, num banco protegido por criptografia.
/// </summary>
public sealed record RecallInfo(bool Present, bool HasSnapshots, long SnapshotBytes)
{
    public static string SnapshotFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoreAIPlatform.00");

    public static RecallInfo Probe()
    {
        var folder = SnapshotFolder;
        var present = Directory.Exists(folder)
            || File.Exists(Path.Combine(Environment.SystemDirectory, "AIXHost.exe"));
        if (!Directory.Exists(folder))
        {
            return new RecallInfo(present, false, 0);
        }

        long bytes = 0;
        try
        {
            foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                bytes += f.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return new RecallInfo(present, bytes > 0, bytes);
    }
}
