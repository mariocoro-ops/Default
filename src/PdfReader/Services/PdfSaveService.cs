using PdfReader.Models;
using PdfReader.ViewModels;
using PdfSharp.Drawing;
using PdfIO = PdfSharp.Pdf.IO;

namespace PdfReader.Services;

/// <summary>
/// Writes the session's annotations into a copy of the PDF using PDFsharp.
/// Marks are drawn into the page content (flattened) in append mode, so the
/// result renders identically in every viewer. XGraphics.FromPdfPage uses a
/// top-left origin in points, matching our stored geometry up to the 96→72
/// unit conversion.
/// </summary>
public static class PdfSaveService
{
    private const double DipToPoint = 72.0 / 96.0;
    private const byte HighlightAlpha = 115; // ~45% — text stays readable underneath

    public static Task SaveAsync(DocumentViewModel doc, string targetPath)
    {
        // Snapshot on the UI thread; ObservableCollection isn't thread-safe.
        var pages = doc.Pages
            .Where(p => p.Annotations.Count > 0)
            .Select(p => (p.Index, Annotations: p.Annotations.ToList()))
            .ToList();
        byte[] bytes = doc.SourceBytes;

        return Task.Run(() =>
        {
            using var input = new MemoryStream(bytes, writable: false);
            using var pdf = PdfIO.PdfReader.Open(input, PdfIO.PdfDocumentOpenMode.Modify);

            foreach (var (index, annotations) in pages)
            {
                var page = pdf.Pages[(int)index];
                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                foreach (var annotation in annotations)
                {
                    Draw(gfx, annotation);
                }
            }

            pdf.Save(targetPath);
        });
    }

    private static void Draw(XGraphics gfx, AnnotationBase annotation)
    {
        switch (annotation)
        {
            case HighlightAnnotation highlight:
            {
                var brush = new XSolidBrush(XColor.FromArgb(
                    HighlightAlpha, highlight.Color.R, highlight.Color.G, highlight.Color.B));
                foreach (var r in highlight.Rects)
                {
                    gfx.DrawRectangle(
                        brush,
                        r.X * DipToPoint,
                        r.Y * DipToPoint,
                        r.Width * DipToPoint,
                        r.Height * DipToPoint);
                }

                break;
            }

            case InkAnnotation ink when ink.Points.Count > 0:
            {
                double width = Math.Max(0.5, ink.Thickness * DipToPoint);
                var color = XColor.FromArgb(255, ink.Color.R, ink.Color.G, ink.Color.B);

                if (ink.Points.Count == 1)
                {
                    var p = ink.Points[0];
                    gfx.DrawEllipse(
                        new XSolidBrush(color),
                        p.X * DipToPoint - width / 2,
                        p.Y * DipToPoint - width / 2,
                        width,
                        width);
                    break;
                }

                var pen = new XPen(color, width)
                {
                    LineCap = XLineCap.Round,
                    LineJoin = XLineJoin.Round,
                };
                gfx.DrawLines(
                    pen,
                    ink.Points.Select(p => new XPoint(p.X * DipToPoint, p.Y * DipToPoint)).ToArray());
                break;
            }
        }
    }
}
