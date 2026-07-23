using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using PdfReader.Models;
using PdfReader.Services;
using PdfReader.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;
using WinRT.Interop;

namespace PdfReader;

public sealed partial class MainWindow : Window
{
    private const double PageSpacing = 16.0;   // must match StackLayout Spacing in XAML
    private const double ContentMargin = 24.0; // must match ItemsRepeater Margin in XAML
    private const double MinZoom = 0.25;
    private const double MaxZoom = 4.0;

    private static readonly double[] ZoomPresets =
        { 0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0 };

    private readonly PrintService _printService;
    private readonly Stack<(PageViewModel Page, AnnotationBase Annotation, bool WasAdd)> _undoStack = new();
    private DocumentViewModel? _doc;
    private TextGeometryService? _textService;
    private LinkService? _linkService;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _infoBarTimer;
    private int _currentPage = 1;
    private bool _fitWidthMode = true;
    private bool _isModified;
    private double _panStartHorizontal;
    private double _panStartVertical;

    // "Type a slide number, press Enter" quick navigation.
    private string _gotoBuffer = string.Empty;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _gotoTimer;

    public MainWindow()
    {
        InitializeComponent();

        // Custom title bar over Mica.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Colors.White;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF);
        AppWindow.TitleBar.ButtonHoverForegroundColor = Colors.White;

        AppWindow.Resize(new SizeInt32(1200, 860));

        _printService = new PrintService(this);

        // Undo bookkeeping for edits made on the page overlays.
        ToolState.Current.AnnotationAdded += (page, annotation) => OnAnnotationEdit(page, annotation, wasAdd: true);
        ToolState.Current.AnnotationRemoved += (page, annotation) => OnAnnotationEdit(page, annotation, wasAdd: false);

        // Hand-tool panning: the overlay reports window-space deltas; translate
        // them into scroll offsets from where the grab started.
        ToolState.Current.PanStarted += () =>
        {
            _panStartHorizontal = Scroller.HorizontalOffset;
            _panStartVertical = Scroller.VerticalOffset;
        };
        ToolState.Current.PanUpdated += (dx, dy) =>
            Scroller.ChangeView(_panStartHorizontal - dx, _panStartVertical - dy, null, disableAnimation: true);

        // Link navigation raised from the per-page link layers.
        ToolState.Current.NavigateToPageRequested += NavigateToPage;
        ToolState.Current.OpenUriRequested += uri => _ = OpenExternalUriAsync(uri);

        // Ctrl+mouse-wheel zoom. handledEventsToo because the ScrollViewer
        // marks wheel events handled.
        Scroller.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(Scroller_PointerWheelChanged),
            handledEventsToo: true);

        RegisterSlideNumberAccelerators();
    }

    /// <summary>
    /// Registers digit / Enter / Backspace accelerators for PowerPoint-style
    /// "type a slide number, press Enter" navigation. Done in code because
    /// twenty digit keys (top row + numpad) would bloat the XAML.
    /// </summary>
    private void RegisterSlideNumberAccelerators()
    {
        void Add(VirtualKey key, TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> handler)
        {
            var accelerator = new KeyboardAccelerator { Key = key };
            accelerator.Invoked += handler;
            Root.KeyboardAccelerators.Add(accelerator);
        }

        for (int k = (int)VirtualKey.Number0; k <= (int)VirtualKey.Number9; k++)
        {
            Add((VirtualKey)k, DigitAccelerator_Invoked);
        }

        for (int k = (int)VirtualKey.NumberPad0; k <= (int)VirtualKey.NumberPad9; k++)
        {
            Add((VirtualKey)k, DigitAccelerator_Invoked);
        }

        Add(VirtualKey.Enter, EnterAccelerator_Invoked);
        Add(VirtualKey.Back, BackAccelerator_Invoked);
    }

    // ---------------------------------------------------------------- opening

    public async Task OpenFileAsync(StorageFile file)
    {
        DocumentViewModel doc;
        try
        {
            doc = await DocumentViewModel.LoadAsync(file);
        }
        catch
        {
            await ShowErrorAsync(
                "Couldn't open file",
                $"\"{file.Name}\" could not be opened. It may be password-protected or not a valid PDF.");
            return;
        }

        _doc = doc;
        doc.SetRasterizationScale(Root.XamlRoot?.RasterizationScale ?? 1.0);

        _textService?.Dispose();
        _textService = new TextGeometryService(doc.SourceBytes);
        ToolState.Current.WordProvider = index => _textService.GetWordsAsync(index);

        _linkService?.Dispose();
        _linkService = new LinkService(doc.SourceBytes);
        ToolState.Current.LinkProvider = index => _linkService.GetLinksAsync(index);

        _undoStack.Clear();
        _isModified = false;
        ClearGoto();
        SetTool(AnnotationTool.Hand); // hand/pan is the default reading tool

        PagesRepeater.ItemsSource = doc.Pages;
        EmptyState.Visibility = Visibility.Collapsed;

        UpdateTitle();
        PageCountText.Text = $"/ {doc.Pages.Count}";
        PageBox.Text = "1";
        _currentPage = 1;

        PrintButton.IsEnabled = _printService.IsSupported;
        SaveButton.IsEnabled = true;
        UndoButton.IsEnabled = false;
        PageBox.IsEnabled = true;
        PrevPageButton.IsEnabled = true;
        NextPageButton.IsEnabled = true;
        ZoomInButton.IsEnabled = true;
        ZoomOutButton.IsEnabled = true;
        FitWidthButton.IsEnabled = true;
        SelectToolButton.IsEnabled = true;
        HandToolButton.IsEnabled = true;
        TextSelectToolButton.IsEnabled = true;
        DrawToolButton.IsEnabled = true;
        HighlightToolButton.IsEnabled = true;
        TextToolButton.IsEnabled = true;
        CommentToolButton.IsEnabled = true;
        SignatureToolButton.IsEnabled = true;
        EraseToolButton.IsEnabled = true;
        ColorsButton.IsEnabled = true;

        _fitWidthMode = true;
        Root.UpdateLayout();
        ApplyFitWidth();
        Scroller.ChangeView(0, 0, null, disableAnimation: true);

        // Park keyboard focus on the document, not the Open button — a
        // focused toolbar button swallows Enter (re-opening the file picker)
        // and surfaces its "Ctrl+O" accelerator hint at odd moments.
        Scroller.Focus(FocusState.Programmatic);
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e) => await PickAndOpenAsync();

    private async Task PickAndOpenAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".pdf");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await OpenFileAsync(file);
        }
    }

    private void UpdateTitle()
    {
        if (_doc is null)
        {
            Title = "Slate PDF";
            FileNameText.Text = string.Empty;
            return;
        }

        string name = _isModified ? $"{_doc.FileName} •" : _doc.FileName;
        Title = $"{name} - Slate PDF";
        FileNameText.Text = $"—  {name}";
    }

    // ---------------------------------------------------------------- drag & drop

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Open PDF";
        }
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        var pdf = items.OfType<StorageFile>()
            .FirstOrDefault(f => f.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase));
        if (pdf is not null)
        {
            await OpenFileAsync(pdf);
        }
    }

    // ---------------------------------------------------------------- lazy page rendering

    private void Page_EffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        if (sender.DataContext is not PageViewModel page)
        {
            return;
        }

        double threshold = Math.Max(Scroller.ViewportHeight, 400);
        if (args.BringIntoViewDistanceY <= threshold * 1.5)
        {
            _ = page.EnsureRenderedAsync();
        }
        else if (args.BringIntoViewDistanceY > threshold * 4)
        {
            page.Release();
        }
    }

    // ---------------------------------------------------------------- annotation tools

    private void SetTool(AnnotationTool tool)
    {
        ToolState.Current.Tool = tool;
        if (tool != AnnotationTool.TextSelect)
        {
            ToolState.Current.ClearSelection();
        }

        SelectToolButton.IsChecked = tool == AnnotationTool.None;
        HandToolButton.IsChecked = tool == AnnotationTool.Hand;
        TextSelectToolButton.IsChecked = tool == AnnotationTool.TextSelect;
        DrawToolButton.IsChecked = tool == AnnotationTool.Draw;
        HighlightToolButton.IsChecked = tool == AnnotationTool.Highlight;
        TextToolButton.IsChecked = tool == AnnotationTool.Text;
        CommentToolButton.IsChecked = tool == AnnotationTool.Comment;
        SignatureToolButton.IsChecked = tool == AnnotationTool.Signature;
        EraseToolButton.IsChecked = tool == AnnotationTool.Erase;

        // Clicking a tool button leaves focus on that button, where Enter
        // would re-toggle it and digits/goto behave inconsistently. Hand the
        // focus back to the document after every tool change.
        if (_doc is not null)
        {
            Scroller.Focus(FocusState.Programmatic);
        }
    }

    private void SelectToolButton_Click(object sender, RoutedEventArgs e) =>
        SetTool(AnnotationTool.None);

    private void HandToolButton_Click(object sender, RoutedEventArgs e) =>
        SetTool(HandToolButton.IsChecked == true ? AnnotationTool.Hand : AnnotationTool.None);

    private void TextSelectToolButton_Click(object sender, RoutedEventArgs e) =>
        SetTool(TextSelectToolButton.IsChecked == true ? AnnotationTool.TextSelect : AnnotationTool.None);

    private void DrawToolButton_Click(object sender, RoutedEventArgs e) =>
        SetTool(DrawToolButton.IsChecked == true ? AnnotationTool.Draw : AnnotationTool.None);

    private void HighlightToolButton_Click(object sender, RoutedEventArgs e) =>
        SetTool(HighlightToolButton.IsChecked == true ? AnnotationTool.Highlight : AnnotationTool.None);

    private void TextToolButton_Click(object sender, RoutedEventArgs e) =>
        SetTool(TextToolButton.IsChecked == true ? AnnotationTool.Text : AnnotationTool.None);

    private void CommentToolButton_Click(object sender, RoutedEventArgs e) =>
        SetTool(CommentToolButton.IsChecked == true ? AnnotationTool.Comment : AnnotationTool.None);

    private async void SignatureToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (SignatureToolButton.IsChecked != true)
        {
            SetTool(AnnotationTool.None);
            return;
        }

        if (await EnsureSignatureAsync())
        {
            SetTool(AnnotationTool.Signature);
        }
        else
        {
            SetTool(AnnotationTool.None);
        }
    }

    private void EraseToolButton_Click(object sender, RoutedEventArgs e) =>
        SetTool(EraseToolButton.IsChecked == true ? AnnotationTool.Erase : AnnotationTool.None);

    // ---------------------------------------------------------------- signature management

    /// <summary>Makes sure a signature image is saved and decoded; prompts to import one if not.</summary>
    private async Task<bool> EnsureSignatureAsync()
    {
        if (!SignatureStore.HasSignature && !await ImportSignatureAsync())
        {
            return false;
        }

        return await SignatureStore.GetBitmapAsync() is not null;
    }

    private async Task<bool> ImportSignatureAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".bmp");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return false;
        }

        try
        {
            await SignatureStore.ImportAsync(file);
            return true;
        }
        catch
        {
            await ShowErrorAsync(
                "Couldn't import signature",
                $"\"{file.Name}\" could not be read as an image.");
            return false;
        }
    }

    private async void ReplaceSignature_Click(object sender, RoutedEventArgs e)
    {
        if (await ImportSignatureAsync())
        {
            await SignatureStore.GetBitmapAsync();
        }
    }

    private void RemoveSignature_Click(object sender, RoutedEventArgs e)
    {
        SignatureStore.Clear();
        if (ToolState.Current.Tool == AnnotationTool.Signature)
        {
            SetTool(AnnotationTool.None);
        }
    }

    private static Windows.UI.Color ParseColor(string hex) => Windows.UI.Color.FromArgb(
        255,
        Convert.ToByte(hex.Substring(1, 2), 16),
        Convert.ToByte(hex.Substring(3, 2), 16),
        Convert.ToByte(hex.Substring(5, 2), 16));

    private void PenColorItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string hex })
        {
            ToolState.Current.PenColor = ParseColor(hex);
        }
    }

    private void PenSizeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string size })
        {
            ToolState.Current.PenThickness = double.Parse(size, CultureInfo.InvariantCulture);
        }
    }

    private void HighlightColorItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string hex })
        {
            ToolState.Current.HighlightColor = ParseColor(hex);
        }
    }

    private void TextSizeItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string size })
        {
            ToolState.Current.FontSize = double.Parse(size, CultureInfo.InvariantCulture);
        }
    }

    private void OnAnnotationEdit(PageViewModel page, AnnotationBase annotation, bool wasAdd)
    {
        _undoStack.Push((page, annotation, wasAdd));
        UndoButton.IsEnabled = true;
        if (!_isModified)
        {
            _isModified = true;
            UpdateTitle();
        }
    }

    private void Undo()
    {
        if (_undoStack.Count == 0)
        {
            return;
        }

        var (page, annotation, wasAdd) = _undoStack.Pop();
        if (wasAdd)
        {
            page.Annotations.Remove(annotation);
        }
        else
        {
            page.Annotations.Add(annotation);
        }

        UndoButton.IsEnabled = _undoStack.Count > 0;
        if (_undoStack.Count == 0 && _isModified)
        {
            _isModified = false;
            UpdateTitle();
        }
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e) => Undo();

    // ---------------------------------------------------------------- saving

    private async void SaveButton_Click(object sender, RoutedEventArgs e) => await SaveCopyAsync();

    private async Task SaveCopyAsync()
    {
        if (_doc is null)
        {
            return;
        }

        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add("PDF document", new List<string> { ".pdf" });
        picker.SuggestedFileName =
            System.IO.Path.GetFileNameWithoutExtension(_doc.FileName) + " (annotated)";
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            await PdfSaveService.SaveAsync(_doc, file.Path);
        }
        catch
        {
            await ShowErrorAsync("Save failed", $"Could not save to \"{file.Path}\".");
            return;
        }

        _isModified = false;
        UpdateTitle();
        ShowSaveConfirmation(file.Path);
    }

    private void ShowSaveConfirmation(string path)
    {
        SaveInfoBar.Message = $"Saved to {path}";
        SaveInfoBar.IsOpen = true;

        if (_infoBarTimer is null)
        {
            _infoBarTimer = DispatcherQueue.CreateTimer();
            _infoBarTimer.Interval = TimeSpan.FromSeconds(4);
            _infoBarTimer.IsRepeating = false;
            _infoBarTimer.Tick += (_, _) => SaveInfoBar.IsOpen = false;
        }

        _infoBarTimer.Stop();
        _infoBarTimer.Start();
    }

    // ---------------------------------------------------------------- page navigation

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        // Only track once the view settles: intermediate events during an
        // animated page jump would recompute _currentPage from a mid-flight
        // offset and make rapid PageDown presses re-target the same page.
        if (!e.IsIntermediate)
        {
            UpdateCurrentPageFromScroll();
        }
    }

    private void UpdateCurrentPageFromScroll()
    {
        if (_doc is null || _doc.Pages.Count == 0)
        {
            return;
        }

        double center = Scroller.VerticalOffset + Scroller.ViewportHeight / 2;
        double y = ContentMargin;
        int page = _doc.Pages.Count;

        for (int i = 0; i < _doc.Pages.Count; i++)
        {
            double bottom = y + _doc.Pages[i].DisplayHeight;
            if (center < bottom + PageSpacing / 2)
            {
                page = i + 1;
                break;
            }

            y = bottom + PageSpacing;
        }

        if (page != _currentPage)
        {
            _currentPage = page;
            if (!ReferenceEquals(FocusManager.GetFocusedElement(Root.XamlRoot), PageBox))
            {
                PageBox.Text = page.ToString();
            }
        }
    }

    private void JumpToPage(int pageNumber, bool animate = false)
    {
        if (_doc is null)
        {
            return;
        }

        pageNumber = Math.Clamp(pageNumber, 1, _doc.Pages.Count);
        double offset = ContentMargin;
        for (int i = 0; i < pageNumber - 1; i++)
        {
            offset += _doc.Pages[i].DisplayHeight + PageSpacing;
        }

        Scroller.ChangeView(null, offset, null, disableAnimation: !animate);
        _currentPage = pageNumber;
        PageBox.Text = pageNumber.ToString();
    }

    // ---------------------------------------------------------------- link navigation

    /// <summary>Scrolls to a zero-based page and a 0..1 vertical position on it (internal links).</summary>
    private void NavigateToPage(int pageIndex, double topFraction)
    {
        if (_doc is null || _doc.Pages.Count == 0)
        {
            return;
        }

        pageIndex = Math.Clamp(pageIndex, 0, _doc.Pages.Count - 1);
        double offset = ContentMargin;
        for (int i = 0; i < pageIndex; i++)
        {
            offset += _doc.Pages[i].DisplayHeight + PageSpacing;
        }

        offset += Math.Clamp(topFraction, 0, 1) * _doc.Pages[pageIndex].DisplayHeight;
        offset = Math.Max(0, offset - 8); // a little headroom above the target

        // Animated so the jump reads as navigation rather than a teleport.
        Scroller.ChangeView(null, offset, null, disableAnimation: false);
        _currentPage = pageIndex + 1;
        PageBox.Text = _currentPage.ToString();
    }

    /// <summary>Opens an external link after confirming, restricted to safe schemes.</summary>
    private async Task OpenExternalUriAsync(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https" or "mailto" or "tel"))
        {
            await ShowErrorAsync("Can't open link", "This link points to an unsupported or unsafe location.");
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Open link?",
            Content = parsed.ToString(),
            PrimaryButtonText = "Open",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await Launcher.LaunchUriAsync(parsed);
        }
    }

    private void PrevPageButton_Click(object sender, RoutedEventArgs e) => JumpToPage(_currentPage - 1);

    private void NextPageButton_Click(object sender, RoutedEventArgs e) => JumpToPage(_currentPage + 1);

    private void PageBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && int.TryParse(PageBox.Text, out int page))
        {
            JumpToPage(page);
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- zoom

    /// <summary>
    /// Sets the zoom, keeping a chosen viewport point fixed over the same spot
    /// in the document. Defaults to the viewport center; Ctrl+wheel passes the
    /// cursor position so zooming homes in on where you're pointing.
    /// </summary>
    private void SetZoom(double zoom, bool keepAnchor = true, Point? anchorInViewport = null)
    {
        if (_doc is null || _doc.Pages.Count == 0)
        {
            return;
        }

        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        double oldZoom = _doc.Zoom;
        if (Math.Abs(zoom - oldZoom) < 0.001)
        {
            return;
        }

        double px = anchorInViewport?.X ?? Scroller.ViewportWidth / 2;
        double py = anchorInViewport?.Y ?? Scroller.ViewportHeight / 2;
        double ratio = zoom / oldZoom;

        // Content-space point currently under the anchor, captured before the
        // layout changes. Horizontally the content is centered when narrower
        // than the viewport, so subtract that centering pad to get a
        // content-local coordinate that scales with zoom.
        double viewportWidth = Scroller.ViewportWidth;
        double maxPageBaseWidth = _doc.Pages.Max(p => p.BaseWidth);
        double contentWidthOld = maxPageBaseWidth * oldZoom + ContentMargin * 2;
        double padOld = Math.Max(0, (viewportWidth - contentWidthOld) / 2);
        double contentX = Scroller.HorizontalOffset + px - padOld;
        double contentY = Scroller.VerticalOffset + py;

        _doc.SetZoom(zoom);
        ZoomText.Text = $"{Math.Round(zoom * 100)}%";

        if (keepAnchor)
        {
            Root.UpdateLayout();
            double contentWidthNew = maxPageBaseWidth * zoom + ContentMargin * 2;
            double padNew = Math.Max(0, (viewportWidth - contentWidthNew) / 2);
            double newHorizontal = contentX * ratio + padNew - px;
            double newVertical = contentY * ratio - py;
            Scroller.ChangeView(
                Math.Max(0, newHorizontal),
                Math.Max(0, newVertical),
                null,
                disableAnimation: true);
        }
    }

    private void StepZoom(int direction, Point? anchorInViewport = null)
    {
        if (_doc is null)
        {
            return;
        }

        _fitWidthMode = false;
        double current = _doc.Zoom;
        double target = direction > 0
            ? ZoomPresets.FirstOrDefault(z => z > current + 0.001, MaxZoom)
            : ZoomPresets.LastOrDefault(z => z < current - 0.001, MinZoom);
        SetZoom(target, keepAnchor: true, anchorInViewport);
    }

    private void ApplyFitWidth()
    {
        if (_doc is null || _doc.Pages.Count == 0)
        {
            return;
        }

        double viewport = Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : Root.ActualWidth;
        double maxPageWidth = _doc.Pages.Max(p => p.BaseWidth);
        if (viewport <= 0 || maxPageWidth <= 0)
        {
            return;
        }

        double zoom = (viewport - ContentMargin * 2) / maxPageWidth;
        SetZoom(zoom, keepAnchor: false);
        // First-time zoom text update even if SetZoom short-circuits.
        ZoomText.Text = $"{Math.Round(_doc.Zoom * 100)}%";
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e) => StepZoom(+1);

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e) => StepZoom(-1);

    private void FitWidthButton_Click(object sender, RoutedEventArgs e)
    {
        _fitWidthMode = true;
        ApplyFitWidth();
    }

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fitWidthMode)
        {
            ApplyFitWidth();
        }
    }

    private void Scroller_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        bool ctrlDown = InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);
        if (!ctrlDown || _doc is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(Scroller);
        StepZoom(point.Properties.MouseWheelDelta > 0 ? +1 : -1, point.Position);
        e.Handled = true;
    }

    // ---------------------------------------------------------------- printing

    private async void PrintButton_Click(object sender, RoutedEventArgs e) => await PrintAsync();

    private async Task PrintAsync()
    {
        if (_doc is null)
        {
            return;
        }

        if (!_printService.IsSupported)
        {
            await ShowErrorAsync("Printing unavailable", "Printing is not supported on this device.");
            return;
        }

        try
        {
            PrintButton.IsEnabled = false;
            await _printService.PrintAsync(_doc);
        }
        catch
        {
            await ShowErrorAsync("Print failed", "Something went wrong while preparing the document for printing.");
        }
        finally
        {
            PrintButton.IsEnabled = true;
        }
    }

    // ---------------------------------------------------------------- keyboard accelerators

    private async void OpenAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await PickAndOpenAsync();
    }

    private async void SaveAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await SaveCopyAsync();
    }

    private async void PrintAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await PrintAsync();
    }

    private void UndoAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Undo();
    }

    private void CopyAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Let TextBoxes (page box, text editors, comment editors) keep their
        // native copy behavior.
        if (FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox)
        {
            return;
        }

        string text = ToolState.Current.SelectedText;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        args.Handled = true;
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private void ZoomInAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        StepZoom(+1);
    }

    private void ZoomOutAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        StepZoom(-1);
    }

    private void FitWidthAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _fitWidthMode = true;
        ApplyFitWidth();
    }

    private void EscapeAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Esc first cancels a half-typed slide number, then falls back to the
        // hand tool (the default), so it's always a "get me back" key.
        if (_gotoBuffer.Length > 0)
        {
            ClearGoto();
            args.Handled = true;
            return;
        }

        if (_doc is not null && ToolState.Current.Tool != AnnotationTool.Hand)
        {
            SetTool(AnnotationTool.Hand);
            args.Handled = true;
            return;
        }

        args.Handled = false;
    }

    private void HandAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Don't hijack "h" while typing in a text field.
        if (_doc is null || IsTextBoxFocused())
        {
            args.Handled = false;
            return;
        }

        args.Handled = true;
        SetTool(ToolState.Current.Tool == AnnotationTool.Hand ? AnnotationTool.None : AnnotationTool.Hand);
    }

    // ---------------------------------------------------------------- page keys / slide number

    /// <summary>
    /// Page/document navigation keys, handled on the tunneling preview pass so
    /// they preempt the focused ScrollViewer's built-in handling (which scrolls
    /// by a screenful and would fight the page-snapped jumps).
    /// </summary>
    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_doc is null || IsTextBoxFocused())
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.PageDown:
                JumpToPage(_currentPage + 1, animate: true);
                break;
            case VirtualKey.PageUp:
                JumpToPage(_currentPage - 1, animate: true);
                break;
            case VirtualKey.Home:
                JumpToPage(1, animate: true);
                break;
            case VirtualKey.End:
                JumpToPage(_doc.Pages.Count, animate: true);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void DigitAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        int digit = DigitFromKey(sender.Key);
        if (_doc is null || digit < 0 || IsTextBoxFocused())
        {
            args.Handled = false;
            return;
        }

        args.Handled = true;

        // Move focus off any toolbar button so Enter isn't swallowed as a
        // button activation before our Enter accelerator sees it.
        if (_gotoBuffer.Length == 0)
        {
            Scroller.Focus(FocusState.Programmatic);
        }

        if (_gotoBuffer.Length < 5) // no document has 100k pages
        {
            _gotoBuffer += (char)('0' + digit);
        }

        ShowGoto();
    }

    private void EnterAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Only act on Enter when a slide number is pending; otherwise leave
        // Enter to whatever has focus (buttons, text fields).
        if (_gotoBuffer.Length == 0 || IsTextBoxFocused())
        {
            args.Handled = false;
            return;
        }

        args.Handled = true;
        if (int.TryParse(_gotoBuffer, out int page))
        {
            JumpToPage(page, animate: true);
        }

        ClearGoto();
    }

    private void BackAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_gotoBuffer.Length == 0 || IsTextBoxFocused())
        {
            args.Handled = false;
            return;
        }

        args.Handled = true;
        _gotoBuffer = _gotoBuffer[..^1];
        if (_gotoBuffer.Length == 0)
        {
            ClearGoto();
        }
        else
        {
            ShowGoto();
        }
    }

    private static int DigitFromKey(VirtualKey key)
    {
        if (key >= VirtualKey.Number0 && key <= VirtualKey.Number9)
        {
            return key - VirtualKey.Number0;
        }

        if (key >= VirtualKey.NumberPad0 && key <= VirtualKey.NumberPad9)
        {
            return key - VirtualKey.NumberPad0;
        }

        return -1;
    }

    private bool IsTextBoxFocused() => FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox;

    private void ShowGoto()
    {
        GotoText.Text = _gotoBuffer;
        GotoIndicator.Visibility = Visibility.Visible;

        // Auto-dismiss if the user pauses, matching PowerPoint's behavior.
        if (_gotoTimer is null)
        {
            _gotoTimer = DispatcherQueue.CreateTimer();
            _gotoTimer.Interval = TimeSpan.FromSeconds(3);
            _gotoTimer.IsRepeating = false;
            _gotoTimer.Tick += (_, _) => ClearGoto();
        }

        _gotoTimer.Stop();
        _gotoTimer.Start();
    }

    private void ClearGoto()
    {
        _gotoBuffer = string.Empty;
        GotoIndicator.Visibility = Visibility.Collapsed;
        _gotoTimer?.Stop();
    }

    // ---------------------------------------------------------------- helpers

    private async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = Root.XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
