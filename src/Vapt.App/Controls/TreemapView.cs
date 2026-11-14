using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Vapt.Core;
using Vapt.Core.Disk;
using Windows.Foundation;
using Windows.UI;
using Rect = Windows.Foundation.Rect;

namespace Vapt.Controls;

/// <summary>
/// Mapa do disco em blocos aninhados (estilo SpaceSniffer) desenhado com Win2D. O mapa
/// inteiro fica num bitmap em cache; passar o mouse só redesenha o cache e o contorno.
/// </summary>
public sealed partial class TreemapView : UserControl
{
    private readonly CanvasControl _canvas;
    private readonly TreemapOptions _baseOptions = new();
    private List<TreemapItem> _items = [];
    private CanvasRenderTarget? _cache;
    private bool _dirty = true;
    private TreemapItem? _hover;
    private DiskNode? _selected;
    private DiskNode? _root;
    private DiskFilter _filter = DiskFilter.Parse(null);

    // Animação de zoom: o mapa novo cresce a partir do retângulo do bloco clicado.
    private Rect? _zoomFrom;
    private DateTime _zoomStart;
    private const double ZoomMs = 220;

    public TreemapView()
    {
        _canvas = new CanvasControl { ClearColor = Colors.Transparent };
        _canvas.Draw += OnDraw;
        _canvas.CreateResources += (_, _) => _dirty = true;
        _canvas.SizeChanged += (_, _) => Invalidate();
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerExited += (_, _) => SetHover(null);
        _canvas.Tapped += OnTapped;
        _canvas.DoubleTapped += OnDoubleTapped;
        _canvas.RightTapped += OnRightTapped;
        _canvas.PointerPressed += OnPointerPressed;
        Content = _canvas;
        ActualThemeChanged += (_, _) => Invalidate();
        Unloaded += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRenderingTick;
            _zoomFrom = null;
            _cache?.Dispose();
            _cache = null;
        };
    }

    /// <summary>Pasta mostrada no mapa inteiro.</summary>
    public DiskNode? Root
    {
        get => _root;
        set
        {
            _root = value;
            _hover = null;
            Invalidate();
        }
    }

    /// <summary>Espaço livre do volume, mostrado como bloco quando a raiz é a unidade.</summary>
    public long FreeSpace { get; set; }

    public DiskFilter Filter
    {
        get => _filter;
        set
        {
            _filter = value;
            Invalidate();
        }
    }

    public DiskNode? Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            _canvas.Invalidate();
        }
    }

    public event EventHandler<DiskNode?>? HoverChanged;

    public event EventHandler<DiskNode>? ZoomRequested;

    public event EventHandler<DiskNode?>? SelectionChanged;

    public event EventHandler<(DiskNode Node, Point Position)>? ItemContextRequested;

    /// <summary>Botão "voltar" do mouse (XButton1).</summary>
    public event EventHandler? BackRequested;

    public event EventHandler? ForwardRequested;

    /// <summary>Refaz o layout (a árvore mudou ou o tamanho da área mudou).</summary>
    public void Invalidate()
    {
        _dirty = true;
        _canvas.Invalidate();
    }

    /// <summary>Retângulo atual de um nó no mapa (para animar o zoom a partir dele).</summary>
    public Rect? BoundsOf(DiskNode node)
    {
        var item = _items.FirstOrDefault(i => ReferenceEquals(i.Node, node));
        return item is null ? null : new Rect(item.Bounds.X, item.Bounds.Y, item.Bounds.W, item.Bounds.H);
    }

    public void AnimateZoomFrom(Rect? from)
    {
        _zoomFrom = from;
        _zoomStart = DateTime.UtcNow;
        CompositionTarget.Rendering -= OnRenderingTick;
        if (from is not null)
        {
            CompositionTarget.Rendering += OnRenderingTick;
        }
    }

    private void OnRenderingTick(object? sender, object e)
    {
        _canvas.Invalidate();
        if ((DateTime.UtcNow - _zoomStart).TotalMilliseconds >= ZoomMs)
        {
            CompositionTarget.Rendering -= OnRenderingTick;
            _zoomFrom = null;
        }
    }

    // ── Desenho ────────────────────────────────────────────────────────────

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var w = (float)sender.ActualWidth;
        var h = (float)sender.ActualHeight;
        if (w < 2 || h < 2 || _root is null)
        {
            return;
        }

        if (_dirty || _cache is null || Math.Abs(_cache.Size.Width - w) > 0.5 || Math.Abs(_cache.Size.Height - h) > 0.5)
        {
            Rebuild(sender, w, h);
        }

        var ds = args.DrawingSession;
        if (_zoomFrom is { } from)
        {
            var t = Math.Clamp((DateTime.UtcNow - _zoomStart).TotalMilliseconds / ZoomMs, 0, 1);
            var e = 1 - Math.Pow(1 - t, 3); // ease-out cúbico
            var dest = new Rect(
                from.X + (0 - from.X) * e,
                from.Y + (0 - from.Y) * e,
                from.Width + (w - from.Width) * e,
                from.Height + (h - from.Height) * e);
            ds.DrawImage(_cache, dest, new Rect(0, 0, w, h), (float)(0.35 + 0.65 * e));
            return;
        }

        ds.DrawImage(_cache!, 0, 0);
        var accent = Accent();
        if (_selected is not null && _items.FirstOrDefault(i => ReferenceEquals(i.Node, _selected)) is { } sel)
        {
            var r = sel.Bounds;
            ds.DrawRoundedRectangle(new Rect(r.X + 1, r.Y + 1, Math.Max(0, r.W - 2), Math.Max(0, r.H - 2)), 3, 3, accent, 2.5f);
        }

        if (_hover is not null)
        {
            var r = _hover.Bounds;
            ds.FillRectangle(new Rect(r.X, r.Y, r.W, r.H), Color.FromArgb(28, 255, 255, 255));
            ds.DrawRectangle(new Rect(r.X + 0.5, r.Y + 0.5, Math.Max(0, r.W - 1), Math.Max(0, r.H - 1)), accent, 1.5f);
        }
    }

    private void Rebuild(CanvasControl sender, float w, float h)
    {
        _dirty = false;
        var options = new TreemapOptions
        {
            HeaderHeight = _baseOptions.HeaderHeight,
            Padding = _baseOptions.Padding,
            FreeSpace = _root?.Parent is null ? FreeSpace : 0,
        };
        _items = _root is null ? [] : TreemapLayout.Build(_root, new MapRect(0, 0, w, h), options);
        if (_hover is not null)
        {
            _hover = _items.FirstOrDefault(i => ReferenceEquals(i.Node, _hover.Node));
        }

        if (_cache is null || Math.Abs(_cache.Size.Width - w) > 0.5 || Math.Abs(_cache.Size.Height - h) > 0.5)
        {
            _cache?.Dispose();
            _cache = new CanvasRenderTarget(sender, w, h);
        }

        using var ds = _cache.CreateDrawingSession();
        ds.Clear(Colors.Transparent);
        var dark = ActualTheme == ElementTheme.Dark;
        var palette = new Palette(dark);
        using var headerFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI Variable Text",
            FontSize = 11.5f,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            WordWrapping = CanvasWordWrapping.NoWrap,
            TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        };
        using var nameFormat = new CanvasTextFormat
        {
            FontFamily = "Segoe UI Variable Text",
            FontSize = 11.5f,
            WordWrapping = CanvasWordWrapping.NoWrap,
            TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        };

        foreach (var item in _items)
        {
            DrawItem(ds, item, palette, headerFormat, nameFormat);
        }
    }

    private void DrawItem(CanvasDrawingSession ds, TreemapItem item, Palette p, CanvasTextFormat headerFormat, CanvasTextFormat nameFormat)
    {
        var r = item.Bounds;
        var rect = new Rect(r.X, r.Y, r.W, r.H);
        var node = item.Node;

        if (node.Kind == NodeKind.FreeSpace)
        {
            ds.FillRectangle(rect, p.Free);
            using var style = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
            ds.DrawRectangle(Inset(rect, 0.5), p.FreeStroke, 1, style);
            DrawCenteredLabel(ds, rect, node.Name, Bytes.Format(node.Size), p.TextOnFree, nameFormat);
            return;
        }

        if (item.ShowsChildren)
        {
            // Pasta aberta: corpo, faixa de título e borda.
            var body = p.FolderBody(item.Depth);
            ds.FillRectangle(rect, body);
            var header = new Rect(r.X, r.Y, r.W, Math.Min(r.H, _baseOptions.HeaderHeight));
            ds.FillRectangle(header, p.FolderHeader(item.Depth));
            ds.DrawRectangle(Inset(rect, 0.5), p.FolderStroke, 1);
            if (r.W > 28)
            {
                var label = $"{node.Name} · {Bytes.Format(node.Size)}";
                var textRect = new Rect(r.X + 6, r.Y, Math.Max(0, r.W - 10), header.Height);
                DrawClipped(ds, label, textRect, p.FolderText, headerFormat);
            }

            return;
        }

        // Arquivo ou pasta pequena demais para abrir.
        var category = FileCategories.Of(node);
        var fill = p.Category(category);
        if (!_filter.IsEmpty && !_filter.Matches(node))
        {
            fill = Color.FromArgb(40, fill.R, fill.G, fill.B);
        }

        ds.FillRectangle(rect, fill);
        if (r.W >= 4 && r.H >= 4)
        {
            // Leve brilho no topo dá volume sem cansar a vista.
            ds.FillRectangle(new Rect(r.X, r.Y, r.W, Math.Min(r.H, 1.5)), Color.FromArgb(50, 255, 255, 255));
            ds.DrawRectangle(Inset(rect, 0.5), p.ItemStroke, 1);
        }

        var name = node.IsDirectory ? node.Name + "\\" : node.Name;
        DrawCenteredLabel(ds, rect, name, Bytes.Format(node.Size), p.TextOn(fill), nameFormat);
    }

    private static void DrawCenteredLabel(CanvasDrawingSession ds, Rect rect, string name, string size, Color color, CanvasTextFormat format)
    {
        if (rect.Width < 34 || rect.Height < 16)
        {
            return;
        }

        var inner = Inset(rect, 3);
        if (rect.Height >= 32)
        {
            var mid = inner.Y + inner.Height / 2;
            DrawClipped(ds, name, new Rect(inner.X, mid - 15, inner.Width, 15), color, format);
            DrawClipped(ds, size, new Rect(inner.X, mid, inner.Width, 15), Color.FromArgb(200, color.R, color.G, color.B), format);
        }
        else
        {
            DrawClipped(ds, name, inner, color, format);
        }
    }

    private static void DrawClipped(CanvasDrawingSession ds, string text, Rect rect, Color color, CanvasTextFormat format)
    {
        if (rect.Width < 8 || rect.Height < 8)
        {
            return;
        }

        using var layout = new CanvasTextLayout(ds, text, format, (float)rect.Width, (float)rect.Height)
        {
            TrimmingSign = CanvasTrimmingSign.Ellipsis,
            Options = CanvasDrawTextOptions.Clip,
        };
        ds.DrawTextLayout(layout, (float)rect.X, (float)rect.Y, color);
    }

    private static Rect Inset(Rect r, double d) =>
        new(r.X + d, r.Y + d, Math.Max(0, r.Width - 2 * d), Math.Max(0, r.Height - 2 * d));

    private Color Accent() =>
        (Color)(Application.Current.Resources.TryGetValue(ActualTheme == ElementTheme.Dark ? "SystemAccentColorLight2" : "SystemAccentColorDark1", out var c) ? c : Colors.DodgerBlue);

    // ── Interação ─────────────────────────────────────────────────────────

    private TreemapItem? Hit(Point p) => TreemapLayout.HitTest(_items, p.X, p.Y);

    private void SetHover(TreemapItem? item)
    {
        if (ReferenceEquals(item?.Node, _hover?.Node))
        {
            return;
        }

        _hover = item;
        _canvas.Invalidate();
        HoverChanged?.Invoke(this, item?.Node);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e) => SetHover(Hit(e.GetCurrentPoint(_canvas).Position));

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(_canvas).Properties;
        if (props.IsXButton1Pressed)
        {
            BackRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (props.IsXButton2Pressed)
        {
            ForwardRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        var item = Hit(e.GetPosition(_canvas));
        Selected = item?.Node;
        SelectionChanged?.Invoke(this, item?.Node);
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var item = Hit(e.GetPosition(_canvas));
        if (item is null)
        {
            return;
        }

        // Duplo clique num arquivo amplia a pasta dele, como no SpaceSniffer.
        var target = item.Node.IsDirectory ? item.Node : item.Node.Parent;
        if (target is not null && !ReferenceEquals(target, _root) && target.Kind == NodeKind.Directory)
        {
            ZoomRequested?.Invoke(this, target);
        }
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var pos = e.GetPosition(_canvas);
        if (Hit(pos) is { } item && item.Node.Kind != NodeKind.FreeSpace)
        {
            Selected = item.Node;
            SelectionChanged?.Invoke(this, item.Node);
            ItemContextRequested?.Invoke(this, (item.Node, pos));
        }
    }

    public UIElement Surface => _canvas;

    // ── Cores ────────────────────────────────────────────────────────────

    private readonly struct Palette(bool dark)
    {
        public Color Free => dark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(10, 0, 0, 0);

        public Color FreeStroke => dark ? Color.FromArgb(90, 255, 255, 255) : Color.FromArgb(90, 0, 0, 0);

        public Color TextOnFree => dark ? Color.FromArgb(200, 255, 255, 255) : Color.FromArgb(200, 0, 0, 0);

        public Color FolderStroke => dark ? Color.FromArgb(255, 20, 20, 22) : Color.FromArgb(255, 196, 192, 186);

        public Color ItemStroke => dark ? Color.FromArgb(150, 0, 0, 0) : Color.FromArgb(110, 255, 255, 255);

        public Color FolderText => dark ? Color.FromArgb(235, 255, 255, 255) : Color.FromArgb(230, 26, 26, 26);

        public Color FolderHeader(int depth)
        {
            // Tons alternados por nível ajudam a ver onde uma pasta termina.
            var even = depth % 2 == 0;
            return dark
                ? (even ? Color.FromArgb(255, 58, 61, 68) : Color.FromArgb(255, 70, 66, 60))
                : (even ? Color.FromArgb(255, 224, 228, 235) : Color.FromArgb(255, 236, 226, 211));
        }

        public Color FolderBody(int depth)
        {
            var even = depth % 2 == 0;
            return dark
                ? (even ? Color.FromArgb(255, 38, 40, 45) : Color.FromArgb(255, 46, 43, 39))
                : (even ? Color.FromArgb(255, 244, 246, 249) : Color.FromArgb(255, 249, 244, 237));
        }

        public Color Category(FileCategory c)
        {
            var (r, g, b) = c switch
            {
                FileCategory.Folder => dark ? (110, 104, 96) : (196, 184, 166),
                FileCategory.Video => (232, 116, 59),
                FileCategory.Image => (46, 158, 110),
                FileCategory.Audio => (196, 80, 140),
                FileCategory.Archive => (142, 107, 209),
                FileCategory.Program => (59, 130, 214),
                FileCategory.Document => (217, 162, 27),
                FileCategory.Code => (32, 163, 181),
                FileCategory.Game => (192, 86, 61),
                FileCategory.System => (125, 133, 144),
                _ => (106, 155, 216),
            };
            // No tema claro as cores ficam um pouco mais suaves.
            if (!dark)
            {
                r = (int)(r + (255 - r) * 0.18);
                g = (int)(g + (255 - g) * 0.18);
                b = (int)(b + (255 - b) * 0.18);
            }

            return Color.FromArgb(255, (byte)r, (byte)g, (byte)b);
        }

        public Color TextOn(Color fill)
        {
            var lum = (0.299 * fill.R + 0.587 * fill.G + 0.114 * fill.B) / 255;
            if (fill.A < 128)
            {
                return dark ? Color.FromArgb(150, 255, 255, 255) : Color.FromArgb(150, 0, 0, 0);
            }

            return lum > 0.62 ? Color.FromArgb(230, 20, 20, 20) : Color.FromArgb(245, 255, 255, 255);
        }
    }

    public static Color CategoryColor(FileCategory c, bool dark) => new Palette(dark).Category(c);
}
