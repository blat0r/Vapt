namespace Vapt.Core.Disk;

public enum NodeKind : byte
{
    Directory,
    File,
    /// <summary>Espaço livre do volume (só na raiz, opcional).</summary>
    FreeSpace,
}

/// <summary>
/// Nó da árvore de espaço. Escrito por uma thread de varredura por pasta e lido pela interface
/// ao mesmo tempo: os filhos são publicados de uma vez (array novo) e o tamanho só muda
/// via Interlocked, então a interface sempre vê um estado coerente, só incompleto.
/// </summary>
public sealed class DiskNode
{
    private long _size;
    private long _files;
    private DiskNode[] _children = [];

    public DiskNode(string name, NodeKind kind, DiskNode? parent, long size = 0)
    {
        Name = name;
        Kind = kind;
        Parent = parent;
        _size = size;
        _files = kind == NodeKind.File ? 1 : 0;
    }

    public string Name { get; }

    public NodeKind Kind { get; }

    public DiskNode? Parent { get; }

    public DateTime Modified { get; init; }

    public bool IsDirectory => Kind == NodeKind.Directory;

    /// <summary>Tamanho total (para pastas, a soma de tudo encontrado até agora).</summary>
    public long Size => Volatile.Read(ref _size);

    /// <summary>Quantidade de arquivos dentro (recursivo).</summary>
    public long FileCount => Volatile.Read(ref _files);

    public DiskNode[] Children => Volatile.Read(ref _children);

    /// <summary>A pasta já foi listada (as subpastas podem ainda estar sendo lidas).</summary>
    public bool Listed { get; private set; }

    /// <summary>Não foi possível abrir (sem permissão etc.).</summary>
    public bool Denied { get; internal set; }

    internal void Publish(DiskNode[] children)
    {
        Volatile.Write(ref _children, children);
        Listed = true;
    }

    /// <summary>Soma tamanho e contagem neste nó e em todos acima.</summary>
    internal void AddUp(long bytes, long files)
    {
        for (var n = this; n is not null; n = n.Parent)
        {
            Interlocked.Add(ref n._size, bytes);
            Interlocked.Add(ref n._files, files);
        }
    }

    /// <summary>Tira um nó da árvore (depois de apagá-lo do disco).</summary>
    public void Remove(DiskNode child)
    {
        var current = Children;
        var next = current.Where(c => !ReferenceEquals(c, child)).ToArray();
        if (next.Length == current.Length)
        {
            return;
        }

        Volatile.Write(ref _children, next);
        AddUp(-child.Size, -child.FileCount);
    }

    public string FullPath
    {
        get
        {
            if (Parent is null)
            {
                return Name;
            }

            var parts = new Stack<string>();
            for (var n = this; n is not null; n = n.Parent)
            {
                parts.Push(n.Name);
            }

            return Path.Combine([.. parts]);
        }
    }

    public string Extension => Kind == NodeKind.File ? Path.GetExtension(Name).ToLowerInvariant() : "";

    public override string ToString() => $"{Name} ({Bytes.Format(Size)})";
}
