using PdfSharp.Pdf;
using PdfIO = PdfSharp.Pdf.IO;

namespace PdfReader.Services;

/// <summary>
/// Page-level surgery on a PDF: delete pages, insert pages from another file,
/// and reorder. Every operation takes a byte array and returns a new one — the
/// document is never mutated in place and no file on disk is touched, so a
/// failed operation leaves the caller's bytes intact.
///
/// Deleting uses Modify mode, which keeps the document's structure (outlines,
/// named destinations) as far as PDFsharp can. Insert and reorder have to build
/// a new document, because page objects are being interleaved across files;
/// that import copies each page with its resources but drops document-level
/// extras such as bookmarks and form fields. That trade is the reason delete
/// takes the gentler path.
/// </summary>
public static class PdfPageEditService
{
    /// <summary>Removes the given zero-based page indices. At least one page must survive.</summary>
    public static Task<byte[]> DeletePagesAsync(byte[] source, IReadOnlyList<int> indices) =>
        Task.Run(() =>
        {
            using var input = new MemoryStream(source, writable: false);
            using var pdf = PdfIO.PdfReader.Open(input, PdfIO.PdfDocumentOpenMode.Modify);

            var doomed = indices.Where(i => i >= 0 && i < pdf.PageCount)
                                .Distinct()
                                .OrderByDescending(i => i)
                                .ToList();
            if (doomed.Count == 0)
            {
                return source;
            }

            if (doomed.Count >= pdf.PageCount)
            {
                throw new InvalidOperationException("A PDF must keep at least one page.");
            }

            // Descending, so each removal can't shift the indices still to come.
            foreach (int i in doomed)
            {
                pdf.Pages.RemoveAt(i);
            }

            return Save(pdf);
        });

    /// <summary>
    /// Inserts pages from <paramref name="incoming"/> into <paramref name="target"/>
    /// at the given zero-based position. <paramref name="incomingIndices"/> selects
    /// which incoming pages to take, in the order given; null means all of them.
    /// </summary>
    public static Task<byte[]> InsertPagesAsync(
        byte[] target,
        byte[] incoming,
        IReadOnlyList<int>? incomingIndices,
        int at) =>
        Task.Run(() =>
        {
            using var targetInput = new MemoryStream(target, writable: false);
            using var source = PdfIO.PdfReader.Open(targetInput, PdfIO.PdfDocumentOpenMode.Import);

            using var incomingInput = new MemoryStream(incoming, writable: false);
            using var donor = PdfIO.PdfReader.Open(incomingInput, PdfIO.PdfDocumentOpenMode.Import);

            var taken = (incomingIndices ?? Enumerable.Range(0, donor.PageCount).ToList())
                .Where(i => i >= 0 && i < donor.PageCount)
                .ToList();
            if (taken.Count == 0)
            {
                throw new InvalidOperationException("No pages were selected from that file.");
            }

            at = Math.Clamp(at, 0, source.PageCount);

            using var result = new PdfDocument();
            CopyDocumentInfo(source, result);

            for (int i = 0; i < at; i++)
            {
                result.AddPage(source.Pages[i]);
            }

            foreach (int i in taken)
            {
                result.AddPage(donor.Pages[i]);
            }

            for (int i = at; i < source.PageCount; i++)
            {
                result.AddPage(source.Pages[i]);
            }

            return Save(result);
        });

    /// <summary>
    /// Rewrites the document so its pages appear in the order given, where each
    /// entry is a zero-based index into the current document.
    /// </summary>
    public static Task<byte[]> ReorderAsync(byte[] source, IReadOnlyList<int> order) =>
        Task.Run(() =>
        {
            using var input = new MemoryStream(source, writable: false);
            using var pdf = PdfIO.PdfReader.Open(input, PdfIO.PdfDocumentOpenMode.Import);

            if (order.Count != pdf.PageCount ||
                order.Distinct().Count() != pdf.PageCount ||
                order.Any(i => i < 0 || i >= pdf.PageCount))
            {
                throw new InvalidOperationException("The new page order doesn't match the document.");
            }

            // Already in order — don't rewrite the file for nothing.
            bool unchanged = true;
            for (int i = 0; i < order.Count && unchanged; i++)
            {
                unchanged = order[i] == i;
            }

            if (unchanged)
            {
                return source;
            }

            using var result = new PdfDocument();
            CopyDocumentInfo(pdf, result);
            foreach (int i in order)
            {
                result.AddPage(pdf.Pages[i]);
            }

            return Save(result);
        });

    /// <summary>Page count of an arbitrary PDF, for validating an insert range.</summary>
    public static Task<int> GetPageCountAsync(byte[] bytes) =>
        Task.Run(() =>
        {
            using var input = new MemoryStream(bytes, writable: false);
            using var pdf = PdfIO.PdfReader.Open(input, PdfIO.PdfDocumentOpenMode.Import);
            return pdf.PageCount;
        });

    /// <summary>
    /// Parses a page range like "1-3, 7, 10-" into zero-based indices, in the
    /// order written. Blank or "all" means every page. Returns null if the text
    /// can't be parsed or names no page in range, so callers can report it.
    /// </summary>
    public static IReadOnlyList<int>? ParsePageRange(string? text, int pageCount)
    {
        if (pageCount <= 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(text) || text.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return Enumerable.Range(0, pageCount).ToList();
        }

        var result = new List<int>();
        foreach (string rawPart in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string part = rawPart.Trim();
            if (part.Length == 0)
            {
                continue;
            }

            int dash = part.IndexOf('-');
            if (dash < 0)
            {
                if (!int.TryParse(part, out int single))
                {
                    return null;
                }

                if (single >= 1 && single <= pageCount)
                {
                    result.Add(single - 1);
                }

                continue;
            }

            // "3-", "-5" and "2-6" are all reasonable things to type.
            string leftText = part[..dash].Trim();
            string rightText = part[(dash + 1)..].Trim();

            int from = 1;
            if (leftText.Length > 0 && !int.TryParse(leftText, out from))
            {
                return null;
            }

            int to = pageCount;
            if (rightText.Length > 0 && !int.TryParse(rightText, out to))
            {
                return null;
            }

            if (from > to)
            {
                (from, to) = (to, from);
            }

            from = Math.Max(from, 1);
            to = Math.Min(to, pageCount);
            for (int i = from; i <= to; i++)
            {
                result.Add(i - 1);
            }
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>Carries the title/author across when a rebuild starts from a blank document.</summary>
    private static void CopyDocumentInfo(PdfDocument from, PdfDocument to)
    {
        try
        {
            to.Info.Title = from.Info.Title;
            to.Info.Author = from.Info.Author;
            to.Info.Subject = from.Info.Subject;
            to.Info.Keywords = from.Info.Keywords;
        }
        catch
        {
            // Metadata is a nicety; never fail an edit over it.
        }
    }

    private static byte[] Save(PdfDocument pdf)
    {
        using var output = new MemoryStream();
        pdf.Save(output, closeStream: false);
        return output.ToArray();
    }
}
