using PdfReader.Models;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using Windows.Foundation;

namespace PdfReader.Services;

/// <summary>
/// Extracts word bounding boxes with PdfPig for text-aware highlighting.
/// Boxes are returned in reading order, normalized to the page (0..1 in both
/// axes, top-left origin), which sidesteps any unit or crop-box mismatch
/// between PdfPig and the renderer — the overlay just multiplies by its own
/// page size. The PdfPig document opens lazily on first use (off the UI
/// thread) and results are cached per page. Any failure — encrypted file,
/// scanned pages, malformed content — degrades to an empty list, which makes
/// highlights fall back to freeform rectangles.
/// </summary>
public sealed class TextGeometryService : IDisposable
{
    private readonly byte[] _bytes;
    private readonly object _sync = new();
    private readonly Dictionary<uint, IReadOnlyList<WordBox>> _cache = new();
    private UglyToad.PdfPig.PdfDocument? _document;
    private bool _openFailed;
    private bool _disposed;

    public TextGeometryService(byte[] bytes)
    {
        _bytes = bytes;
    }

    public Task<IReadOnlyList<WordBox>> GetWordsAsync(uint pageIndex)
    {
        lock (_sync)
        {
            if (_cache.TryGetValue(pageIndex, out var cached))
            {
                return Task.FromResult(cached);
            }
        }

        return Task.Run<IReadOnlyList<WordBox>>(() =>
        {
            lock (_sync)
            {
                if (_cache.TryGetValue(pageIndex, out var cached))
                {
                    return cached;
                }

                if (_disposed || _openFailed)
                {
                    return Array.Empty<WordBox>();
                }

                try
                {
                    _document ??= UglyToad.PdfPig.PdfDocument.Open(_bytes);
                }
                catch
                {
                    _openFailed = true;
                    return Array.Empty<WordBox>();
                }

                try
                {
                    // PdfPig is 1-based; coordinates are absolute PDF user
                    // space with a bottom-left origin. Normalize against the
                    // crop box (the region the renderer actually shows) and
                    // flip Y to a top-left origin.
                    var page = _document.GetPage((int)pageIndex + 1);

                    double cropLeft = 0, cropBottom = 0;
                    double cropWidth = page.Width, cropHeight = page.Height;
                    try
                    {
                        var crop = page.CropBox.Bounds;
                        if (crop.Width > 0 && crop.Height > 0)
                        {
                            cropLeft = crop.Left;
                            cropBottom = crop.Bottom;
                            cropWidth = crop.Width;
                            cropHeight = crop.Height;
                        }
                    }
                    catch
                    {
                        // fall back to the page size
                    }

                    double cropTop = cropBottom + cropHeight;
                    var words = new List<WordBox>();
                    foreach (var word in GetWordsInReadingOrder(page))
                    {
                        var box = word.BoundingBox;
                        words.Add(new WordBox(
                            new Rect(
                                (box.Left - cropLeft) / cropWidth,
                                (cropTop - box.Top) / cropHeight,
                                Math.Max(0, box.Right - box.Left) / cropWidth,
                                Math.Max(0, box.Top - box.Bottom) / cropHeight),
                            word.Text));
                    }

                    IReadOnlyList<WordBox> result = words;
                    _cache[pageIndex] = result;
                    return result;
                }
                catch
                {
                    return Array.Empty<WordBox>();
                }
            }
        });
    }

    /// <summary>
    /// Words in visual reading order. The raw content-stream order runs
    /// straight across multi-column layouts, so the page is first segmented
    /// into text blocks (recursive XY-cut splits on whitespace gutters — i.e.
    /// column boundaries), the blocks are put into reading order, and words
    /// are emitted block by block, line by line. Any hiccup falls back to the
    /// plain content-stream order.
    /// </summary>
    private static IEnumerable<Word> GetWordsInReadingOrder(Page page)
    {
        try
        {
            var words = page.GetWords(NearestNeighbourWordExtractor.Instance).ToList();
            if (words.Count == 0)
            {
                return words;
            }

            var blocks = RecursiveXYCut.Instance.GetBlocks(words);
            var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks);
            return ordered
                .SelectMany(block => block.TextLines)
                .SelectMany(line => line.Words)
                .ToList();
        }
        catch
        {
            return page.GetWords();
        }
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
