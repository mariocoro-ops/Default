using Windows.Foundation;

namespace PdfReader.Services;

/// <summary>
/// Extracts word bounding boxes with PdfPig for text-aware highlighting.
/// The PdfPig document opens lazily on first use (off the UI thread) and
/// results are cached per page. Any failure — encrypted file, scanned pages,
/// malformed content — degrades to an empty list, which makes highlights fall
/// back to freeform rectangles.
/// </summary>
public sealed class TextGeometryService : IDisposable
{
    private const double PointToDip = 96.0 / 72.0;

    private readonly byte[] _bytes;
    private readonly object _sync = new();
    private readonly Dictionary<uint, IReadOnlyList<Rect>> _cache = new();
    private UglyToad.PdfPig.PdfDocument? _document;
    private bool _openFailed;
    private bool _disposed;

    public TextGeometryService(byte[] bytes)
    {
        _bytes = bytes;
    }

    public Task<IReadOnlyList<Rect>> GetWordRectsAsync(uint pageIndex)
    {
        lock (_sync)
        {
            if (_cache.TryGetValue(pageIndex, out var cached))
            {
                return Task.FromResult(cached);
            }
        }

        return Task.Run<IReadOnlyList<Rect>>(() =>
        {
            lock (_sync)
            {
                if (_cache.TryGetValue(pageIndex, out var cached))
                {
                    return cached;
                }

                if (_disposed || _openFailed)
                {
                    return Array.Empty<Rect>();
                }

                try
                {
                    _document ??= UglyToad.PdfPig.PdfDocument.Open(_bytes);
                }
                catch
                {
                    _openFailed = true;
                    return Array.Empty<Rect>();
                }

                try
                {
                    // PdfPig is 1-based; coordinates are PDF points with a
                    // bottom-left origin, so flip Y and convert to DIPs.
                    var page = _document.GetPage((int)pageIndex + 1);
                    double pageHeight = page.Height;

                    var rects = new List<Rect>();
                    foreach (var word in page.GetWords())
                    {
                        var box = word.BoundingBox;
                        rects.Add(new Rect(
                            box.Left * PointToDip,
                            (pageHeight - box.Top) * PointToDip,
                            Math.Max(0, box.Right - box.Left) * PointToDip,
                            Math.Max(0, box.Top - box.Bottom) * PointToDip));
                    }

                    IReadOnlyList<Rect> result = rects;
                    _cache[pageIndex] = result;
                    return result;
                }
                catch
                {
                    return Array.Empty<Rect>();
                }
            }
        });
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _document?.Dispose();
            _document = null;
        }
    }
}
