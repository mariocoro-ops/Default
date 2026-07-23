using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
/// annotations and handles every editing tool: text selection (with copy),
/// pen, highlighter, text boxes, comments, signature stamps, and the eraser.
/// The canvas is laid out at the page's 100%-zoom size and scaled with a
/// RenderTransform, so all pointer coordinates arrive already in
/// zoom-independent page DIPs.
/// </summary>
public sealed class AnnotationCanvas : Canvas
{
    private const byte HighlightAlpha = 0x73;
    private const double AnchorSnapDistance = 25.0; // DIPs — press must be near text to snap
    private const double DragThresholdSquared = 16.0; // ~4 DIPs before a press becomes a drag
    private const double ResizeHandleSize = 12.0;

    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(
        nameof(Page), typeof(PageViewModel), typeof(AnnotationCanvas),
        new PropertyMetadata(null, OnPageChanged));

    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(double), typeof(AnnotationCanvas),
        new PropertyMetadata(1.0, OnZoomChanged));

    private readonly ScaleTransform _scale = new();
    private readonly Dictionary<AnnotationBase, List<FrameworkElement>> _visuals = new();

    // Live drawing state
    private Polyline? _activeStroke;
    private List<Point>? _activePoints;
    private Point _lastPoint;
    private bool _pointerActive;

    // Hand-tool panning state
    private bool _panning;
    private Point _panStart; // window coordinates at grab

    // Live highlight-selection state
    private Point _selectionStart;
    private readonly List<Rectangle> _previewShapes = new();

    // Text-selection state (TextSelect tool)
    private int _selectionAnchor = -1;
    private readonly List<Rectangle> _selectionShapes = new();
    private List<Rect>? _selectionRects; // committed selection, for select-then-highlight

    // Object whose editor should open once the pointer is released. Opening
    // (and focusing) an editor during the press is what made text boxes
    // "flash": the focus didn't survive the in-flight pointer interaction,
    // LostFocus fired on an empty box, and the box deleted itself.
    private AnnotationBase? _pendingEditorObject;

    // Object interaction state (Text / Comment / Signature tools)
    private AnnotationBase? _pressedObject;
    private Point _pressPos;
    private bool _dragMoved;
    private List<(FrameworkElement Element, double Left, double Top)>? _dragOriginals;
    private SignatureAnnotation? _resizingSignature;
    private Rect _resizeStartBounds;

    // In-place text editor
    private TextBox? _activeEditor;
    private TextBoxAnnotation? _editingAnnotation;
    private bool _closingEditor;

    // Word boxes for this page (base DIPs, reading order), fetched lazily.
    private List<WordBox>? _pageWords;
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
            ToolState.Current.SelectionOwnerChanged += OnSelectionOwnerChanged;
            UpdateInteractivity();
        };
        Unloaded += (_, _) =>
        {
            ToolState.Current.PropertyChanged -= OnToolStateChanged;
            ToolState.Current.SelectionOwnerChanged -= OnSelectionOwnerChanged;
        };

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

        // Word cache and selection belong to the old page (containers recycle).
        canvas._pageWords = null;
        canvas._wordFetchStarted = false;
        canvas._selectionAnchor = -1;

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
            // Switching to the highlighter with a live text selection turns
            // that selection into a highlight.
            if (ToolState.Current.Tool == AnnotationTool.Highlight)
            {
                ConvertSelectionToHighlight();
            }

            CancelActiveInteraction();
            ClearTextSelectionVisuals();
            Rebuild(); // signature resize handles appear/disappear with the tool
            UpdateInteractivity();
        }
    }

    private void ConvertSelectionToHighlight()
    {
        if (Page is null ||
            !ReferenceEquals(ToolState.Current.SelectionOwner, this) ||
            _selectionRects is not { Count: > 0 })
        {
            return;
        }

        var highlight = new HighlightAnnotation { Color = ToolState.Current.HighlightColor };
        highlight.Rects.AddRange(_selectionRects);
        _selectionRects = null;
        Page.Annotations.Add(highlight);
        ToolState.Current.NotifyAnnotationAdded(Page, highlight);
        ToolState.Current.ClearSelection();
    }

    private void OnSelectionOwnerChanged(object? owner)
    {
        if (!ReferenceEquals(owner, this))
        {
            ClearTextSelectionVisuals();
        }
    }

    private void UpdateInteractivity()
    {
        var tool = ToolState.Current.Tool;
        IsHitTestVisible = Page is not null && tool != AnnotationTool.None;

        ProtectedCursor = tool switch
        {
            AnnotationTool.Hand => InputSystemCursor.Create(InputSystemCursorShape.Hand),
            AnnotationTool.TextSelect => InputSystemCursor.Create(InputSystemCursorShape.IBeam),
            AnnotationTool.Highlight => InputSystemCursor.Create(InputSystemCursorShape.IBeam),
            AnnotationTool.Text => InputSystemCursor.Create(InputSystemCursorShape.IBeam),
            AnnotationTool.Draw => InputSystemCursor.Create(InputSystemCursorShape.Cross),
            AnnotationTool.Comment => InputSystemCursor.Create(InputSystemCursorShape.Arrow),
            AnnotationTool.Signature => InputSystemCursor.Create(InputSystemCursorShape.Arrow),
            AnnotationTool.Erase => InputSystemCursor.Create(InputSystemCursorShape.Hand),
            _ => null,
        };

        if (tool is AnnotationTool.Highlight or AnnotationTool.TextSelect)
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
            _pageWords = new List<WordBox>();
            return;
        }

        _ = FetchWordsAsync(provider, Page);
    }

    private async Task FetchWordsAsync(
        Func<uint, Task<IReadOnlyList<WordBox>>> provider, PageViewModel page)
    {
        List<WordBox> words;
        try
        {
            // Provider results are normalized (0..1); scale to this page's
            // base size so everything downstream is in page DIPs.
            var normalized = await provider(page.Index);
            words = normalized
                .Select(w => new WordBox(
                    new Rect(
                        w.Bounds.X * page.BaseWidth,
                        w.Bounds.Y * page.BaseHeight,
                        w.Bounds.Width * page.BaseWidth,
                        w.Bounds.Height * page.BaseHeight),
                    w.Text))
                .ToList();
        }
        catch
        {
            words = new List<WordBox>();
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
            var r = _pageWords[i].Bounds;
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

    private List<List<WordBox>>? WordSpanLines(Point anchor, Point current)
    {
        if (_pageWords is not { Count: > 0 })
        {
            return null;
        }

        int? anchorIndex = WordIndexAt(anchor, AnchorSnapDistance);
        if (!anchorIndex.HasValue)
        {
            return null;
        }

        // Once anchored to text, the far end snaps to the nearest word no
        // matter how far the pointer strays.
        int? currentIndex = WordIndexAt(current, double.MaxValue);
        if (!currentIndex.HasValue)
        {
            return null;
        }

        int lo = Math.Min(anchorIndex.Value, currentIndex.Value);
        int hi = Math.Max(anchorIndex.Value, currentIndex.Value);
        return AnnotationGeometry.GroupIntoLines(_pageWords.GetRange(lo, hi - lo + 1));
    }

    /// <summary>
    /// The highlight rectangles for the current drag: words between anchor and
    /// pointer in reading order when the drag started on text, else the raw
    /// dragged rectangle.
    /// </summary>
    private List<Rect> ComputeHighlightRects(Point anchor, Point current, out bool snappedToText)
    {
        var lines = WordSpanLines(anchor, current);
        if (lines is not null)
        {
            snappedToText = true;
            return lines.Select(AnnotationGeometry.MergeLine).ToList();
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
        _visuals.Clear();
        _previewShapes.Clear();
        _selectionShapes.Clear();
        if (Page is null)
        {
            return;
        }

        foreach (var annotation in Page.Annotations)
        {
            if (annotation == _editingAnnotation)
            {
                continue; // its in-place editor stands in for the visual
            }

            AddShapesFor(annotation);
        }

        if (_activeEditor is not null)
        {
            Children.Add(_activeEditor);
        }
    }

    private void AddShapesFor(AnnotationBase annotation)
    {
        var elements = new List<FrameworkElement>();
        _visuals[annotation] = elements;

        void Add(FrameworkElement element)
        {
            elements.Add(element);
            Children.Add(element);
        }

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
                    Add(rect);
                }

                break;
            }

            case TextBoxAnnotation text:
            {
                var block = new TextBlock
                {
                    Text = text.Text,
                    FontSize = text.FontSize,
                    Foreground = new SolidColorBrush(text.Color),
                    TextWrapping = TextWrapping.NoWrap,
                };
                block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                text.RenderSize = new Size(block.DesiredSize.Width, block.DesiredSize.Height);
                SetLeft(block, text.Position.X);
                SetTop(block, text.Position.Y);
                Add(block);
                break;
            }

            case CommentAnnotation comment:
            {
                var icon = new Border
                {
                    Width = CommentAnnotation.IconSize,
                    Height = CommentAnnotation.IconSize,
                    CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Color.FromArgb(255, 0xFF, 0xC1, 0x07)),
                    Child = new FontIcon
                    {
                        Glyph = "\uE90A", // Comment
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.FromArgb(255, 0x20, 0x20, 0x20)),
                    },
                };
                SetLeft(icon, comment.Position.X);
                SetTop(icon, comment.Position.Y);
                Add(icon);
                break;
            }

            case SignatureAnnotation signature:
            {
                var b = signature.Bounds;
                FrameworkElement visual;
                if (SignatureStore.CachedBitmap is { } bitmap)
                {
                    visual = new Image
                    {
                        Source = bitmap,
                        Stretch = Stretch.Fill,
                        Width = b.Width,
                        Height = b.Height,
                    };
                }
                else
                {
                    visual = new Rectangle
                    {
                        Width = b.Width,
                        Height = b.Height,
                        Fill = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
                    };
                }

                SetLeft(visual, b.X);
                SetTop(visual, b.Y);
                Add(visual);

                if (ToolState.Current.Tool == AnnotationTool.Signature)
                {
                    // Bottom-right resize handle, shown while the tool is active.
                    var handle = new Rectangle
                    {
                        Width = ResizeHandleSize,
                        Height = ResizeHandleSize,
                        Fill = new SolidColorBrush(Color.FromArgb(255, 0x4F, 0x8E, 0xF7)),
                        RadiusX = 2,
                        RadiusY = 2,
                    };
                    SetLeft(handle, b.Right - ResizeHandleSize / 2);
                    SetTop(handle, b.Bottom - ResizeHandleSize / 2);
                    Add(handle);
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
                    Add(dot);
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

                Add(new ShapePath
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

    private void UpdateTextSelectionVisuals(Point current)
    {
        ClearTextSelectionVisuals();

        var lines = WordSpanLines(_selectionStart, current);
        if (lines is null)
        {
            return;
        }

        foreach (var r in lines.Select(AnnotationGeometry.MergeLine))
        {
            var shape = new Rectangle
            {
                Width = r.Width,
                Height = r.Height,
                Fill = new SolidColorBrush(Color.FromArgb(0x55, 0x33, 0x99, 0xFF)),
            };
            SetLeft(shape, r.X);
            SetTop(shape, r.Y);
            _selectionShapes.Add(shape);
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

    private void ClearTextSelectionVisuals()
    {
        foreach (var shape in _selectionShapes)
        {
            Children.Remove(shape);
        }

        _selectionShapes.Clear();
        _selectionRects = null;
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

        // Clicking anywhere while a text editor is open commits it first.
        if (_activeEditor is not null)
        {
            CloseActiveEditor(commit: true);
            e.Handled = true;
            return;
        }

        var pos = Clamp(e.GetCurrentPoint(this).Position);
        CapturePointer(e.Pointer);
        _pointerActive = true;
        _pressPos = pos;
        _dragMoved = false;
        e.Handled = true;

        switch (tool)
        {
            case AnnotationTool.Hand:
                // Track in window coordinates so the deltas we feed back into
                // the ScrollViewer aren't themselves moved by the scrolling.
                _panning = true;
                _panStart = e.GetCurrentPoint(null).Position;
                ToolState.Current.NotifyPanStarted();
                break;

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

            case AnnotationTool.TextSelect:
                EnsureWordsLoaded();
                ToolState.Current.ClaimSelection(this);
                _selectionStart = pos;
                _selectionAnchor = WordIndexAt(pos, AnchorSnapDistance) ?? -1;
                ClearTextSelectionVisuals();
                break;

            case AnnotationTool.Text:
            {
                var hit = FindObjectAt<TextBoxAnnotation>(pos);
                if (hit is not null)
                {
                    BeginObjectPress(hit);
                }
                else
                {
                    var annotation = new TextBoxAnnotation
                    {
                        Position = pos,
                        FontSize = ToolState.Current.FontSize,
                        Color = ToolState.Current.PenColor,
                    };
                    Page.Annotations.Add(annotation);
                    ToolState.Current.NotifyAnnotationAdded(Page, annotation);
                    _pendingEditorObject = annotation; // editor opens on release
                }

                break;
            }

            case AnnotationTool.Comment:
            {
                var hit = FindObjectAt<CommentAnnotation>(pos);
                if (hit is not null)
                {
                    BeginObjectPress(hit);
                }
                else
                {
                    var annotation = new CommentAnnotation
                    {
                        Position = new Point(
                            Math.Max(0, pos.X - CommentAnnotation.IconSize / 2),
                            Math.Max(0, pos.Y - CommentAnnotation.IconSize / 2)),
                    };
                    Page.Annotations.Add(annotation);
                    ToolState.Current.NotifyAnnotationAdded(Page, annotation);
                    _pendingEditorObject = annotation; // editor opens on release
                }

                break;
            }

            case AnnotationTool.Signature:
            {
                var onHandle = FindSignatureHandleAt(pos);
                if (onHandle is not null)
                {
                    _resizingSignature = onHandle;
                    _resizeStartBounds = onHandle.Bounds;
                }
                else if (FindObjectAt<SignatureAnnotation>(pos) is { } hit)
                {
                    BeginObjectPress(hit);
                }
                else if (SignatureStore.HasSignature)
                {
                    double width = Math.Min(180, Width * 0.4);
                    double height = width / Math.Max(0.1, SignatureStore.AspectRatio);
                    var bounds = new Rect(
                        Math.Clamp(pos.X - width / 2, 0, Math.Max(0, Width - width)),
                        Math.Clamp(pos.Y - height / 2, 0, Math.Max(0, Height - height)),
                        width,
                        height);
                    var annotation = new SignatureAnnotation { Bounds = bounds };
                    Page.Annotations.Add(annotation);
                    ToolState.Current.NotifyAnnotationAdded(Page, annotation);
                }

                break;
            }

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
        var pos = Clamp(e.GetCurrentPoint(this).Position);

        switch (ToolState.Current.Tool)
        {
            case AnnotationTool.Hand when _panning:
            {
                var p = e.GetCurrentPoint(null).Position;
                ToolState.Current.NotifyPanUpdated(p.X - _panStart.X, p.Y - _panStart.Y);
                break;
            }

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
                UpdateHighlightPreview(pos);
                break;

            case AnnotationTool.TextSelect when _selectionAnchor >= 0:
                UpdateTextSelectionVisuals(pos);
                break;

            case AnnotationTool.Text:
            case AnnotationTool.Comment:
            case AnnotationTool.Signature:
                UpdateObjectDrag(pos);
                break;

            case AnnotationTool.Erase:
                EraseAt(pos);
                break;
        }
    }

    private void UpdateObjectDrag(Point pos)
    {
        if (_resizingSignature is not null)
        {
            double aspect = Math.Max(0.1, SignatureStore.AspectRatio);
            double newWidth = Math.Clamp(
                _resizeStartBounds.Width + (pos.X - _pressPos.X), 40, Math.Max(40, Width));
            double newHeight = newWidth / aspect;

            if (_visuals.TryGetValue(_resizingSignature, out var elements) && elements.Count > 0)
            {
                elements[0].Width = newWidth;
                elements[0].Height = newHeight;
                if (elements.Count > 1)
                {
                    SetLeft(elements[1], _resizeStartBounds.X + newWidth - ResizeHandleSize / 2);
                    SetTop(elements[1], _resizeStartBounds.Y + newHeight - ResizeHandleSize / 2);
                }
            }

            _dragMoved = true;
            return;
        }

        if (_pressedObject is null)
        {
            return;
        }

        double dx = pos.X - _pressPos.X, dy = pos.Y - _pressPos.Y;
        if (!_dragMoved && dx * dx + dy * dy < DragThresholdSquared)
        {
            return;
        }

        _dragMoved = true;
        if (_dragOriginals is not null)
        {
            foreach (var (element, left, top) in _dragOriginals)
            {
                SetLeft(element, left + dx);
                SetTop(element, top + dy);
            }
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

            case AnnotationTool.TextSelect when _selectionAnchor >= 0:
            {
                UpdateTextSelectionVisuals(pos);
                var lines = WordSpanLines(_selectionStart, pos);
                ToolState.Current.SetSelectedText(
                    lines is not null ? AnnotationGeometry.BuildText(lines) : string.Empty);
                _selectionRects = lines?.Select(AnnotationGeometry.MergeLine).ToList();
                break;
            }

            case AnnotationTool.Text:
            case AnnotationTool.Comment:
            case AnnotationTool.Signature:
                CommitObjectInteraction(pos);
                if (_pendingEditorObject is TextBoxAnnotation pendingText)
                {
                    OpenTextEditor(pendingText);
                }
                else if (_pendingEditorObject is CommentAnnotation pendingComment)
                {
                    OpenCommentEditor(pendingComment);
                }

                _pendingEditorObject = null;
                break;
        }

        FinishInteraction(e);
    }

    private void CommitObjectInteraction(Point pos)
    {
        if (_resizingSignature is not null)
        {
            if (_dragMoved)
            {
                double aspect = Math.Max(0.1, SignatureStore.AspectRatio);
                double newWidth = Math.Clamp(
                    _resizeStartBounds.Width + (pos.X - _pressPos.X), 40, Math.Max(40, Width));
                _resizingSignature.Bounds = new Rect(
                    _resizeStartBounds.X, _resizeStartBounds.Y, newWidth, newWidth / aspect);
                Rebuild();
            }

            _resizingSignature = null;
            return;
        }

        if (_pressedObject is null)
        {
            return;
        }

        var pressed = _pressedObject;
        _pressedObject = null;
        _dragOriginals = null;

        if (_dragMoved)
        {
            double dx = pos.X - _pressPos.X, dy = pos.Y - _pressPos.Y;
            switch (pressed)
            {
                case TextBoxAnnotation text:
                    text.Position = new Point(text.Position.X + dx, text.Position.Y + dy);
                    break;
                case CommentAnnotation comment:
                    comment.Position = new Point(comment.Position.X + dx, comment.Position.Y + dy);
                    break;
                case SignatureAnnotation signature:
                    signature.Bounds = new Rect(
                        signature.Bounds.X + dx, signature.Bounds.Y + dy,
                        signature.Bounds.Width, signature.Bounds.Height);
                    break;
            }

            Rebuild();
        }
        else
        {
            // A click (no drag) opens the object's editor.
            switch (pressed)
            {
                case TextBoxAnnotation text:
                    OpenTextEditor(text);
                    break;
                case CommentAnnotation comment:
                    OpenCommentEditor(comment);
                    break;
            }
        }
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        // Only a loss during an active interaction is a real cancellation.
        if (_pointerActive)
        {
            CancelActiveInteraction();
        }
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // ReleasePointerCapture in FinishInteraction raises this event too;
        // by then _pointerActive is already false. Cancelling unconditionally
        // here is what killed freshly opened text editors.
        if (_pointerActive)
        {
            CancelActiveInteraction();
        }
    }

    // ------------------------------------------------------------ object helpers

    private void BeginObjectPress(AnnotationBase annotation)
    {
        _pressedObject = annotation;
        _dragOriginals = _visuals.TryGetValue(annotation, out var elements)
            ? elements.Select(el => (el, GetLeft(el), GetTop(el))).ToList()
            : null;
    }

    private T? FindObjectAt<T>(Point pos)
        where T : AnnotationBase
    {
        if (Page is null)
        {
            return null;
        }

        for (int i = Page.Annotations.Count - 1; i >= 0; i--)
        {
            if (Page.Annotations[i] is T match &&
                AnnotationGeometry.Contains(GetObjectBounds(match), pos, 2))
            {
                return match;
            }
        }

        return null;
    }

    private SignatureAnnotation? FindSignatureHandleAt(Point pos)
    {
        if (Page is null)
        {
            return null;
        }

        for (int i = Page.Annotations.Count - 1; i >= 0; i--)
        {
            if (Page.Annotations[i] is SignatureAnnotation signature)
            {
                var b = signature.Bounds;
                var handle = new Rect(
                    b.Right - ResizeHandleSize / 2, b.Bottom - ResizeHandleSize / 2,
                    ResizeHandleSize, ResizeHandleSize);
                if (AnnotationGeometry.Contains(handle, pos, 4))
                {
                    return signature;
                }
            }
        }

        return null;
    }

    private static Rect GetObjectBounds(AnnotationBase annotation) => annotation switch
    {
        TextBoxAnnotation text => new Rect(
            text.Position.X,
            text.Position.Y,
            Math.Max(40, text.RenderSize.Width),
            Math.Max(20, text.RenderSize.Height)),
        CommentAnnotation comment => new Rect(
            comment.Position.X, comment.Position.Y, CommentAnnotation.IconSize, CommentAnnotation.IconSize),
        SignatureAnnotation signature => signature.Bounds,
        _ => default,
    };

    // ------------------------------------------------------------ text box editor

    private void OpenTextEditor(TextBoxAnnotation annotation)
    {
        CloseActiveEditor(commit: true);

        _editingAnnotation = annotation;
        _activeEditor = new TextBox
        {
            Text = annotation.Text,
            FontSize = annotation.FontSize,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            MinWidth = 140,
            Foreground = new SolidColorBrush(annotation.Color),
            // The app is dark-themed but the editor floats on the white page.
            RequestedTheme = ElementTheme.Light,
            Background = new SolidColorBrush(Colors.White),
        };
        // Offset roughly compensates the TextBox's inner padding so the
        // editor's text sits where the committed TextBlock will render.
        SetLeft(_activeEditor, annotation.Position.X - 10);
        SetTop(_activeEditor, annotation.Position.Y - 6);
        _activeEditor.LostFocus += (_, _) => CloseActiveEditor(commit: true);

        Rebuild(); // hides the static visual, re-adds the editor

        // Focus one tick later — focusing while the pointer interaction is
        // still settling doesn't stick, and the resulting LostFocus would
        // immediately commit-and-delete the empty box.
        var editor = _activeEditor;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(_activeEditor, editor))
            {
                editor.Focus(FocusState.Programmatic);
                editor.SelectionStart = editor.Text.Length;
            }
        });
    }

    private void CloseActiveEditor(bool commit)
    {
        if (_activeEditor is null || _closingEditor)
        {
            return;
        }

        _closingEditor = true;
        var editor = _activeEditor;
        var annotation = _editingAnnotation;
        _activeEditor = null;
        _editingAnnotation = null;
        Children.Remove(editor);

        if (annotation is not null)
        {
            if (commit)
            {
                annotation.Text = editor.Text;
            }

            if (string.IsNullOrWhiteSpace(annotation.Text))
            {
                // An empty text box is pointless — treat as removed.
                if (Page is not null && Page.Annotations.Remove(annotation))
                {
                    ToolState.Current.NotifyAnnotationRemoved(Page, annotation);
                }
                else
                {
                    Rebuild();
                }
            }
            else
            {
                Rebuild();
            }
        }

        _closingEditor = false;
    }

    // ------------------------------------------------------------ comment editor

    private void OpenCommentEditor(CommentAnnotation annotation)
    {
        if (Page is null || !_visuals.TryGetValue(annotation, out var elements) || elements.Count == 0)
        {
            return;
        }

        var page = Page;
        var box = new TextBox
        {
            Text = annotation.Text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Width = 260,
            Height = 110,
            PlaceholderText = "Write a comment…",
        };

        var deleteButton = new Button { Content = "Delete comment" };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(box);
        panel.Children.Add(deleteButton);

        var flyout = new Flyout
        {
            Content = panel,
            Placement = FlyoutPlacementMode.RightEdgeAlignedTop,
        };

        bool deleted = false;
        deleteButton.Click += (_, _) =>
        {
            deleted = true;
            if (page.Annotations.Remove(annotation))
            {
                ToolState.Current.NotifyAnnotationRemoved(page, annotation);
            }

            flyout.Hide();
        };

        flyout.Closed += (_, _) =>
        {
            if (!deleted)
            {
                annotation.Text = box.Text;
            }
        };

        flyout.ShowAt(elements[0]);
        box.Focus(FocusState.Programmatic);
    }

    // ------------------------------------------------------------ erase / cleanup

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
        // Clear the interaction state BEFORE releasing capture — the release
        // synchronously raises PointerCaptureLost, which must see the
        // interaction as already finished.
        _pointerActive = false;
        _panning = false;
        _activeStroke = null;
        _activePoints = null;
        _pressedObject = null;
        _dragOriginals = null;
        _resizingSignature = null;
        ReleasePointerCapture(e.Pointer);
    }

    private void CancelActiveInteraction()
    {
        if (_activeStroke is not null)
        {
            Children.Remove(_activeStroke);
        }

        ClearPreview();
        CloseActiveEditor(commit: true);

        if (_pressedObject is not null || _resizingSignature is not null)
        {
            Rebuild(); // snap any half-dragged visuals back to their model state
        }

        _pointerActive = false;
        _panning = false;
        _activeStroke = null;
        _activePoints = null;
        _pressedObject = null;
        _dragOriginals = null;
        _resizingSignature = null;
        _pendingEditorObject = null;
        _selectionAnchor = -1;
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
