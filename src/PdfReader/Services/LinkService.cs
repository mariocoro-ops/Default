using PdfReader.Models;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using Windows.Foundation;
using PdfIO = PdfSharp.Pdf.IO;

namespace PdfReader.Services;

/// <summary>
/// Extracts clickable link annotations (internal GoTo jumps and external URIs)
/// with PDFsharp's low-level dictionary access. Results are normalized to the
/// page and cached per page; the document opens lazily off the UI thread. Any
/// failure — encrypted file, exotic destinations, malformed annotations —
/// degrades to "no links", so link support never breaks viewing.
/// </summary>
public sealed class LinkService : IDisposable
{
    private readonly byte[] _bytes;
    private readonly object _sync = new();
    private readonly Dictionary<uint, IReadOnlyList<PdfLink>> _cache = new();

    private PdfDocument? _document;
    private Dictionary<PdfObjectID, int>? _pageIndexById;
    private bool _openFailed;
    private bool _disposed;

    public LinkService(byte[] bytes)
    {
        _bytes = bytes;
    }

    public Task<IReadOnlyList<PdfLink>> GetLinksAsync(uint pageIndex)
    {
        lock (_sync)
        {
            if (_cache.TryGetValue(pageIndex, out var cached))
            {
                return Task.FromResult(cached);
            }
        }

        return Task.Run<IReadOnlyList<PdfLink>>(() =>
        {
            lock (_sync)
            {
                if (_cache.TryGetValue(pageIndex, out var cached))
                {
                    return cached;
                }

                if (_disposed || _openFailed || !EnsureOpen())
                {
                    return Array.Empty<PdfLink>();
                }

                IReadOnlyList<PdfLink> links;
                try
                {
                    links = ExtractPageLinks((int)pageIndex);
                }
                catch
                {
                    links = Array.Empty<PdfLink>();
                }

                _cache[pageIndex] = links;
                return links;
            }
        });
    }

    private bool EnsureOpen()
    {
        if (_document is not null)
        {
            return true;
        }

        try
        {
            using var stream = new MemoryStream(_bytes, writable: false);
            _document = PdfIO.PdfReader.Open(stream, PdfIO.PdfDocumentOpenMode.Import);

            _pageIndexById = new Dictionary<PdfObjectID, int>();
            for (int i = 0; i < _document.PageCount; i++)
            {
                var reference = _document.Pages[i].Reference;
                if (reference is not null)
                {
                    _pageIndexById[reference.ObjectID] = i;
                }
            }

            return true;
        }
        catch
        {
            _openFailed = true;
            return false;
        }
    }

    private List<PdfLink> ExtractPageLinks(int pageIndex)
    {
        var result = new List<PdfLink>();
        var page = _document!.Pages[pageIndex];

        var annots = AsArray(page.Elements["/Annots"]);
        if (annots is null)
        {
            return result;
        }

        // Page box used to normalize link rectangles (top-left origin).
        var (cropLeft, cropTop, cropWidth, cropHeight) = GetPageBox(page);
        if (cropWidth <= 0 || cropHeight <= 0)
        {
            return result;
        }

        foreach (var item in annots.Elements)
        {
            try
            {
                var annot = AsDict(item);
                if (annot is null || annot.Elements.GetName("/Subtype") != "/Link")
                {
                    continue;
                }

                var rectArray = AsArray(annot.Elements["/Rect"]);
                if (rectArray is null || rectArray.Elements.Count < 4)
                {
                    continue;
                }

                double x1 = Num(rectArray.Elements[0]) ?? 0;
                double y1 = Num(rectArray.Elements[1]) ?? 0;
                double x2 = Num(rectArray.Elements[2]) ?? 0;
                double y2 = Num(rectArray.Elements[3]) ?? 0;
                double left = Math.Min(x1, x2), right = Math.Max(x1, x2);
                double bottom = Math.Min(y1, y2), top = Math.Max(y1, y2);

                var bounds = new Rect(
                    (left - cropLeft) / cropWidth,
                    (cropTop - top) / cropHeight,
                    (right - left) / cropWidth,
                    (top - bottom) / cropHeight);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    continue;
                }

                var link = BuildLink(annot, bounds, cropTop, cropHeight);
                if (link is not null)
                {
                    result.Add(link);
                }
            }
            catch
            {
                // Skip this annotation; keep the rest.
            }
        }

        return result;
    }

    private PdfLink? BuildLink(PdfDictionary annot, Rect bounds, double cropTop, double cropHeight)
    {
        // A link's target is either a direct /Dest or an /A action dictionary.
        var action = AsDict(annot.Elements["/A"]);
        if (action is not null)
        {
            string? subtype = action.Elements.GetName("/S");
            if (subtype == "/URI")
            {
                string? uri = (Deref(action.Elements["/URI"]) as PdfString)?.Value;
                return MakeUriLink(bounds, uri);
            }

            if (subtype == "/GoTo")
            {
                return MakeGoToLink(bounds, action.Elements["/D"], cropTop, cropHeight);
            }

            // GoToR / Launch / etc. are cross-document; not supported.
            return null;
        }

        var dest = annot.Elements["/Dest"];
        return dest is not null ? MakeGoToLink(bounds, dest, cropTop, cropHeight) : null;
    }

    private static PdfLink? MakeUriLink(Rect bounds, string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        return new PdfLink { NormalizedBounds = bounds, Uri = uri.Trim() };
    }

    private PdfLink? MakeGoToLink(Rect bounds, PdfItem? destItem, double cropTop, double cropHeight)
    {
        var destArray = ResolveDestination(destItem);
        if (destArray is null || destArray.Elements.Count == 0)
        {
            return null;
        }

        int? pageIndex = ResolvePageIndex(destArray.Elements[0]);
        if (pageIndex is null)
        {
            return null;
        }

        double topFraction = ResolveTopFraction(destArray, cropTop, cropHeight);
        return new PdfLink
        {
            NormalizedBounds = bounds,
            TargetPageIndex = pageIndex,
            TargetTopFraction = topFraction,
        };
    }

    /// <summary>Follows a destination that may be an explicit array or a named destination.</summary>
    private PdfArray? ResolveDestination(PdfItem? destItem)
    {
        destItem = Deref(destItem);
        switch (destItem)
        {
            case PdfArray array:
                return array;

            case PdfName name:
                return ResolveNamedDestination(name.Value.TrimStart('/'));

            case PdfString str:
                return ResolveNamedDestination(str.Value);

            default:
                return null;
        }
    }

    private PdfArray? ResolveNamedDestination(string name)
    {
        var catalog = _document!.Internals.Catalog;

        // Older form: catalog /Dests dictionary keyed by name.
        var dests = AsDict(catalog.Elements["/Dests"]);
        if (dests is not null)
        {
            var value = dests.Elements["/" + name];
            var resolved = DestValueToArray(value);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        // Newer form: catalog /Names /Dests name tree keyed by string.
        var names = AsDict(catalog.Elements["/Names"]);
        var destsTree = AsDict(names?.Elements["/Dests"]);
        if (destsTree is not null)
        {
            return SearchNameTree(destsTree, name);
        }

        return null;
    }

    private PdfArray? SearchNameTree(PdfDictionary node, string target)
    {
        // Leaf: /Names is a flat [key value key value ...] list.
        var namesArray = AsArray(node.Elements["/Names"]);
        if (namesArray is not null)
        {
            for (int i = 0; i + 1 < namesArray.Elements.Count; i += 2)
            {
                if ((Deref(namesArray.Elements[i]) as PdfString)?.Value == target)
                {
                    return DestValueToArray(namesArray.Elements[i + 1]);
                }
            }
        }

        // Intermediate: recurse into kids (linear — ignores /Limits for robustness).
        var kids = AsArray(node.Elements["/Kids"]);
        if (kids is not null)
        {
            foreach (var kid in kids.Elements)
            {
                var kidDict = AsDict(kid);
                if (kidDict is not null)
                {
                    var found = SearchNameTree(kidDict, target);
                    if (found is not null)
                    {
                        return found;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>A resolved destination is either the array itself or a dict with a /D array.</summary>
    private PdfArray? DestValueToArray(PdfItem? value)
    {
        value = Deref(value);
        return value switch
        {
            PdfArray array => array,
            PdfDictionary dict => AsArray(dict.Elements["/D"]),
            _ => null,
        };
    }

    private int? ResolvePageIndex(PdfItem? pageItem)
    {
        // Usually an indirect reference to the target page object...
        if (pageItem is PdfReference reference &&
            _pageIndexById!.TryGetValue(reference.ObjectID, out int index))
        {
            return index;
        }

        // ...occasionally a bare page number.
        double? number = Num(pageItem);
        if (number is not null)
        {
            int i = (int)number.Value;
            if (i >= 0 && i < _document!.PageCount)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the target Y from the destination's fit spec (XYZ/FitH/FitBH/FitR)
    /// and converts it to a 0..1 fraction from the top of the page.
    /// </summary>
    private static double ResolveTopFraction(PdfArray dest, double cropTop, double cropHeight)
    {
        if (dest.Elements.Count < 2)
        {
            return 0;
        }

        string? fit = (Deref(dest.Elements[1]) as PdfName)?.Value;
        double? topUserSpace = fit switch
        {
            "/XYZ" => dest.Elements.Count > 3 ? Num(dest.Elements[3]) : null,
            "/FitH" => dest.Elements.Count > 2 ? Num(dest.Elements[2]) : null,
            "/FitBH" => dest.Elements.Count > 2 ? Num(dest.Elements[2]) : null,
            "/FitR" => dest.Elements.Count > 4 ? Num(dest.Elements[4]) : null,
            _ => null,
        };

        if (topUserSpace is null)
        {
            return 0;
        }

        return Math.Clamp((cropTop - topUserSpace.Value) / cropHeight, 0, 1);
    }

    private static (double Left, double Top, double Width, double Height) GetPageBox(PdfPage page)
    {
        // Prefer the crop box (what the renderer shows); fall back to media box.
        var box = page.CropBox;
        if (box.Width <= 0 || box.Height <= 0)
        {
            box = page.MediaBox;
        }

        double left = Math.Min(box.X1, box.X2);
        double top = Math.Max(box.Y1, box.Y2);
        return (left, top, Math.Abs(box.Width), Math.Abs(box.Height));
    }

    // ------------------------------------------------------------ PDF helpers

    private static PdfItem? Deref(PdfItem? item) => item is PdfReference r ? r.Value : item;

    private static PdfDictionary? AsDict(PdfItem? item) => Deref(item) as PdfDictionary;

    private static PdfArray? AsArray(PdfItem? item) => Deref(item) as PdfArray;

    private static double? Num(PdfItem? item) => Deref(item) switch
    {
        PdfReal r => r.Value,
        PdfInteger i => i.Value,
        _ => null,
    };

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
