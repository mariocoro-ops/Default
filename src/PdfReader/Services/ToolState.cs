using System.ComponentModel;
using PdfReader.Models;
using PdfReader.ViewModels;
using Windows.UI;

namespace PdfReader.Services;

public enum AnnotationTool
{
    /// <summary>No annotation tool active — normal scrolling.</summary>
    None,

    /// <summary>Grab-and-drag panning of the document.</summary>
    Hand,

    /// <summary>Select text with the pointer; Ctrl+C copies it.</summary>
    TextSelect,
    Draw,
    Highlight,

    /// <summary>Place / edit typed text boxes.</summary>
    Text,

    /// <summary>Place / edit sticky-note comments.</summary>
    Comment,

    /// <summary>Stamp the pre-saved signature image.</summary>
    Signature,
    Erase,
}

/// <summary>
/// Shared state for the annotation tools. A singleton so the per-page overlay
/// canvases (created inside a DataTemplate) can reach it without plumbing
/// bindings through the item template.
/// </summary>
public sealed class ToolState : INotifyPropertyChanged
{
    public static ToolState Current { get; } = new();

    private AnnotationTool _tool = AnnotationTool.None;

    public AnnotationTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value)
            {
                return;
            }

            _tool = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tool)));
        }
    }

    public Color PenColor { get; set; } = Color.FromArgb(255, 0xE5, 0x39, 0x35);

    public double PenThickness { get; set; } = 3.0;

    public Color HighlightColor { get; set; } = Color.FromArgb(255, 0xFF, 0xEB, 0x3B);

    /// <summary>Font size for new text boxes (base DIPs).</summary>
    public double FontSize { get; set; } = 16.0;

    /// <summary>
    /// Supplies word boxes (with text) for a page index, in reading order and
    /// normalized to the page (0..1, top-left origin). Null or an empty result
    /// means highlights fall back to the raw dragged rectangle and text
    /// selection is unavailable on that page.
    /// </summary>
    public Func<uint, Task<IReadOnlyList<WordBox>>>? WordProvider { get; set; }

    /// <summary>
    /// Supplies clickable link regions for a page index, normalized to the
    /// page. Null or empty means the page has no navigable links.
    /// </summary>
    public Func<uint, Task<IReadOnlyList<PdfLink>>>? LinkProvider { get; set; }

    // ------------------------------------------------------------ text selection

    /// <summary>The canvas that currently owns the text selection, if any.</summary>
    public object? SelectionOwner { get; private set; }

    /// <summary>The selected text, ready for the clipboard.</summary>
    public string SelectedText { get; private set; } = string.Empty;

    /// <summary>Raised when selection ownership moves; canvases that are not the new owner clear their visuals.</summary>
    public event Action<object?>? SelectionOwnerChanged;

    public void ClaimSelection(object owner)
    {
        SelectedText = string.Empty;
        if (!ReferenceEquals(SelectionOwner, owner))
        {
            SelectionOwner = owner;
            SelectionOwnerChanged?.Invoke(owner);
        }
    }

    public void SetSelectedText(string text) => SelectedText = text;

    public void ClearSelection()
    {
        SelectedText = string.Empty;
        if (SelectionOwner is not null)
        {
            SelectionOwner = null;
            SelectionOwnerChanged?.Invoke(null);
        }
    }

    // ------------------------------------------------------------ hand-tool panning

    // The overlay reports pointer deltas (in window coordinates, so they are
    // unaffected by the scrolling they drive); the window translates them into
    // scroll offsets. Kept here so the per-page canvases can reach the single
    // ScrollViewer without a direct reference.
    public event Action? PanStarted;

    public event Action<double, double>? PanUpdated;

    public void NotifyPanStarted() => PanStarted?.Invoke();

    public void NotifyPanUpdated(double dx, double dy) => PanUpdated?.Invoke(dx, dy);

    // ------------------------------------------------------------ link navigation

    /// <summary>Raised when an internal link is clicked: (target page index, 0..1 vertical fraction).</summary>
    public event Action<int, double>? NavigateToPageRequested;

    /// <summary>Raised when an external link is clicked: the absolute URI.</summary>
    public event Action<string>? OpenUriRequested;

    public void RequestNavigateToPage(int pageIndex, double topFraction) =>
        NavigateToPageRequested?.Invoke(pageIndex, topFraction);

    public void RequestOpenUri(string uri) => OpenUriRequested?.Invoke(uri);

    // ------------------------------------------------------------ sticky notes

    /// <summary>
    /// Raised to drop a new post-it note: (fallback page index, pointer position
    /// in window coordinates, isFallbackPhase). Phase one lets the page under
    /// the pointer place the note at the cursor; if none claims it, phase two
    /// has the fallback page center it.
    /// </summary>
    public event Action<uint, Windows.Foundation.Point, bool>? AddNoteRequested;

    /// <summary>Set by the canvas that placed the note, ending the request.</summary>
    public bool NoteRequestClaimed { get; private set; }

    public void ClaimNoteRequest() => NoteRequestClaimed = true;

    public void RequestAddNote(uint fallbackPageIndex, Windows.Foundation.Point pointerInWindow)
    {
        NoteRequestClaimed = false;
        AddNoteRequested?.Invoke(fallbackPageIndex, pointerInWindow, false);
        if (!NoteRequestClaimed)
        {
            AddNoteRequested?.Invoke(fallbackPageIndex, pointerInWindow, true);
        }
    }

    // ------------------------------------------------------------ edit notifications

    // Raised by the overlay canvas on user edits so the window can maintain
    // the undo stack and the modified flag.
    public event Action<PageViewModel, AnnotationBase>? AnnotationAdded;

    public event Action<PageViewModel, AnnotationBase>? AnnotationRemoved;

    public void NotifyAnnotationAdded(PageViewModel page, AnnotationBase annotation) =>
        AnnotationAdded?.Invoke(page, annotation);

    public void NotifyAnnotationRemoved(PageViewModel page, AnnotationBase annotation) =>
        AnnotationRemoved?.Invoke(page, annotation);

    public event PropertyChangedEventHandler? PropertyChanged;
}
