using PdfReader.Models;
using PdfReader.ViewModels;
using PdfSharp.Drawing;
using PdfSharp.Drawing.Layout;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;
using PdfIO = PdfSharp.Pdf.IO;

namespace PdfReader.Services;

/// <summary>
/// Writes the session's annotations into a copy of the PDF using PDFsharp.
/// Ink, highlights, text boxes, and signatures are drawn into the page
/// content (flattened) in append mode, so they render identically in every
/// viewer. Comments become real PDF text annotations, so they pop up as
/// sticky notes in Acrobat, Edge, and friends. XGraphics.FromPdfPage uses a
/// top-left origin, matching our stored geometry up to a linear scale that is
/// derived from the actual page size (no unit assumptions).
/// </summary>
public static class PdfSaveService
{
    private const byte HighlightAlpha = 115; // ~45% — text stays readable underneath

    public static Task SaveAsync(DocumentViewModel doc, string targetPath)
    {
        // Snapshot on the UI thread; ObservableCollection isn't thread-safe.
        var pages = doc.Pages
            .Where(p => p.Annotations.Count > 0)
            .Select(p => (p.Index, p.BaseWidth, p.BaseHeight, Annotations: p.Annotations.ToList()))
            .ToList();
        byte[] bytes = doc.SourceBytes;

        return Task.Run(() =>
        {
            using var input = new MemoryStream(bytes, writable: false);
            using var pdf = PdfIO.PdfReader.Open(input, PdfIO.PdfDocumentOpenMode.Modify);

            XImage? signatureImage = null;
            try
            {
                foreach (var (index, baseWidth, baseHeight, annotations) in pages)
                {
                    var page = pdf.Pages[(int)index];

                    // Scale from overlay coordinates to this page's point size.
                    double sx = page.Width.Point / baseWidth;
                    double sy = page.Height.Point / baseHeight;

                    using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                    foreach (var annotation in annotations)
                    {
                        Draw(gfx, page, annotation, sx, sy, ref signatureImage);
                    }
                }

                pdf.Save(targetPath);
            }
            finally
            {
                signatureImage?.Dispose();
            }
        });
    }

    private static void Draw(
        XGraphics gfx,
        PdfPage page,
        AnnotationBase annotation,
        double sx,
        double sy,
        ref XImage? signatureImage)
    {
        switch (annotation)
        {
            case HighlightAnnotation highlight:
            {
                var brush = new XSolidBrush(XColor.FromArgb(
                    HighlightAlpha, highlight.Color.R, highlight.Color.G, highlight.Color.B));
                foreach (var r in highlight.Rects)
                {
                    gfx.DrawRectangle(brush, r.X * sx, r.Y * sy, r.Width * sx, r.Height * sy);
                }

                break;
            }

            case TextBoxAnnotation text when !string.IsNullOrWhiteSpace(text.Text):
            {
                var font = new XFont("Arial", text.FontSize * sy, XFontStyleEx.Regular);
                var brush = new XSolidBrush(XColor.FromArgb(
                    255, text.Color.R, text.Color.G, text.Color.B));

                // Line height approximates the overlay TextBlock's spacing.
                double lineHeight = text.FontSize * sy * 1.33;
                string[] lines = text.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    gfx.DrawString(
                        lines[i],
                        font,
                        brush,
                        new XPoint(text.Position.X * sx, text.Position.Y * sy + i * lineHeight),
                        XStringFormats.TopLeft);
                }

                break;
            }

            case StickyNoteAnnotation sticky when !string.IsNullOrWhiteSpace(sticky.Text):
            {
                const double pad = 8;
                double x = sticky.Position.X * sx;
                double y = sticky.Position.Y * sy;
                double w = sticky.Width * sx;
                double h = sticky.RenderSize.Height * sy;

                // Yellow card with a black border, matching the on-screen note.
                gfx.DrawRectangle(
                    new XSolidBrush(XColor.FromArgb(255, 0xFF, 0xE0, 0x2B)),
                    x, y, w, h);
                gfx.DrawRectangle(new XPen(XColors.Black, 1.5 * sx), x, y, w, h);

                var font = new XFont("Arial", sticky.FontSize * sy, XFontStyleEx.Regular);
                var brush = new XSolidBrush(XColor.FromArgb(
                    255, sticky.Color.R, sticky.Color.G, sticky.Color.B));
                var formatter = new XTextFormatter(gfx);
                formatter.DrawString(
                    sticky.Text,
                    font,
                    brush,
                    new XRect(x + pad * sx, y + pad * sy, w - 2 * pad * sx, h - 2 * pad * sy),
                    XStringFormats.TopLeft);
                break;
            }

            case CommentAnnotation comment:
            {
                // A real PDF sticky-note annotation — opens as a popup note in
                // other viewers instead of being burned into the page.
                var note = new PdfTextAnnotation
                {
                    Title = "Comment",
                    Contents = comment.Text ?? string.Empty,
                    Icon = PdfTextAnnotationIcon.Comment,
                };
                var world = new XRect(
                    comment.Position.X * sx,
                    comment.Position.Y * sy,
                    CommentAnnotation.IconSize * sx,
                    CommentAnnotation.IconSize * sx);
                note.Rectangle = new PdfRectangle(gfx.Transformer.WorldToDefaultPage(world));
                page.Annotations.Add(note);
                break;
            }

            case SignatureAnnotation signature when File.Exists(SignatureStore.ImagePath):
            {
                signatureImage ??= XImage.FromFile(SignatureStore.ImagePath);
                var b = signature.Bounds;
                gfx.DrawImage(
                    signatureImage,
                    new XRect(b.X * sx, b.Y * sy, b.Width * sx, b.Height * sy));
                break;
            }

            case InkAnnotation ink when ink.Points.Count > 0:
            {
                double width = Math.Max(0.5, ink.Thickness * sx);
                var color = XColor.FromArgb(255, ink.Color.R, ink.Color.G, ink.Color.B);

                if (ink.Points.Count == 1)
                {
                    var p = ink.Points[0];
                    gfx.DrawEllipse(
                        new XSolidBrush(color),
                        p.X * sx - width / 2,
                        p.Y * sy - width / 2,
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
                    ink.Points.Select(p => new XPoint(p.X * sx, p.Y * sy)).ToArray());
                break;
            }
        }
    }
}
