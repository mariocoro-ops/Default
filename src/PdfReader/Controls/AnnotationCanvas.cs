using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI;
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
/// </summary>
public sealed class AnnotationCanvas : Canvas
{
    private const byte HighlightAlpha = 0x73;

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
    private Rectangle? _selectionRect;
    private Point _selectionStart;
    private bool _pointerActive;

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

    private void UpdateInteractivity() =>
        IsHitTestVisible = Page is not null && ToolState.Current.Tool != AnnotationTool.None;

    private void OnAnnotationsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    // ------------------------------------------------------------ rendering

    private void Rebuild()
    {
        Children.Clear();
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

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    // ------------------------------------------------------------ pointer interaction

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);
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
                _selectionStart = pos;
                _selectionRect = new Rectangle
                {
                    Fill = new SolidColorBrush(WithAlpha(ToolState.Current.HighlightColor, 0x40)),
                    Stroke = new SolidColorBrush(WithAlpha(ToolState.Current.HighlightColor, 0xA0)),
                    StrokeThickness = 1,
                    Width = 0,
                    Height = 0,
                };
                SetLeft(_selectionRect, pos.X);
                SetTop(_selectionRect, pos.Y);
                Children.Add(_selectionRect);
                break;

            case AnnotationTool.Erase:
                EraseAt(pos);
                break;
        }
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);
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

            case AnnotationTool.Highlight when _selectionRect is not null:
            {
                var pos = Clamp(e.GetCurrentPoint(this).Position);
                var rect = Normalize(_selectionStart, pos);
                SetLeft(_selectionRect, rect.X);
                SetTop(_selectionRect, rect.Y);
                _selectionRect.Width = rect.Width;
                _selectionRect.Height = rect.Height;
                break;
            }

            case AnnotationTool.Erase:
                EraseAt(Clamp(e.GetCurrentPoint(this).Position));
                break;
        }
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        base.OnPointerReleased(e);
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

            case AnnotationTool.Highlight when page is not null && _selectionRect is not null:
            {
                Children.Remove(_selectionRect);
                var rect = Normalize(_selectionStart, pos);
                if (rect.Width >= 3 || rect.Height >= 3)
                {
                    _ = CommitHighlightAsync(page, rect);
                }

                break;
            }
        }

        FinishInteraction(e);
    }

    protected override void OnPointerCanceled(PointerRoutedEventArgs e)
    {
        base.OnPointerCanceled(e);
        CancelActiveInteraction();
    }

    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        CancelActiveInteraction();
    }

    private async Task CommitHighlightAsync(PageViewModel page, Rect selection)
    {
        var color = ToolState.Current.HighlightColor;

        IReadOnlyList<Rect> words = Array.Empty<Rect>();
        var provider = ToolState.Current.WordProvider;
        if (provider is not null)
        {
            try
            {
                words = await provider(page.Index);
            }
            catch
            {
                // fall through to the freeform rectangle
            }
        }

        var hits = words.Where(w => AnnotationGeometry.Intersects(w, selection)).ToList();

        var highlight = new HighlightAnnotation { Color = color };
        if (hits.Count > 0)
        {
            highlight.Rects.AddRange(AnnotationGeometry.MergeIntoLines(hits));
        }
        else
        {
            // No text under the selection (scanned page, image, etc.) —
            // keep the raw rectangle so the tool still does something useful.
            highlight.Rects.Add(selection);
        }

        page.Annotations.Add(highlight);
        ToolState.Current.NotifyAnnotationAdded(page, highlight);
    }

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
        _selectionRect = null;
    }

    private void CancelActiveInteraction()
    {
        if (_activeStroke is not null)
        {
            Children.Remove(_activeStroke);
        }

        if (_selectionRect is not null)
        {
            Children.Remove(_selectionRect);
        }

        _pointerActive = false;
        _activeStroke = null;
        _activePoints = null;
        _selectionRect = null;
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
