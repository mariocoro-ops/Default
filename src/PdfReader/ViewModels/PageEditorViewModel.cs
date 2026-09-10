using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace PdfReader.ViewModels;

/// <summary>
/// The page editor's document. Deliberately kept at arm's length from
/// <see cref="DocumentViewModel"/>: it owns its own copy of the bytes and its
/// own renderer, so nothing it does can disturb the viewer's document, its
/// annotations, or the caches keyed off the viewer's page indices.
///
/// Every edit is "produce new bytes, then rebuild": there is no in-place
/// mutation to get subtly wrong. That is affordable precisely because nothing
/// here needs to survive a rebuild — no annotations, no scroll position, no
/// text-geometry or link caches.
/// </summary>
public sealed class PageEditorViewModel : INotifyPropertyChanged, IDisposable
{
    // Enough to undo a bad afternoon; bounded because each entry is a whole
    // copy of the document.
    private const int MaxUndoDepth = 20;

    private readonly List<byte[]> _undo = new();
    private readonly double _rasterizationScale;

    private byte[] _bytes;

    // The bytes the document was last saved at (its original content to begin
    // with). Compared by reference — undo restores the very same array — so
    // undoing back to the start correctly clears the modified flag.
    private byte[] _savedBytes;

    private InMemoryRandomAccessStream? _renderStream;
    private bool _isModified;
    private bool _busy;

    private PageEditorViewModel(byte[] bytes, string sourceName, double rasterizationScale)
    {
        _bytes = bytes;
        _savedBytes = bytes;
        _rasterizationScale = rasterizationScale;
        SourceName = sourceName;
    }

    /// <summary>Name of the file the editor started from, for the save dialog's suggestion.</summary>
    public string SourceName { get; }

    public ObservableCollection<PageThumbViewModel> Pages { get; } = new();

    /// <summary>The current document bytes — what a save writes out.</summary>
    public byte[] Bytes => _bytes;

    public int PageCount => Pages.Count;

    /// <summary>True once an edit has been made and not yet saved.</summary>
    public bool IsModified
    {
        get => _isModified;
        private set
        {
            if (_isModified == value)
            {
                return;
            }

            _isModified = value;
            OnPropertyChanged();
        }
    }

    /// <summary>True while an edit is being applied; the UI disables itself.</summary>
    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (_busy == value)
            {
                return;
            }

            _busy = value;
            OnPropertyChanged();
        }
    }

    public bool CanUndo => _undo.Count > 0;

    public static async Task<PageEditorViewModel> CreateAsync(
        byte[] bytes,
        string sourceName,
        double rasterizationScale)
    {
        // Copy: the viewer's SourceBytes must never be handed out for editing.
        var vm = new PageEditorViewModel((byte[])bytes.Clone(), sourceName, rasterizationScale);
        await vm.RebuildAsync();
        return vm;
    }

    /// <summary>
    /// Runs a page operation and adopts its result, recording the previous
    /// bytes for undo. If the operation throws, nothing changes.
    /// </summary>
    public async Task ApplyAsync(Func<byte[], Task<byte[]>> operation)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            byte[] previous = _bytes;
            byte[] next = await operation(previous);

            // An operation that had nothing to do shouldn't cost an undo slot.
            if (ReferenceEquals(next, previous))
            {
                return;
            }

            PushUndo(previous);
            _bytes = next;
            await RebuildAsync();
            IsModified = !ReferenceEquals(_bytes, _savedBytes);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task UndoAsync()
    {
        if (IsBusy || _undo.Count == 0)
        {
            return;
        }

        IsBusy = true;
        try
        {
            _bytes = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            await RebuildAsync();

            OnPropertyChanged(nameof(CanUndo));

            // Undone all the way back to the saved state: nothing left to save.
            IsModified = !ReferenceEquals(_bytes, _savedBytes);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Rebuilds the thumbnail list from the current bytes without recording an
    /// undo step. Used to put the grid back in sync after an edit failed part
    /// way through — for instance a drag that reordered the cards before the
    /// rewrite was rejected.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await RebuildAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Marks the current state as saved without altering the document.</summary>
    public void MarkSaved()
    {
        _savedBytes = _bytes;
        IsModified = false;
    }

    /// <summary>
    /// Reloads the renderer and the thumbnail list from the current bytes. The
    /// old thumbnails are discarded wholesale — nothing about them is worth
    /// carrying across, which is what makes this safe.
    /// </summary>
    private async Task RebuildAsync()
    {
        ReleaseRenderer();

        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(_bytes.AsBuffer());
        stream.Seek(0);

        // The thumbnails hold the document; this class only has to keep the
        // stream behind it alive.
        var document = await PdfDocument.LoadFromStreamAsync(stream);
        _renderStream = stream;

        Pages.Clear();
        for (uint i = 0; i < document.PageCount; i++)
        {
            Pages.Add(new PageThumbViewModel(document, i, (int)i, _rasterizationScale));
        }

        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(CanUndo));
    }

    /// <summary>
    /// The page order the grid is currently showing, expressed as indices into
    /// the working document — i.e. exactly the permutation a reorder needs.
    /// </summary>
    public IReadOnlyList<int> CurrentOrder() => Pages.Select(p => p.Position).ToList();

    /// <summary>Renumbers the thumbnails after a drag so the labels read 1..n again.</summary>
    public void RenumberInPlace()
    {
        for (int i = 0; i < Pages.Count; i++)
        {
            Pages[i].Position = i;
        }
    }

    private void PushUndo(byte[] snapshot)
    {
        _undo.Add(snapshot);
        if (_undo.Count > MaxUndoDepth)
        {
            _undo.RemoveAt(0);
        }

        OnPropertyChanged(nameof(CanUndo));
    }

    private void ReleaseRenderer()
    {
        _renderStream?.Dispose();
        _renderStream = null;
    }

    public void Dispose()
    {
        Pages.Clear();
        _undo.Clear();
        ReleaseRenderer();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
