using Windows.Foundation;

namespace PdfReader.Models;

/// <summary>
/// A clickable link region read from the PDF. Bounds are normalized to the
/// page (0..1, top-left origin), matching how word boxes are carried, so the
/// link layer just multiplies by its own page size. Exactly one of the two
/// targets is set: an internal jump (page index + vertical fraction) or an
/// external URI.
/// </summary>
public sealed class PdfLink
{
    public Rect NormalizedBounds { get; init; }

    /// <summary>Zero-based target page for an internal link, else null.</summary>
    public int? TargetPageIndex { get; init; }

    /// <summary>Vertical position on the target page, 0 (top) to 1 (bottom).</summary>
    public double TargetTopFraction { get; init; }

    /// <summary>Absolute URI for an external link, else null.</summary>
    public string? Uri { get; init; }

    public bool IsInternal => TargetPageIndex.HasValue;
}
