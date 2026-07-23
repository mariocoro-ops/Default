using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PdfReader.Models;
using PdfReader.Services;
using PdfReader.ViewModels;
using Windows.UI;

namespace PdfReader.Controls;

/// <summary>
/// Renders transparent clickable targets over the PDF's own link annotations.
/// It sits below the AnnotationCanvas, so links are live only in select mode
/// (when the annotation overlay is not hit-testable); any active tool takes
/// precedence. Targets are tap-activated and don't handle pointer press, so
/// drag-panning that starts on a link still pans. Like the annotation overlay,
/// it is laid out at 100%-zoom size and scaled with a RenderTransform.
/// </summary>
public sealed class LinkLayer : Canvas
{
    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(
        nameof(Page), typeof(PageViewModel), typeof(LinkLayer),
        new PropertyMetadata(null, OnPageChanged));

    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(double), typeof(LinkLayer),
        new PropertyMetadata(1.0, OnZoomChanged));

    private readonly ScaleTransform _scale = new();
    private bool _fetchStarted;

    public LinkLayer()
    {
        // Background stays null so only the link targets are hit-testable; the
        // rest of the page passes pointer/scroll through to the ScrollViewer.
        RenderTransform = _scale;
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
        var layer = (LinkLayer)d;
        layer.Children.Clear();
        layer._fetchStarted = false;
        layer.LoadLinks();
    }

    private static void OnZoomChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var layer = (LinkLayer)d;
        layer._scale.ScaleX = layer._scale.ScaleY = (double)e.NewValue;
    }

    private void LoadLinks()
    {
        if (_fetchStarted || Page is null)
        {
            return;
        }

        _fetchStarted = true;
        var provider = ToolState.Current.LinkProvider;
        if (provider is not null)
        {
            _ = BuildAsync(provider, Page);
        }
    }

    private async Task BuildAsync(Func<uint, Task<IReadOnlyList<PdfLink>>> provider, PageViewModel page)
    {
        IReadOnlyList<PdfLink> links;
        try
        {
            links = await provider(page.Index);
        }
        catch
        {
            return;
        }

        // The container may have been recycled to another page while awaiting.
        if (Page != page)
        {
            return;
        }

        foreach (var link in links)
        {
            var target = new LinkTarget(link)
            {
                Width = link.NormalizedBounds.Width * page.BaseWidth,
                Height = link.NormalizedBounds.Height * page.BaseHeight,
            };
            SetLeft(target, link.NormalizedBounds.X * page.BaseWidth);
            SetTop(target, link.NormalizedBounds.Y * page.BaseHeight);
            target.Tapped += OnLinkTapped;
            Children.Add(target);
        }
    }

    private static void OnLinkTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not LinkTarget target)
        {
            return;
        }

        e.Handled = true;
        var link = target.Link;
        if (link.IsInternal)
        {
            ToolState.Current.RequestNavigateToPage(link.TargetPageIndex!.Value, link.TargetTopFraction);
        }
        else if (link.Uri is not null)
        {
            ToolState.Current.RequestOpenUri(link.Uri);
        }
    }

    /// <summary>
    /// A single clickable link region: a transparent, hand-cursored hit target
    /// with a faint hover tint so links are discoverable. It deliberately does
    /// not handle pointer press (only Tapped), leaving drag-pan gestures to the
    /// ScrollViewer.
    /// </summary>
    private sealed class LinkTarget : Grid
    {
        private static readonly SolidColorBrush Transparent = new(Colors.Transparent);
        private static readonly SolidColorBrush Hover = new(Color.FromArgb(0x22, 0x4F, 0x8E, 0xF7));

        public LinkTarget(PdfLink link)
        {
            Link = link;
            Background = Transparent;
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
            PointerEntered += (_, _) => Background = Hover;
            PointerExited += (_, _) => Background = Transparent;
        }

        public PdfLink Link { get; }
    }
}
