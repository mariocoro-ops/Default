using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PdfReader.Services;
using PdfReader.ViewModels;
using Windows.UI;

namespace PdfReader.Controls;

/// <summary>
/// Draws find-in-document hits over a page. Deliberately separate from the
/// annotation layer: these highlights are transient — never saved into the PDF,
/// never undoable, never erasable. Non-interactive, so it can't disturb the
/// tools. Like the other overlays it is laid out at 100%-zoom size and scaled.
/// </summary>
public sealed class SearchLayer : Canvas
{
    private static readonly Color MatchColor = Color.FromArgb(0x66, 0xFF, 0xEB, 0x3B);   // yellow
    private static readonly Color CurrentColor = Color.FromArgb(0xAA, 0xFF, 0x8A, 0x00); // orange

    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(
        nameof(Page), typeof(PageViewModel), typeof(SearchLayer),
        new PropertyMetadata(null, (d, _) => ((SearchLayer)d).Rebuild()));

    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(double), typeof(SearchLayer),
        new PropertyMetadata(1.0, (d, e) =>
        {
            var layer = (SearchLayer)d;
            layer._scale.ScaleX = layer._scale.ScaleY = (double)e.NewValue;
        }));

    private readonly ScaleTransform _scale = new();

    public SearchLayer()
    {
        RenderTransform = _scale;
        IsHitTestVisible = false;

        Loaded += (_, _) =>
        {
            SearchState.Current.ResultsChanged += Rebuild;
            Rebuild();
        };
        Unloaded += (_, _) => SearchState.Current.ResultsChanged -= Rebuild;
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

    private void Rebuild()
    {
        Children.Clear();
        if (Page is null || !SearchState.Current.IsActive || Width <= 0 || Height <= 0)
        {
            return;
        }

        int currentIndex = SearchState.Current.CurrentIndex;
        foreach (var (index, match) in SearchState.Current.MatchesOnPage(Page.Index))
        {
            var brush = new SolidColorBrush(index == currentIndex ? CurrentColor : MatchColor);
            foreach (var rect in match.Rects)
            {
                var shape = new Rectangle
                {
                    Width = Math.Max(1, rect.Width * Width),
                    Height = Math.Max(1, rect.Height * Height),
                    Fill = brush,
                };
                SetLeft(shape, rect.X * Width);
                SetTop(shape, rect.Y * Height);
                Children.Add(shape);
            }
        }
    }
}
