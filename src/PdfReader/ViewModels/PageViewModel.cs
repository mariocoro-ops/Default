using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PdfReader.Models;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace PdfReader.ViewModels;

/// <summary>
/// One page of the open document. Holds the page's layout size (which scales
/// with zoom) and lazily renders the page bitmap when the page nears the
/// viewport. Rendering must happen on the UI thread because BitmapImage is
/// a UI-thread object; the actual rasterization inside RenderToStreamAsync
/// runs off-thread, so the UI stays responsive.
/// </summary>
public sealed class PageViewModel : INotifyPropertyChanged
{
    // Limit concurrent page renders so fast scrolling doesn't queue up
    // dozens of rasterizations at once.
    private static readonly SemaphoreSlim RenderGate = new(2);

    private readonly PdfDocument _document;
    private ImageSource? _source;
    private double _zoom = 1.0;
    private double _renderedScale;
    private bool _rendering;
    private bool _renderQueued;

    public PageViewModel(PdfDocument document, uint index, double baseWidth, double baseHeight)
    {
        _document = document;
        Index = index;
        BaseWidth = baseWidth;
        BaseHeight = baseHeight;
    }

    /// <summary>Zero-based page index.</summary>
    public uint Index { get; }

    /// <summary>Page width in DIPs at 100% zoom.</summary>
    public double BaseWidth { get; }

    /// <summary>Page height in DIPs at 100% zoom.</summary>
    public double BaseHeight { get; }

    /// <summary>Display scale of the monitor, so bitmaps are rendered at native pixel density.</summary>
    public double RasterizationScale { get; set; } = 1.0;

    /// <summary>Annotations on this page, in page DIPs at 100% zoom.</summary>
    public ObservableCollection<AnnotationBase> Annotations { get; } = new();

    public double Zoom
    {
        get => _zoom;
        set
        {
            if (Math.Abs(_zoom - value) < 0.001)
            {
                return;
            }

            _zoom = value;
            OnPropertyChanged(nameof(DisplayWidth));
            OnPropertyChanged(nameof(DisplayHeight));
        }
    }

    public double DisplayWidth => BaseWidth * _zoom;

    public double DisplayHeight => BaseHeight * _zoom;

    public ImageSource? Source
    {
        get => _source;
        private set
        {
            _source = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Renders the page bitmap at the current zoom if it isn't already.
    /// Re-entrant safe: a call made while a render is in flight queues one
    /// follow-up render (which picks up the latest zoom).
    /// </summary>
    public async Task EnsureRenderedAsync()
    {
        if (_rendering)
        {
            _renderQueued = true;
            return;
        }

        _rendering = true;
        try
        {
            do
            {
                _renderQueued = false;
                double scale = _zoom * RasterizationScale;
                if (Source is not null && Math.Abs(scale - _renderedScale) < scale * 0.01)
                {
                    continue; // already rendered at (close enough to) this scale
                }

                await RenderGate.WaitAsync();
                try
                {
                    uint pixelWidth = (uint)Math.Max(1, Math.Round(BaseWidth * scale));
                    using var page = _document.GetPage(Index);
                    using var stream = new InMemoryRandomAccessStream();
                    await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
                    {
                        DestinationWidth = pixelWidth,
                    });

                    stream.Seek(0);
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                    Source = bitmap;
                    _renderedScale = scale;
                }
                finally
                {
                    RenderGate.Release();
                }
            }
            while (_renderQueued);
        }
        catch
        {
            // A single page failing to render should not take the app down;
            // the page simply stays blank and is retried on the next viewport pass.
        }
        finally
        {
            _rendering = false;
        }
    }

    /// <summary>Frees the page bitmap when the page has scrolled far off screen.</summary>
    public void Release()
    {
        if (_rendering)
        {
            return;
        }

        Source = null;
        _renderedScale = 0;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
