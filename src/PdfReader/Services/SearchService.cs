using System.Text;
using PdfReader.Models;
using Windows.Foundation;

namespace PdfReader.Services;

/// <summary>
/// One hit: the page it's on and the rectangles covering it (one per text
/// line, so a match wrapping across lines highlights correctly). Rectangles are
/// normalized to the page (0..1), matching how word geometry is carried.
/// </summary>
public sealed record SearchMatch(uint PageIndex, IReadOnlyList<Rect> Rects);

/// <summary>
/// Finds a query inside a page's extracted words. Words are joined into one
/// searchable string with an index back to the word they came from, so phrases
/// spanning several words ("cold storage") match and map onto the right boxes.
/// </summary>
public static class SearchService
{
    public static List<SearchMatch> FindInPage(
        uint pageIndex,
        IReadOnlyList<WordBox> words,
        string query,
        bool matchCase,
        bool wholeWord)
    {
        var results = new List<SearchMatch>();
        if (words.Count == 0 || string.IsNullOrEmpty(query))
        {
            return results;
        }

        var builder = new StringBuilder();
        var spans = new List<(int Start, int End, int WordIndex)>(words.Count);
        for (int i = 0; i < words.Count; i++)
        {
            int start = builder.Length;
            builder.Append(words[i].Text);
            spans.Add((start, builder.Length, i));
            if (i < words.Count - 1)
            {
                builder.Append(' '); // the separator a phrase query will contain
            }
        }

        string haystack = builder.ToString();
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        int searchFrom = 0;
        while (searchFrom <= haystack.Length - query.Length)
        {
            int at = haystack.IndexOf(query, searchFrom, comparison);
            if (at < 0)
            {
                break;
            }

            int end = at + query.Length;
            searchFrom = at + 1; // allow overlapping starts

            if (wholeWord && !IsWholeWord(haystack, at, end))
            {
                continue;
            }

            // Every word the match touches.
            var matched = new List<WordBox>();
            foreach (var (start, wordEnd, wordIndex) in spans)
            {
                if (start < end && at < wordEnd)
                {
                    matched.Add(words[wordIndex]);
                }
            }

            if (matched.Count == 0)
            {
                continue;
            }

            var rects = AnnotationGeometry.GroupIntoLines(matched)
                .Select(AnnotationGeometry.MergeLine)
                .ToList();
            results.Add(new SearchMatch(pageIndex, rects));
        }

        return results;
    }

    private static bool IsWholeWord(string text, int start, int end)
    {
        bool leftOk = start == 0 || !char.IsLetterOrDigit(text[start - 1]);
        bool rightOk = end >= text.Length || !char.IsLetterOrDigit(text[end]);
        return leftOk && rightOk;
    }
}

/// <summary>
/// Shared search results. The per-page highlight layers read from this and
/// redraw when it changes — a singleton for the same reason ToolState is one:
/// the page overlays are created in code and need to reach it without plumbing.
/// </summary>
public sealed class SearchState
{
    public static SearchState Current { get; } = new();

    private readonly List<SearchMatch> _matches = new();

    public IReadOnlyList<SearchMatch> Matches => _matches;

    /// <summary>Index into <see cref="Matches"/> of the highlighted hit, or -1.</summary>
    public int CurrentIndex { get; private set; } = -1;

    public bool IsActive { get; private set; }

    /// <summary>Raised when results or the current hit change; layers redraw.</summary>
    public event Action? ResultsChanged;

    public void BeginSearch()
    {
        _matches.Clear();
        CurrentIndex = -1;
        IsActive = true;
        ResultsChanged?.Invoke();
    }

    public void AddMatches(IEnumerable<SearchMatch> matches)
    {
        _matches.AddRange(matches);
        ResultsChanged?.Invoke();
    }

    public void SetCurrentIndex(int index)
    {
        if (CurrentIndex == index)
        {
            return;
        }

        CurrentIndex = index;
        ResultsChanged?.Invoke();
    }

    public void Clear()
    {
        _matches.Clear();
        CurrentIndex = -1;
        IsActive = false;
        ResultsChanged?.Invoke();
    }

    /// <summary>Matches on a page, paired with their global index (for current-hit colouring).</summary>
    public IEnumerable<(int Index, SearchMatch Match)> MatchesOnPage(uint pageIndex)
    {
        for (int i = 0; i < _matches.Count; i++)
        {
            if (_matches[i].PageIndex == pageIndex)
            {
                yield return (i, _matches[i]);
            }
        }
    }
}
