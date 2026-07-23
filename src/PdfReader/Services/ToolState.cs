using System.ComponentModel;
using PdfReader.Models;
using PdfReader.ViewModels;
using Windows.Foundation;
using Windows.UI;

namespace PdfReader.Services;

public enum AnnotationTool
{
    /// <summary>No annotation tool active — normal scrolling/selection.</summary>
    None,
    Draw,
    Highlight,
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

    /// <summary>
    /// Supplies word bounding boxes for a page index, in reading order and
    /// normalized to the page (0..1, top-left origin), used for text-aware
    /// highlighting. Null or an empty result means the highlight falls back
    /// to the raw dragged rectangle.
    /// </summary>
    public Func<uint, Task<IReadOnlyList<Rect>>>? WordProvider { get; set; }

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
