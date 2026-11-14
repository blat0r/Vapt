namespace Vapt.Core.Disk;

public enum FileCategory
{
    Folder,
    Video,
    Image,
    Audio,
    Archive,
    Program,
    Document,
    Code,
    Game,
    System,
    Other,
    Free,
}

/// <summary>Categoria de cada arquivo pela extensão (define a cor no mapa).</summary>
public static class FileCategories
{
    private static readonly Dictionary<string, FileCategory> ByExtension = Build();

    public static FileCategory Of(DiskNode node)
    {
        if (node.Kind == NodeKind.FreeSpace)
        {
            return FileCategory.Free;
        }

        if (node.IsDirectory)
        {
            return FileCategory.Folder;
        }

        return ByExtension.TryGetValue(node.Extension, out var c) ? c : FileCategory.Other;
    }

    public static string Label(FileCategory c) => c switch
    {
        FileCategory.Folder => "Pastas",
        FileCategory.Video => "Vídeos",
        FileCategory.Image => "Imagens",
        FileCategory.Audio => "Áudio",
        FileCategory.Archive => "Compactados e imagens de disco",
        FileCategory.Program => "Programas",
        FileCategory.Document => "Documentos",
        FileCategory.Code => "Código",
        FileCategory.Game => "Jogos e pacotes",
        FileCategory.System => "Sistema",
        FileCategory.Free => "Espaço livre",
        _ => "Outros",
    };

    private static Dictionary<string, FileCategory> Build()
    {
        var map = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(FileCategory c, string list)
        {
            foreach (var e in list.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                map["." + e] = c;
            }
        }

        Add(FileCategory.Video, "mp4 mkv avi mov wmv webm flv m4v mpg mpeg ts m2ts 3gp vob");
        Add(FileCategory.Image, "jpg jpeg png gif bmp webp tif tiff heic heif avif raw cr2 nef arw dng psd svg ico");
        Add(FileCategory.Audio, "mp3 wav flac aac ogg opus m4a wma aiff mid");
        Add(FileCategory.Archive, "zip rar 7z tar gz bz2 xz zst iso img vhd vhdx vmdk wim esd cab msi msix appx");
        Add(FileCategory.Program, "exe dll sys ocx drv cpl msc lib so");
        Add(FileCategory.Document, "pdf doc docx xls xlsx ppt pptx odt ods odp txt rtf md csv epub");
        Add(FileCategory.Code, "cs js ts tsx jsx py java c cpp h hpp rs go json xml yaml yml html css lua ps1 bat cmd sh sln csproj pdb obj");
        Add(FileCategory.Game, "pak upk uasset ucas utoc forge bundle assets resource bsa ba2 vpk gcf rpf arc dat bin");
        Add(FileCategory.System, "log etl evtx dmp tmp temp bak old cache db sqlite edb mui cat manifest regtrans-ms blf");
        return map;
    }
}
