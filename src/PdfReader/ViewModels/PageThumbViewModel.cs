using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace PdfReader.ViewModels;

/// <summary>
/// One page thumbnail in the page editor. Deliberately much thinner than
/// <see cref="PageViewModel"/>: no zoom, no annotations, no text geometry —
/// a fixed-width picture and a page number. Thumbnails render lazily as the
/// grid realizes containers, so a 300-page deck opens instantly.
/// </summary>
public sealed class PageThumbViewModel : INotifyPropertyChanged
{
    /// <summary>Thumbnail width in DIPs. Height follows the page's aspect ratio.</summary>
    public const double ThumbWidth = 168.0;

    // A few at a time: enough to fill a scroll-ahead, few enough that a fast
    // flick doesn't queue up hundreds of rasterizations.
    private static readonly SemaphoreSlim RenderGate = new(3);

    private readonly PdfDocument _document;
    private readonly uint _pageIndex;
    private ImageSource? _source;
    private int _position;
    private bool _rendering;

    public PageThumbViewModel(PdfDocument document, uint pageIndex, int position, double rasterizationScale)
    {
        _document = document;
        _pageIndex = pageIndex;
        _position = position;
        RasterizationScale = rasterizationScale;

        using var page = document.GetPage(pageIndex);
        var size = page.Size;
        double aspect = size.Width > 0 ? size.Height / size.Width : 1.294;
        ThumbHeight = Math.Round(ThumbWidth * aspect);
    }

    /// <summary>
    /// Zero-based position in the working document. The grid reorders these
    /// objects directly, so after a drag this is the page's OLD position — which
    /// is exactly the permutation the reorder needs. Reset after every rebuild.
    /// </summary>
    public int Position
    {
        get => _position;
        set
        {
            if (_position == value)
            {
                return;
            }

            _position = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Label));
        }
    }

    /// <summary>Page number as shown under the thumbnail.</summary>
    public string Label => (_position + 1).ToString();

    public double ThumbHeight { get; }

    public double RasterizationScale { get; }

    public ImageSource? Source
    {
        get => _source;
        private set
        {
            _source = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Rasterizes the thumbnail once. Safe to call repeatedly.</summary>
    public async Task EnsureRenderedAsync()
    {
        if (_rendering || Source is not null)
        {
            return;
        }

        _rendering = true;
        try
        {
            await RenderGate.WaitAsync();
            try
            {
                uint pixelWidth = (uint)Math.Max(1, Math.Round(ThumbWidth * RasterizationScale));
                using var page = _document.GetPage(_pageIndex);
                using var stream = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
                {
                    DestinationWidth = pixelWidth,
                });

                stream.Seek(0);
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                Source = bitmap;
            }
            finally
            {
                RenderGate.Release();
            }
        }
        catch
        {
            // A thumbnail that won't render leaves an empty card rather than
            // taking the editor down; the page itself is still editable.
        }
        finally
        {
            _rendering = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
