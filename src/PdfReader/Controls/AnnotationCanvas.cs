using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PdfReader.Models;
using PdfReader.Services;
using PdfReader.ViewModels;
using Windows.Foundation;
using Windows.UI;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace PdfReader.Controls;

/// <summary>
/// Transparent interactive layer above each page bitmap. Renders the page's
/// annotations and handles the Draw / Highlight / Erase tools. The canvas is
/// laid out at the page's 100%-zoom size and scaled with a RenderTransform,
/// so all pointer coordinates arrive already in zoom-independent page DIPs.
///
/// Highlighting works like text selection in a normal PDF reader: the drag
/// selects words in reading order between the anchor and the pointer (full
/// lines in the middle, partial first/last lines), with a live preview. On
/// pages without extractable text it falls back to a freeform rectangle.
/// </summary>
public sealed class AnnotationCanvas : Canvas
{
    private const byte HighlightAlpha = 0x73;
    private const double AnchorSnapDistance = 25.0; // DIPs — press must be near text to enter selection mode

    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(
        nameof(Page), typeof(PageViewModel), typeof(AnnotationCanvas),
        new PropertyMetadata(null, OnPageChanged));

    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(double), typeof(AnnotationCanvas),
        new PropertyMetadata(1.0, OnZoomChanged));

    private readonly ScaleTransform _scale = new();

    // Live drawing state
    private Polyline? _activeStroke;
    private List<Point>? _activePoints;
    private Point _lastPoint;
    private bool _pointerActive;

    // Live highlight-selection state
    private Point _selectionStart;
    private readonly List<Rectangle> _previewShapes = new();

    // Word boxes for this page (base DIPs, reading order), fetched lazily.
    private List<Rect>? _pageWords;
    private bool _wordFetchStarted;

    public AnnotationCanvas()
    {
        // A transparent (not null) background keeps the whole surface hit-testable.
        Background = new SolidColorBrush(Colors.Transparent);
        RenderTransform = _scale;
        IsHitTestVisible = false;

        Loaded += (_, _) =>
        {
            ToolState.Current.PropertyChanged += OnToolStateChanged;
            UpdateInteractivity();
        };
        Unloaded += (_, _) => ToolState.Current.PropertyChanged -= OnToolStateChanged;

        // WinUI 3's UIElement has no protected OnPointer* virtuals (unlike
        // UWP's Control), so wire the pointer events directly.
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnPointerCanceled;
        PointerCaptureLost += OnPointerCaptureLost;
    }

    public PageViewModel? Page
    {
        get => (PageViewModel?)GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    public double Zoom
    {
        get => (double)GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    private static void OnPageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (AnnotationCanvas)d;
        if (e.OldValue is PageViewModel oldPage)
        {
            oldPage.Annotations.CollectionChanged -= canvas.OnAnnotationsChanged;
        }

        if (e.NewValue is PageViewModel newPage)
        {
            newPage.Annotations.CollectionChanged += canvas.OnAnnotationsChanged;
        }

        // Word cache belongs to the old page (containers get recycled).
        canvas._pageWords = null;
        canvas._wordFetchStarted = false;

        canvas.CancelActiveInteraction();
        canvas.Rebuild();
        canvas.UpdateInteractivity();
    }

    private static void OnZoomChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var canvas = (AnnotationCanvas)d;
        canvas._scale.ScaleX = canvas._scale.ScaleY = (double)e.NewValue;
    }

    private void OnToolStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolState.Tool))
        {
            CancelActiveInteraction();
            UpdateInteractivity();
        }
    }

    private void UpdateInteractivity()
    {
        var tool = ToolState.Current.Tool;
        IsHitTestVisible = Page is not null && tool != AnnotationTool.None;

        ProtectedCursor = tool switch
        {
            AnnotationTool.Highlight => InputSystemCursor.Create(InputSystemCursorShape.IBeam),
            AnnotationTool.Draw => InputSystemCursor.Create(InputSystemCursorShape.Cross),
            AnnotationTool.Erase => InputSystemCursor.Create(InputSystemCursorShape.Hand),
            _ => null,
        };

        if (tool == AnnotationTool.Highlight)
        {
            EnsureWordsLoaded();
        }
    }

    private void OnAnnotationsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    // ------------------------------------------------------------ word geometry

    private void EnsureWordsLoaded()
    {
        if (_wordFetchStarted || Page is null)
        {
            return;
        }

        _wordFetchStarted = true;
        var provider = ToolState.Current.WordProvider;
        if (provider is null)
        {
            _pageWords = new List<Rect>();
            return;
        }

        _ = FetchWordsAsync(provider, Page);
    }

    private async Task FetchWordsAsync(
        Func<uint, Task<IReadOnlyList<Rect>>> provider, PageViewModel page)
    {
        List<Rect> words;
        try
        {
            // Provider results are normalized (0..1); scale to this page's
            // base size so everything downstream is in page DIPs.
            var normalized = await provider(page.Index);
            words = normalized
                .Select(n => new Rect(
                    n.X * page.BaseWidth,
                    n.Y * page.BaseHeight,
                    n.Width * page.BaseWidth,
                    n.Height * page.BaseHeight))
                .ToList();
        }
        catch
        {
            words = new List<Rect>();
        }

        if (Page == page)
        {
            _pageWords = words;
        }
    }

    /// <summary>
    /// Index of the word at (or near) a point, or null if none within
    /// <paramref name="maxDistance"/>.
    /// </summary>
    private int? WordIndexAt(Point p, double maxDistance)
    {
        if (_pageWords is null || _pageWords.Count == 0)
        {
            return null;
        }

        int best = -1;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < _pageWords.Count; i++)
        {
            var r = _pageWords[i];
            double dx = Math.Max(Math.Max(r.Left - p.X, 0), p.X - r.Right);
            double dy = Math.Max(Math.Max(r.Top - p.Y, 0), p.Y - r.Bottom);
            if (dx <= 0 && dy <= 0)
            {
                return i; // inside the box
            }

            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return bestDistance <= maxDistance ? best : null;
    }

    /// <summary>
    /// The highlight rectangles for the current drag: words between anchor and
    /// pointer in reading order when the drag started on text, else the raw
    /// dragged rectangle.
    /// </summary>
    private List<Rect> ComputeHighlightRects(Point anchor, Point current, out bool snappedToText)
    {
        if (_pageWords is { Count: > 0 })
        {
            int? anchorIndex = WordIndexAt(anchor, AnchorSnapDistance);
            if (anchorIndex.HasValue)
            {
                // Once anchored to text, the far end snaps to the nearest word
                // no matter how far the pointer strays.
                int? currentIndex = WordIndexAt(current, double.MaxValue);
                if (currentIndex.HasValue)
                {
                    snappedToText = true;
                    int lo = Math.Min(anchorIndex.Value, currentIndex.Value);
                    int hi = Math.Max(anchorIndex.Value, currentIndex.Value);
                    return AnnotationGeometry.MergeIntoLines(_pageWords.GetRange(lo, hi - lo + 1));
                }
            }
        }

        snappedToText = false;
        var rect = Normalize(anchor, current);
        return rect.Width >= 3 || rect.Height >= 3
            ? new List<Rect> { rect }
            : new List<Rect>();
    }

    // ------------------------------------------------------------ rendering

    private void Rebuild()
    {
        Children.Clear();
        _previewShapes.Clear();
        if (Page is null)
        {
            return;
        }

        foreach (var annotation in Page.Annotations)
        {
            AddShapesFor(annotation);
        }
    }

    private void AddShapesFor(AnnotationBase annotation)
    {
        switch (annotation)
        {
            case HighlightAnnotation highlight:
            {
                var fill = new SolidColorBrush(WithAlpha(highlight.Color, HighlightAlpha));
                foreach (var r in highlight.Rects)
                {
                    var rect = new Rectangle { Width = r.Width, Height = r.Height, Fill = fill };
                    SetLeft(rect, r.X);
                    SetTop(rect, r.Y);
                    Children.Add(rect);
                }

                break;
            }

            case InkAnnotation ink when ink.Points.Count > 0:
            {
                if (ink.Points.Count == 1)
                {
                    var p = ink.Points[0];
                    var dot = new Ellipse
                    {
                        Width = ink.Thickness,
                        Height = ink.Thickness,
                        Fill = new SolidColorBrush(ink.Color),
                    };
                    SetLeft(dot, p.X - ink.Thickness / 2);
                    SetTop(dot, p.Y - ink.Thickness / 2);
                    Children.Add(dot);
                    break;
                }

                var figure = new PathFigure { StartPoint = ink.Points[0], IsClosed = false, IsFilled = false };
                var segment = new PolyLineSegment();
                for (int i = 1; i < ink.Points.Count; i++)
                {
                    segment.Points.Add(ink.Points[i]);
                }

                figure.Segments.Add(segment);
                var geometry = new PathGeometry();
                geometry.Figures.Add(figure);

                Children.Add(new ShapePath
                {
                    Data = geometry,
                    Stroke = new SolidColorBrush(ink.Color),
                    StrokeThickness = ink.Thickness,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                });
                break;
            }
        }
    }

    private void UpdateHighlightPreview(Point current)
    {
        ClearPreview();

        var rects = ComputeHighlightRects(_selectionStart, current, out bool snappedToText);
        var color = ToolState.Current.HighlightColor;
        foreach (var r in rects)
        {
            var shape = new Rectangle
            {
                Width = r.Width,
                Height = r.Height,
                Fill = new SolidColorBrush(WithAlpha(color, 0x55)),
            };
            if (!snappedToText)
            {
                shape.Stroke = new SolidColorBrush(WithAlpha(color, 0xA0));
                shape.StrokeThickness = 1;
            }

            SetLeft(shape, r.X);
            SetTop(shape, r.Y);
            _previewShapes.Add(shape);
            Children.Add(shape);
        }
    }

    private void ClearPreview()
    {
        foreach (var shape in _previewShapes)
        {
            Children.Remove(shape);
        }

        _previewShapes.Clear();
    }

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    // ------------------------------------------------------------ pointer interaction

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Page is null || _pointerActive)
        {
            return;
        }

        var tool = ToolState.Current.Tool;
        if (tool == AnnotationTool.None)
        {
            return;
        }

        var pos = Clamp(e.GetCurrentPoint(this).Position);
        CapturePointer(e.Pointer);
        _pointerActive = true;
        e.Handled = true;

        switch (tool)
        {
            case AnnotationTool.Draw:
                _activePoints = new List<Point> { pos };
                _lastPoint = pos;
                _activeStroke = new Polyline
                {
                    Stroke = new SolidColorBrush(ToolState.Current.PenColor),
                    StrokeThickness = ToolState.Current.PenThickness,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                };
                _activeStroke.Points.Add(pos);
                Children.Add(_activeStroke);
                break;

            case AnnotationTool.Highlight:
                EnsureWordsLoaded();
                _selectionStart = pos;
                UpdateHighlightPreview(pos);
                break;

            case AnnotationTool.Erase:
                EraseAt(pos);
                break;
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerActive || Page is null)
        {
            return;
        }

        e.Handled = true;
        switch (ToolState.Current.Tool)
        {
            case AnnotationTool.Draw when _activeStroke is not null && _activePoints is not null:
            {
                // Intermediate points arrive newest-first; walk them oldest-first.
                var points = e.GetIntermediatePoints(this);
                for (int i = points.Count - 1; i >= 0; i--)
                {
                    var p = Clamp(points[i].Position);
                    double dx = p.X - _lastPoint.X, dy = p.Y - _lastPoint.Y;
                    if (dx * dx + dy * dy < 1.5)
                    {
                        continue;
                    }

                    _activePoints.Add(p);
                    _activeStroke.Points.Add(p);
                    _lastPoint = p;
                }

                break;
            }

            case AnnotationTool.Highlight:
                UpdateHighlightPreview(Clamp(e.GetCurrentPoint(this).Position));
                break;

            case AnnotationTool.Erase:
                EraseAt(Clamp(e.GetCurrentPoint(this).Position));
                break;
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerActive)
        {
            return;
        }

        e.Handled = true;
        var page = Page;
        var pos = Clamp(e.GetCurrentPoint(this).Position);

        switch (ToolState.Current.Tool)
        {
            case AnnotationTool.Draw when page is not null && _activePoints is not null:
            {
                var ink = new InkAnnotation
                {
                    Color = ToolState.Current.PenColor,
                    Thickness = ToolState.Current.PenThickness,
                };
                ink.Points.AddRange(_activePoints);
                if (_activeStroke is not null)
                {
                    Children.Remove(_activeStroke);
                }

                page.Annotations.Add(ink); // triggers Rebuild
                ToolState.Current.NotifyAnnotationAdded(page, ink);
                break;
            }

            case AnnotationTool.Highlight when page is not null:
            {
                ClearPreview();
                var rects = ComputeHighlightRects(_selectionStart, pos, out _);
                if (rects.Count > 0)
                {
                    var highlight = new HighlightAnnotation { Color = ToolState.Current.HighlightColor };
                    highlight.Rects.AddRange(rects);
                    page.Annotations.Add(highlight); // triggers Rebuild
                    ToolState.Current.NotifyAnnotationAdded(page, highlight);
                }

                break;
            }
        }

        FinishInteraction(e);
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e) =>
        CancelActiveInteraction();

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e) =>
        CancelActiveInteraction();

    private void EraseAt(Point pos)
    {
        if (Page is null)
        {
            return;
        }

        for (int i = Page.Annotations.Count - 1; i >= 0; i--)
        {
            var annotation = Page.Annotations[i];
            if (AnnotationGeometry.HitTest(annotation, pos))
            {
                Page.Annotations.RemoveAt(i);
                ToolState.Current.NotifyAnnotationRemoved(Page, annotation);
                break; // one per pointer event keeps erasing predictable
            }
        }
    }

    private void FinishInteraction(PointerRoutedEventArgs e)
    {
        ReleasePointerCapture(e.Pointer);
        _pointerActive = false;
        _activeStroke = null;
        _activePoints = null;
    }

    private void CancelActiveInteraction()
    {
        if (_activeStroke is not null)
        {
            Children.Remove(_activeStroke);
        }

        ClearPreview();
        _pointerActive = false;
        _activeStroke = null;
        _activePoints = null;
    }

    private Point Clamp(Point p) => new(
        Math.Clamp(p.X, 0, Math.Max(0, Width)),
        Math.Clamp(p.Y, 0, Math.Max(0, Height)));

    private static Rect Normalize(Point a, Point b) => new(
        Math.Min(a.X, b.X),
        Math.Min(a.Y, b.Y),
        Math.Abs(a.X - b.X),
        Math.Abs(a.Y - b.Y));
}
