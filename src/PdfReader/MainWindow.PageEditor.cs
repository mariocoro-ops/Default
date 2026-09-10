using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PdfReader.Services;
using PdfReader.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PdfReader;

/// <summary>
/// The Pages tab: delete, reorder and merge pages.
///
/// It shares the window with the viewer but not its document. The editor holds
/// its own copy of the bytes and its own renderer, and hands results back only
/// by saving a new file that the viewer then opens through the ordinary open
/// path. Nothing here reaches into the viewer's pages, annotations, text
/// geometry, link or search caches — which is what keeps the viewer's hard-won
/// navigation behaviour out of the blast radius.
/// </summary>
public sealed partial class MainWindow
{
    private PageEditorViewModel? _editor;

    /// <summary>The document the editor was built from, so switching tabs doesn't discard edits.</summary>
    private DocumentViewModel? _editorSource;

    /// <summary>True while the Pages tab is showing. Gates every viewer shortcut.</summary>
    private bool _pagesTab;

    /// <summary>
    /// Guards against a second dialog while one is up — WinUI allows only one
    /// ContentDialog at a time, and holding Delete would otherwise try to open
    /// a confirmation per repeat.
    /// </summary>
    private bool _editorDialogOpen;

    private async Task<ContentDialogResult> ShowEditorDialogAsync(ContentDialog dialog)
    {
        if (_editorDialogOpen)
        {
            return ContentDialogResult.None;
        }

        _editorDialogOpen = true;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _editorDialogOpen = false;
        }
    }

    /// <summary>
    /// Called before opening a different file. Unsaved page edits live only in
    /// the editor's working copy, so say so before they're dropped. Returns
    /// false to abandon the open.
    /// </summary>
    private async Task<bool> ConfirmDiscardPageEditsAsync()
    {
        if (_editor?.IsModified != true)
        {
            return true;
        }

        var dialog = new ContentDialog
        {
            Title = "Discard your page changes?",
            Content = "The Pages tab has edits you haven't saved. Opening another file discards them.",
            PrimaryButtonText = "Discard and open",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot,
        };

        return await ShowEditorDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    // ---------------------------------------------------------------- tab switching

    private void ViewerTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_pagesTab)
        {
            ApplyTabVisibility(); // re-check the toggle the click just cleared
            return;
        }

        _pagesTab = false;
        ApplyTabVisibility();
        Scroller.Focus(FocusState.Programmatic);
    }

    private async void PagesTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pagesTab)
        {
            ApplyTabVisibility();
            return;
        }

        await SwitchToPagesAsync();
    }

    private async Task SwitchToPagesAsync()
    {
        if (_doc is null || _presenting)
        {
            ApplyTabVisibility();
            return;
        }

        // The editor works from the document's bytes, which do not contain
        // annotations that haven't been saved yet. Say so before they vanish.
        if (_isModified)
        {
            var answer = await AskAboutUnsavedAnnotationsAsync();
            if (answer == ContentDialogResult.None)
            {
                ApplyTabVisibility();
                return;
            }

            if (answer == ContentDialogResult.Primary)
            {
                var saved = await SaveCopyAsync();
                if (saved is null)
                {
                    ApplyTabVisibility(); // save cancelled or failed — stay put
                    return;
                }

                // Reopen the saved copy so the viewer and the editor are
                // working on the same document: the annotations are now part
                // of the file, not pending state.
                await OpenFileAsync(saved);
                if (_doc is null)
                {
                    ApplyTabVisibility();
                    return;
                }
            }
        }

        if (!await EnsureEditorAsync())
        {
            ApplyTabVisibility();
            return;
        }

        _pagesTab = true;
        ApplyTabVisibility();
        PagesGrid.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Asks what to do about annotations that aren't in the file yet. Primary =
    /// save first, Secondary = go anyway, None = stay on the viewer.
    /// </summary>
    private async Task<ContentDialogResult> AskAboutUnsavedAnnotationsAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "You have unsaved annotations",
            Content =
                "The Pages tab edits the document itself, so highlights, notes and drawings "
                + "you haven't saved yet won't be carried over.\n\n"
                + "Saving first creates a new PDF with the annotations in it, and the Pages "
                + "tab picks up from there.",
            PrimaryButtonText = "Save annotations first",
            SecondaryButtonText = "Continue without saving",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };

        return await ShowEditorDialogAsync(dialog);
    }

    /// <summary>Builds the editor for the current document, reusing it if it already matches.</summary>
    private async Task<bool> EnsureEditorAsync()
    {
        if (_doc is null)
        {
            return false;
        }

        // Same document as last time: keep the editor and any edits in it.
        if (_editor is not null && ReferenceEquals(_editorSource, _doc))
        {
            return true;
        }

        SetEditorBusy(true);
        try
        {
            DetachEditor();

            _editor = await PageEditorViewModel.CreateAsync(
                _doc.SourceBytes,
                _doc.FileName,
                Root.XamlRoot?.RasterizationScale ?? 1.0);
            _editor.PropertyChanged += Editor_PropertyChanged;
            _editorSource = _doc;

            PagesGrid.ItemsSource = _editor.Pages;
            return true;
        }
        catch
        {
            DetachEditor();
            await ShowErrorAsync(
                "Couldn't open the page editor",
                "This PDF's pages could not be read for editing. It may be password-protected "
                + "or use features PDFsharp can't rewrite.");
            return false;
        }
        finally
        {
            SetEditorBusy(false);
        }
    }

    /// <summary>Drops the editor when a different document is opened in the viewer.</summary>
    private void ResetEditorForNewDocument()
    {
        DetachEditor();
        _pagesTab = false;
        ApplyTabVisibility();
    }

    private void DetachEditor()
    {
        if (_editor is not null)
        {
            _editor.PropertyChanged -= Editor_PropertyChanged;
            _editor.Dispose();
        }

        _editor = null;
        _editorSource = null;
        PagesGrid.ItemsSource = null;
    }

    private void ApplyTabVisibility()
    {
        ViewerTabButton.IsChecked = !_pagesTab;
        PagesTabButton.IsChecked = _pagesTab;

        var viewer = _pagesTab ? Visibility.Collapsed : Visibility.Visible;
        var editor = _pagesTab ? Visibility.Visible : Visibility.Collapsed;

        DocumentHost.Visibility = viewer;
        ViewerTools.Visibility = viewer;
        ViewerNav.Visibility = viewer;
        ViewerViewTools.Visibility = viewer;

        EditorHost.Visibility = editor;
        EditorTools.Visibility = editor;
        EditorStatusText.Visibility = editor;

        if (_pagesTab)
        {
            // Viewer state that shouldn't survive the switch.
            CloseFind();
            ClearGoto();
        }

        UpdateEditorCommands();
        UpdateTitle();
    }

    private void Editor_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PageEditorViewModel.IsBusy))
        {
            SetEditorBusy(_editor?.IsBusy == true);
            return;
        }

        UpdateEditorCommands();
        if (e.PropertyName == nameof(PageEditorViewModel.IsModified))
        {
            UpdateTitle();
        }
    }

    private void SetEditorBusy(bool busy) =>
        EditorBusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateEditorCommands()
    {
        if (!_pagesTab || _editor is null)
        {
            DeletePagesButton.IsEnabled = false;
            MovePageLeftButton.IsEnabled = false;
            MovePageRightButton.IsEnabled = false;
            EditorUndoButton.IsEnabled = false;
            return;
        }

        var selected = SelectedPositions();
        int count = _editor.PageCount;
        bool any = selected.Count > 0;

        DeletePagesButton.IsEnabled = any && selected.Count < count;
        MovePageLeftButton.IsEnabled = any && selected[0] > 0;
        MovePageRightButton.IsEnabled = any && selected[^1] < count - 1;
        EditorUndoButton.IsEnabled = _editor.CanUndo;

        EditorStatusText.Text = any
            ? $"{selected.Count} of {count} page{(count == 1 ? "" : "s")} selected"
            : $"{count} page{(count == 1 ? "" : "s")} — select to delete, drag to reorder";
    }

    /// <summary>Selected page positions in the working document, ascending.</summary>
    private List<int> SelectedPositions() =>
        PagesGrid.SelectedItems
            .OfType<PageThumbViewModel>()
            .Select(p => p.Position)
            .OrderBy(p => p)
            .ToList();

    // ---------------------------------------------------------------- grid events

    private void PagesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateEditorCommands();

    private void PagesGrid_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
        {
            return;
        }

        // Thumbnails rasterize as their cards come into view, so a long deck
        // opens immediately instead of rendering every page up front.
        if (args.Item is PageThumbViewModel thumb)
        {
            _ = thumb.EnsureRenderedAsync();
        }
    }

    private async void PagesGrid_DragItemsCompleted(
        ListViewBase sender,
        DragItemsCompletedEventArgs args)
    {
        if (_editor is null || args.DropResult != DataPackageOperation.Move)
        {
            return;
        }

        // The grid has already reordered the items themselves, and each one
        // still carries the position it held before the drag — so reading them
        // off in their new order gives exactly the permutation to apply.
        var order = _editor.CurrentOrder();
        _editor.RenumberInPlace();

        await RunEditorOperationAsync(
            bytes => PdfPageEditService.ReorderAsync(bytes, order),
            "Couldn't reorder the pages");
    }

    // ---------------------------------------------------------------- commands

    private async void DeletePagesButton_Click(object sender, RoutedEventArgs e) =>
        await DeleteSelectedPagesAsync();

    private async Task DeleteSelectedPagesAsync()
    {
        if (_editor is null || _editor.IsBusy)
        {
            return;
        }

        var selected = SelectedPositions();
        if (selected.Count == 0)
        {
            return;
        }

        if (selected.Count >= _editor.PageCount)
        {
            await ShowErrorAsync(
                "Can't delete every page",
                "A PDF has to keep at least one page. Leave one unselected.");
            return;
        }

        var confirm = new ContentDialog
        {
            Title = selected.Count == 1
                ? $"Delete page {selected[0] + 1}?"
                : $"Delete {selected.Count} pages?",
            Content = "This changes the working copy only — the file on disk isn't touched "
                + "until you save, and Ctrl+Z undoes it.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot,
        };

        if (await ShowEditorDialogAsync(confirm) != ContentDialogResult.Primary)
        {
            return;
        }

        await RunEditorOperationAsync(
            bytes => PdfPageEditService.DeletePagesAsync(bytes, selected),
            "Couldn't delete the pages");
    }

    private async void MovePageLeftButton_Click(object sender, RoutedEventArgs e) =>
        await MoveSelectedPagesAsync(-1);

    private async void MovePageRightButton_Click(object sender, RoutedEventArgs e) =>
        await MoveSelectedPagesAsync(1);

    private async Task MoveSelectedPagesAsync(int delta)
    {
        if (_editor is null || _editor.IsBusy)
        {
            return;
        }

        var selected = SelectedPositions();
        int count = _editor.PageCount;
        if (selected.Count == 0 ||
            (delta < 0 && selected[0] == 0) ||
            (delta > 0 && selected[^1] == count - 1))
        {
            return;
        }

        var order = Enumerable.Range(0, count).ToList();

        // Walk from the edge the pages are moving toward, so each swap lands
        // before the next one needs the slot.
        var walk = delta < 0 ? selected : Enumerable.Reverse(selected).ToList();
        foreach (int position in walk)
        {
            int target = position + delta;
            (order[position], order[target]) = (order[target], order[position]);
        }

        var newSelection = selected.Select(p => p + delta).ToList();
        await RunEditorOperationAsync(
            bytes => PdfPageEditService.ReorderAsync(bytes, order),
            "Couldn't move the pages",
            newSelection);
    }

    private async void InsertPagesButton_Click(object sender, RoutedEventArgs e) =>
        await InsertPagesAsync();

    private async Task InsertPagesAsync()
    {
        if (_editor is null || _editor.IsBusy)
        {
            return;
        }

        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".pdf");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        byte[] incoming;
        int incomingCount;
        try
        {
            var buffer = await FileIO.ReadBufferAsync(file);
            incoming = buffer.ToArray();
            incomingCount = await PdfPageEditService.GetPageCountAsync(incoming);
        }
        catch
        {
            await ShowErrorAsync(
                "Couldn't read that file",
                $"\"{file.Name}\" could not be opened. It may be password-protected or not a valid PDF.");
            return;
        }

        var selected = SelectedPositions();
        var options = await AskWhereToInsertAsync(file.Name, incomingCount, selected);
        if (options is null)
        {
            return;
        }

        var (rangeText, insertBeforeSelection) = options.Value;
        var indices = PdfPageEditService.ParsePageRange(rangeText, incomingCount);
        if (indices is null)
        {
            await ShowErrorAsync(
                "Couldn't read that page range",
                $"Use page numbers between 1 and {incomingCount}, like \"1-3, 7\" — or \"all\".");
            return;
        }

        int at = insertBeforeSelection && selected.Count > 0 ? selected[0] : _editor.PageCount;
        var newSelection = Enumerable.Range(at, indices.Count).ToList();

        await RunEditorOperationAsync(
            bytes => PdfPageEditService.InsertPagesAsync(bytes, incoming, indices, at),
            "Couldn't insert those pages",
            newSelection);
    }

    /// <summary>
    /// Asks which pages to take and where to put them. Returns null if cancelled.
    /// </summary>
    private async Task<(string Range, bool BeforeSelection)?> AskWhereToInsertAsync(
        string fileName,
        int incomingCount,
        IReadOnlyList<int> selected)
    {
        var rangeBox = new TextBox
        {
            Text = "all",
            Header = $"Pages to take from \"{fileName}\" (1–{incomingCount})",
            PlaceholderText = "all, or 1-3, 7",
        };

        var atEnd = new RadioButton
        {
            Content = "At the end",
            IsChecked = selected.Count == 0,
        };

        var beforeSelection = new RadioButton
        {
            Content = selected.Count > 0
                ? $"Before page {selected[0] + 1}"
                : "Before the selected page (none selected)",
            IsEnabled = selected.Count > 0,
            IsChecked = selected.Count > 0,
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(rangeBox);
        panel.Children.Add(new TextBlock { Text = "Where" });
        panel.Children.Add(beforeSelection);
        panel.Children.Add(atEnd);

        var dialog = new ContentDialog
        {
            Title = "Insert pages",
            Content = panel,
            PrimaryButtonText = "Insert",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };

        if (await ShowEditorDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return null;
        }

        return (rangeBox.Text, beforeSelection.IsChecked == true);
    }

    private async void EditorUndoButton_Click(object sender, RoutedEventArgs e) => await EditorUndoAsync();

    private async Task EditorUndoAsync()
    {
        if (_editor is null || _editor.IsBusy || !_editor.CanUndo)
        {
            return;
        }

        await _editor.UndoAsync();
        UpdateEditorCommands();
        UpdateTitle();
    }

    /// <summary>
    /// Applies a page operation, reporting failures without disturbing the
    /// document, and restores a sensible selection afterwards.
    /// </summary>
    private async Task RunEditorOperationAsync(
        Func<byte[], Task<byte[]>> operation,
        string errorTitle,
        IReadOnlyList<int>? selectAfter = null)
    {
        if (_editor is null)
        {
            return;
        }

        try
        {
            await _editor.ApplyAsync(operation);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(errorTitle, ex.Message);

            // The bytes are untouched on failure, but the grid may have been
            // reordered by a drag — rebuild it from the document to be sure.
            await RefreshEditorFromBytesAsync();
            return;
        }

        RestoreSelection(selectAfter);
        UpdateEditorCommands();
        UpdateTitle();
    }

    /// <summary>Re-syncs the grid with the editor's document after a failed edit.</summary>
    private async Task RefreshEditorFromBytesAsync()
    {
        if (_editor is null)
        {
            return;
        }

        await _editor.RefreshAsync();
        UpdateEditorCommands();
    }

    private void RestoreSelection(IReadOnlyList<int>? positions)
    {
        PagesGrid.SelectedItems.Clear();
        if (_editor is null || positions is null)
        {
            return;
        }

        foreach (int position in positions)
        {
            if (position >= 0 && position < _editor.Pages.Count)
            {
                PagesGrid.SelectedItems.Add(_editor.Pages[position]);
            }
        }
    }

    // ---------------------------------------------------------------- saving

    private async void EditorSaveButton_Click(object sender, RoutedEventArgs e) => await SaveEditedPdfAsync();

    private async Task SaveEditedPdfAsync()
    {
        if (_editor is null || _editor.IsBusy)
        {
            return;
        }

        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add("PDF document", new List<string> { ".pdf" });
        picker.SuggestedFileName =
            System.IO.Path.GetFileNameWithoutExtension(_editor.SourceName) + " (edited)";
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        // Same rule as the viewer's save: never write over the file that's open.
        if (_doc is not null &&
            string.Equals(file.Path, _doc.File.Path, StringComparison.OrdinalIgnoreCase))
        {
            await ShowErrorAsync(
                "Choose a different name",
                "Saving over the original isn't allowed — pick a new file name so a fresh version is created.");
            return;
        }

        try
        {
            await FileIO.WriteBytesAsync(file, _editor.Bytes);
        }
        catch
        {
            await ShowErrorAsync("Save failed", $"Could not save to \"{file.Path}\".");
            return;
        }

        _editor.MarkSaved();
        UpdateTitle();

        var next = new ContentDialog
        {
            Title = "Saved",
            Content = $"Saved to {file.Path}.\n\nOpen it in the viewer to annotate or present it?",
            PrimaryButtonText = "Open in viewer",
            CloseButtonText = "Keep editing",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };

        if (await ShowEditorDialogAsync(next) == ContentDialogResult.Primary)
        {
            // Opening resets the editor and returns to the viewer tab, so the
            // rest of the app carries on with the new version as usual.
            await OpenFileAsync(file);
        }
        else
        {
            EditorInfoBar.Message = $"Saved to {file.Path}";
            EditorInfoBar.IsOpen = true;
        }
    }
}
