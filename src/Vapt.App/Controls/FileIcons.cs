using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Vapt.Controls;

/// <summary>Ícone de um .exe/atalho pela miniatura do Windows (com cache por caminho).</summary>
internal static class FileIcons
{
    private static readonly Dictionary<string, BitmapImage?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<BitmapImage?> GetAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        path = Environment.ExpandEnvironmentVariables(path);
        if (Cache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        BitmapImage? image = null;
        try
        {
            if (File.Exists(path))
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 32, ThumbnailOptions.UseCurrentScale);
                if (thumb is not null)
                {
                    image = new BitmapImage();
                    await image.SetSourceAsync(thumb);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            image = null;
        }

        Cache[path] = image;
        return image;
    }

    /// <summary>Troca o ícone de fonte por ícone do arquivo quando ele carregar.</summary>
    public static async void Apply(CommunityToolkit.WinUI.Controls.SettingsCard card, string? path)
    {
        if (await GetAsync(path) is { } image)
        {
            card.HeaderIcon = new ImageIcon { Source = image };
        }
    }
}
