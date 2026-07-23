using Windows.Foundation;
using Windows.UI;

namespace PdfReader.Models;

/// <summary>
/// Base type for all in-session annotations. Geometry is stored in page
/// coordinates at 100% zoom (DIPs, top-left origin), so it is independent of
/// the current zoom level and maps 1:1 onto PDF points at save time.
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

/// <summary>Geometry helpers shared by the overlay and the eraser.</summary>
public static class AnnotationGeometry
{
    /// <summary>Merges the selected word boxes into one rectangle per text line.</summary>
    public static List<Rect> MergeIntoLines(IReadOnlyList<Rect> words)
    {
        var lines = new List<List<Rect>>();
        foreach (var word in words.OrderBy(w => w.Top + w.Height / 2).ThenBy(w => w.Left))
        {
            double centerY = word.Top + word.Height / 2;
            var line = lines.LastOrDefault(l =>
            {
                var first = l[0];
                double lineCenterY = first.Top + first.Height / 2;
                return Math.Abs(lineCenterY - centerY) < Math.Max(first.Height, word.Height) * 0.6;
            });

            if (line is null)
            {
                lines.Add(new List<Rect> { word });
            }
            else
            {
                line.Add(word);
            }
        }

        return lines
            .Select(l =>
            {
                double left = l.Min(r => r.Left);
                double top = l.Min(r => r.Top);
                double right = l.Max(r => r.Right);
                double bottom = l.Max(r => r.Bottom);
                return new Rect(left, top, right - left, bottom - top);
            })
            .ToList();
    }

    public static bool Intersects(Rect a, Rect b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    /// <summary>Hit test used by the eraser tool.</summary>
    public static bool HitTest(AnnotationBase annotation, Point p)
    {
        switch (annotation)
        {
            case HighlightAnnotation highlight:
                return highlight.Rects.Any(r =>
                    p.X >= r.Left - 2 && p.X <= r.Right + 2 &&
                    p.Y >= r.Top - 2 && p.Y <= r.Bottom + 2);

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
