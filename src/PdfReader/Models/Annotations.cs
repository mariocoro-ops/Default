using Windows.Foundation;
using Windows.UI;

namespace PdfReader.Models;

/// <summary>
/// A word on a page: its bounding box and its text. From the geometry service
/// the bounds are normalized (0..1, top-left origin); inside the overlay they
/// are scaled to page DIPs at 100% zoom. Order follows reading order.
/// </summary>
public readonly record struct WordBox(Rect Bounds, string Text);

/// <summary>
/// Base type for all in-session annotations. Geometry is stored in page
/// coordinates at 100% zoom (DIPs, top-left origin), so it is independent of
/// the current zoom level and maps onto PDF points at save time.
/// </summary>
public abstract class AnnotationBase
{
    public Guid Id { get; } = Guid.NewGuid();
}

/// <summary>A freehand pen stroke.</summary>
public sealed class InkAnnotation : AnnotationBase
{
    public List<Point> Points { get; } = new();

    public Color Color { get; set; }

    public double Thickness { get; set; } = 3.0;
}

/// <summary>
/// A highlight: one rectangle per text line (text-aware), or a single
/// freeform rectangle on pages without extractable text (e.g. scans).
/// </summary>
public sealed class HighlightAnnotation : AnnotationBase
{
    public List<Rect> Rects { get; } = new();

    public Color Color { get; set; }
}

/// <summary>A typed text box placed on the page.</summary>
public sealed class TextBoxAnnotation : AnnotationBase
{
    public Point Position { get; set; }

    public string Text { get; set; } = string.Empty;

    public double FontSize { get; set; } = 16.0;

    public Color Color { get; set; }

    /// <summary>Measured display size (base DIPs); set at render time, used for hit tests and dragging.</summary>
    public Size RenderSize { get; set; } = new(120, 24);
}

/// <summary>A sticky-note comment anchored to a point on the page.</summary>
public sealed class CommentAnnotation : AnnotationBase
{
    public const double IconSize = 22.0;

    public Point Position { get; set; }

    public string Text { get; set; } = string.Empty;
}

/// <summary>The user's pre-saved signature image stamped onto the page.</summary>
public sealed class SignatureAnnotation : AnnotationBase
{
    public Rect Bounds { get; set; }
}

/// <summary>Geometry helpers shared by the overlay tools.</summary>
public static class AnnotationGeometry
{
    /// <summary>
    /// Groups a reading-order run of words into visual lines. A new line
    /// starts when a word's vertical center jumps away from the current
    /// line's first word.
    /// </summary>
    public static List<List<WordBox>> GroupIntoLines(IReadOnlyList<WordBox> words)
    {
        var lines = new List<List<WordBox>>();
        foreach (var word in words)
        {
            var line = lines.Count > 0 ? lines[^1] : null;
            if (line is not null && SameLine(line[0].Bounds, word.Bounds))
            {
                line.Add(word);
            }
            else
            {
                lines.Add(new List<WordBox> { word });
            }
        }

        return lines;
    }

    private static bool SameLine(Rect a, Rect b)
    {
        double centerA = a.Top + a.Height / 2;
        double centerB = b.Top + b.Height / 2;
        return Math.Abs(centerA - centerB) < Math.Max(a.Height, b.Height) * 0.6;
    }

    /// <summary>One bounding rectangle for a line of words.</summary>
    public static Rect MergeLine(IReadOnlyList<WordBox> line)
    {
        double left = line.Min(w => w.Bounds.Left);
        double top = line.Min(w => w.Bounds.Top);
        double right = line.Max(w => w.Bounds.Right);
        double bottom = line.Max(w => w.Bounds.Bottom);
        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>The plain text of a grouped selection, for the clipboard.</summary>
    public static string BuildText(IReadOnlyList<List<WordBox>> lines) =>
        string.Join(
            Environment.NewLine,
            lines.Select(line => string.Join(" ", line.Select(w => w.Text))));

    /// <summary>Hit test used by the eraser tool.</summary>
    public static bool HitTest(AnnotationBase annotation, Point p)
    {
        switch (annotation)
        {
            case HighlightAnnotation highlight:
                return highlight.Rects.Any(r => Contains(r, p, 2));

            case TextBoxAnnotation text:
                return Contains(
                    new Rect(
                        text.Position.X,
                        text.Position.Y,
                        Math.Max(40, text.RenderSize.Width),
                        Math.Max(20, text.RenderSize.Height)),
                    p,
                    2);

            case CommentAnnotation comment:
                return Contains(
                    new Rect(comment.Position.X, comment.Position.Y, CommentAnnotation.IconSize, CommentAnnotation.IconSize),
                    p,
                    2);

            case SignatureAnnotation signature:
                return Contains(signature.Bounds, p, 2);

            case InkAnnotation ink:
                double threshold = Math.Max(4, ink.Thickness / 2 + 3);
                if (ink.Points.Count == 1)
                {
                    return Distance(ink.Points[0], p) <= threshold;
                }

                for (int i = 0; i < ink.Points.Count - 1; i++)
                {
                    if (DistanceToSegment(p, ink.Points[i], ink.Points[i + 1]) <= threshold)
                    {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }

    public static bool Contains(Rect r, Point p, double pad) =>
        p.X >= r.Left - pad && p.X <= r.Right + pad &&
        p.Y >= r.Top - pad && p.Y <= r.Bottom + pad;

    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 1e-9)
        {
            return Distance(p, a);
        }

        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
        return Distance(p, new Point(a.X + t * dx, a.Y + t * dy));
    }
}
