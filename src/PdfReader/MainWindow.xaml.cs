using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Windowing;
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

    // The page explicitly navigated to (-1 = none). Re-validated on every
    // scroll settle instead of a consume-once flag, which could get stuck when
    // a ChangeView produced no event (e.g. already at the target).
    private int _commandedPage = -1;

    // Full-screen presentation mode.
    private bool _presenting;
    private bool _chromeShown = true;
    private Brush? _defaultScrollerBackground;
    private Storyboard? _chromeAnim;
    private bool _laserOn;
    private Point _lastPointerInRoot;

    // ---- cursor hiding for the laser pointer -----------------------------
    // WinUI re-applies a cursor on every pointer move, so ShowCursor is not
    // enough. We subclass the window and swallow WM_SETCURSOR (hiding the
    // cursor) while the laser is on, which the framework can't override.
    private const int GWLP_WNDPROC = -4;
    private const uint WM_SETCURSOR = 0x0020;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const int HTCLIENT = 1;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    private WndProcDelegate? _wndProc; // kept alive for the native pointer
    private IntPtr _wndProcPtr;
    private readonly Dictionary<IntPtr, IntPtr> _originalProcs = new();

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr prev, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWnd, EnumChildProc callback, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr newLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int newLong);

    private static IntPtr SetWindowLongPtrCompat(IntPtr hWnd, int nIndex, IntPtr newLong) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, newLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, newLong.ToInt32()));

    // "Type a slide number, press Enter" quick navigation.
    private string _gotoBuffer = string.Empty;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _gotoTimer;

    /// <summary>Vertical gap between pages. In presentation mode it's a full
    /// screen so only one slide is ever visible.</summary>
    private double PageGap => _presenting ? Math.Max(PageSpacing, Scroller.ViewportHeight) : PageSpacing;

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

        // Cursor-to-top reveal of the chrome while presenting. handledEventsToo
        // so it fires even over the page/overlay which may mark moves handled.
        Root.AddHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler(Root_PointerMoved),
            handledEventsToo: true);

        _defaultScrollerBackground = Scroller.Background;

        InstallCursorHook();
        RegisterSlideNumberAccelerators();
    }

    private void InstallCursorHook()
    {
        _wndProc = LaserWndProc;
        _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProc);
        Subclass(WindowNative.GetWindowHandle(this));
    }

    /// <summary>
    /// Subclasses the content-island child windows too. WinUI sets the cursor
    /// from a child island window, not the top-level HWND, so hooking only the
    /// top window wouldn't catch WM_SETCURSOR. Called lazily when the laser is
    /// first switched on, by which point the islands exist.
    /// </summary>
    private void EnsureCursorHooks()
    {
        var top = WindowNative.GetWindowHandle(this);
        EnumChildWindows(top, (child, _) =>
        {
            Subclass(child);
            return true;
        }, IntPtr.Zero);
    }

    private void Subclass(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _originalProcs.ContainsKey(hwnd))
        {
            return;
        }

        _originalProcs[hwnd] = SetWindowLongPtrCompat(hwnd, GWLP_WNDPROC, _wndProcPtr);
    }

    private IntPtr LaserWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // While the laser is on, hide the cursor over the client area — this
        // runs before WinUI's own handler, so it can't re-show the cursor.
        if (msg == WM_SETCURSOR && _laserOn && (lParam.ToInt64() & 0xFFFF) == HTCLIENT)
        {
            SetCursor(IntPtr.Zero);
            return (IntPtr)1; // TRUE — handled
        }

        var result = _originalProcs.TryGetValue(hWnd, out var original) && original != IntPtr.Zero
            ? CallWindowProc(original, hWnd, msg, wParam, lParam)
            : DefWindowProc(hWnd, msg, wParam, lParam);

        // WinUI's pointer pipeline also calls SetCursor directly while
        // processing mouse moves (not just via WM_SETCURSOR). Re-null the
        // cursor after its handler ran — last write wins until the next move.
        if (msg == WM_MOUSEMOVE && _laserOn)
        {
            SetCursor(IntPtr.Zero);
        }

        return result;
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
        PresentButton.IsEnabled = true;

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

        // Never overwrite the file that's open — this is always "Save As a new
        // version", so a same-path pick is refused.
        if (string.Equals(file.Path, _doc.File.Path, StringComparison.OrdinalIgnoreCase))
        {
            await ShowErrorAsync(
                "Choose a different name",
                "Saving over the original isn't allowed — pick a new file name so a fresh version is created.");
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
        if (e.IsIntermediate)
        {
            return;
        }

        if (_doc is null || _doc.Pages.Count == 0)
        {
            return;
        }

        // While presenting, any stray scroll (e.g. a touchpad nudge) snaps back
        // to the nearest whole slide so a partial slide is never left on screen.
        if (_presenting)
        {
            int nearest = NearestPresentationPage();
            _currentPage = nearest;
            PageBox.Text = nearest.ToString();
            if (Math.Abs(ScrollTargetFor(nearest) - Scroller.VerticalOffset) > 1)
            {
                JumpToPage(nearest);
            }

            return;
        }

        // If the view sits where a commanded jump landed (allowing for end-of-
        // document clamping), keep that page; otherwise it was a manual scroll,
        // so recompute from the offset — which can never drift or get stuck.
        int page;
        if (_commandedPage > 0 &&
            Math.Abs(Math.Clamp(ScrollTargetFor(_commandedPage), 0, Scroller.ScrollableHeight) - Scroller.VerticalOffset) <= 2)
        {
            page = _commandedPage;
        }
        else
        {
            _commandedPage = -1;
            page = TopPageAt(Scroller.VerticalOffset);
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

    /// <summary>The slide whose centered target is closest to the current offset.</summary>
    private int NearestPresentationPage()
    {
        if (_doc is null || _doc.Pages.Count == 0)
        {
            return 1;
        }

        int best = 1;
        double bestDistance = double.MaxValue;
        for (int p = 1; p <= _doc.Pages.Count; p++)
        {
            double distance = Math.Abs(ScrollTargetFor(p) - Scroller.VerticalOffset);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = p;
            }
        }

        return best;
    }

    /// <summary>Scroll offset of the top of a 1-based page.</summary>
    private double PageTop(int pageNumber)
    {
        double offset = ContentMargin;
        for (int i = 0; i < pageNumber - 1 && i < _doc!.Pages.Count; i++)
        {
            offset += _doc.Pages[i].DisplayHeight + PageGap;
        }

        return offset;
    }

    /// <summary>Scroll target for a page — centered in the viewport while presenting.</summary>
    private double ScrollTargetFor(int pageNumber)
    {
        double target = PageTop(pageNumber);
        if (_presenting && _doc is not null)
        {
            double ph = _doc.Pages[pageNumber - 1].DisplayHeight;
            target -= Math.Max(0, (Scroller.ViewportHeight - ph) / 2);
        }

        return Math.Max(0, target);
    }

    /// <summary>The 1-based page whose top the given offset has reached.</summary>
    private int TopPageAt(double offset)
    {
        if (_doc is null || _doc.Pages.Count == 0)
        {
            return 1;
        }

        int page = 1;
        double y = ContentMargin;
        for (int i = 0; i < _doc.Pages.Count; i++)
        {
            if (y <= offset + 4)
            {
                page = i + 1;
            }
            else
            {
                break;
            }

            y += _doc.Pages[i].DisplayHeight + PageGap;
        }

        return page;
    }

    private void JumpToPage(int pageNumber, bool animate = false)
    {
        if (_doc is null)
        {
            return;
        }

        pageNumber = Math.Clamp(pageNumber, 1, _doc.Pages.Count);
        _commandedPage = pageNumber;
        Scroller.ChangeView(null, ScrollTargetFor(pageNumber), null, disableAnimation: !animate);
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
        double offset = PageTop(pageIndex + 1)
            + Math.Clamp(topFraction, 0, 1) * _doc.Pages[pageIndex].DisplayHeight;
        offset = Math.Max(0, offset - 8); // a little headroom above the target

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
        if (Math.Abs(zoom - _doc.Zoom) < 0.001)
        {
            ZoomText.Text = $"{Math.Round(_doc.Zoom * 100)}%";
            return;
        }

        // Re-zooming rescales every page height while the scroll offset stays
        // numerically the same — silently relocating the view to a different
        // page. Capture the page (and position within it) first, and restore
        // it after the reflow, so window resizes keep you where you were.
        int anchorPage = TopPageAt(Scroller.VerticalOffset);
        double heightBefore = Math.Max(1, _doc.Pages[anchorPage - 1].DisplayHeight);
        double fraction = Math.Clamp(
            (Scroller.VerticalOffset - PageTop(anchorPage)) / heightBefore, -0.5, 1.5);

        SetZoom(zoom, keepAnchor: false);
        ZoomText.Text = $"{Math.Round(_doc.Zoom * 100)}%";

        Root.UpdateLayout();
        double target = PageTop(anchorPage) + fraction * _doc.Pages[anchorPage - 1].DisplayHeight;
        Scroller.ChangeView(null, Math.Max(0, target), null, disableAnimation: true);
    }

    /// <summary>Fits the whole slide on screen (used in presentation mode) and centers it.</summary>
    private void ApplyFitPage()
    {
        if (_doc is null || _doc.Pages.Count == 0)
        {
            return;
        }

        double vw = Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : Root.ActualWidth;
        double vh = Scroller.ViewportHeight > 0 ? Scroller.ViewportHeight : Root.ActualHeight;
        double pw = _doc.Pages.Max(p => p.BaseWidth);
        double ph = _doc.Pages.Max(p => p.BaseHeight);
        if (vw <= 0 || vh <= 0 || pw <= 0 || ph <= 0)
        {
            return;
        }

        double zoom = Math.Min((vw - 24) / pw, (vh - 24) / ph);
        SetZoom(zoom, keepAnchor: false);
        ZoomText.Text = $"{Math.Round(_doc.Zoom * 100)}%";

        PagesLayout.Spacing = PageGap; // isolate each slide by a full screen
        Root.UpdateLayout();
        JumpToPage(_currentPage); // recenters via ScrollTargetFor
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
        if (_presenting)
        {
            ApplyFitPage();
        }
        else if (_fitWidthMode)
        {
            ApplyFitWidth();
        }
    }

    // ---------------------------------------------------------------- presentation mode

    private void PresentButton_Click(object sender, RoutedEventArgs e) => TogglePresentation();

    private void PresentAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_doc is null)
        {
            args.Handled = false;
            return;
        }

        args.Handled = true;
        TogglePresentation();
    }

    private void TogglePresentation()
    {
        if (_presenting)
        {
            ExitPresentation();
        }
        else if (_doc is not null)
        {
            EnterPresentation();
        }
    }

    private void EnterPresentation()
    {
        if (_presenting || _doc is null)
        {
            return;
        }

        _presenting = true;
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);

        DocumentHost.Margin = new Thickness(0);
        Scroller.Background = new SolidColorBrush(Colors.Black);
        ChromeBackground.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xF0, 0x1C, 0x1C, 0x1C));

        // Lock the view to whole slides: no scrollbars, no free scrolling.
        // Navigation is via keys/wheel, which re-center exactly on each slide.
        Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        Scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        SetChromeShown(false, animate: false);

        // One slide, fit to screen, centered — hand tool so a click still pans
        // or follows links but nothing gets drawn by accident.
        SetTool(AnnotationTool.Hand);
        Root.UpdateLayout();
        ApplyFitPage();
        Scroller.Focus(FocusState.Programmatic);

        // Switching to the full-screen presenter resizes the window a beat
        // later, so the fit above used the pre-full-screen size. Re-fit once
        // the new size has settled (Scroller_SizeChanged also re-fits, but this
        // guarantees a final authoritative center even if that doesn't fire).
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => { if (_presenting) ApplyFitPage(); });
    }

    private void ExitPresentation()
    {
        if (!_presenting)
        {
            return;
        }

        SetLaser(false); // restore the OS cursor before leaving full screen
        _presenting = false;
        AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);

        ChromeBackground.Background = new SolidColorBrush(Colors.Transparent);
        Scroller.Background = _defaultScrollerBackground;
        Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        SetChromeShown(true, animate: false);
        DocumentHost.Margin = new Thickness(0, ChromeHost.ActualHeight, 0, 0);

        PagesLayout.Spacing = PageSpacing;
        _fitWidthMode = true;
        Root.UpdateLayout();
        ApplyFitWidth();
        JumpToPage(_currentPage);
        Scroller.Focus(FocusState.Programmatic);
    }

    /// <summary>Slides the chrome (title bar + toolbar) in or out of view.</summary>
    private void SetChromeShown(bool shown, bool animate = true)
    {
        _chromeShown = shown;
        double target = shown ? 0 : -Math.Max(1, ChromeHost.ActualHeight);

        _chromeAnim?.Stop();
        if (!animate)
        {
            ChromeTransform.Y = target;
            return;
        }

        var animation = new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(160),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, ChromeTransform);
        Storyboard.SetTargetProperty(animation, "Y");
        _chromeAnim = new Storyboard();
        _chromeAnim.Children.Add(animation);
        _chromeAnim.Begin();
    }

    private void ChromeHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_presenting)
        {
            DocumentHost.Margin = new Thickness(0, ChromeHost.ActualHeight, 0, 0);
        }
        else if (!_chromeShown)
        {
            SetChromeShown(false, animate: false);
        }
    }

    private void ToggleLaser() => SetLaser(!_laserOn);

    private void SetLaser(bool on)
    {
        if (on == _laserOn)
        {
            return;
        }

        _laserOn = on;
        LaserPointer.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on)
        {
            EnsureCursorHooks(); // islands exist by now; hook them for WM_SETCURSOR

            // Place it at the cursor immediately rather than the corner, and
            // hide the arrow now (the window hook keeps it hidden thereafter).
            LaserTransform.X = _lastPointerInRoot.X - LaserPointer.Width / 2;
            LaserTransform.Y = _lastPointerInRoot.Y - LaserPointer.Height / 2;
            SetCursor(IntPtr.Zero);
        }
        // Turning off: the next pointer move restores the cursor via the
        // default window proc, so nothing to do here.
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(Root).Position;
        _lastPointerInRoot = pos; // used for N-to-add-note placement everywhere

        if (!_presenting)
        {
            return;
        }

        if (_laserOn)
        {
            LaserTransform.X = pos.X - LaserPointer.Width / 2;
            LaserTransform.Y = pos.Y - LaserPointer.Height / 2;
        }

        double y = pos.Y;

        // Hysteresis: a generous band reveals the chrome, and it stays until the
        // cursor drops well below it — so it doesn't flicker at the boundary.
        const double revealBand = 48;
        double keepBand = ChromeHost.ActualHeight + 40;
        bool show = _chromeShown ? y <= keepBand : y <= revealBand;
        if (show != _chromeShown)
        {
            SetChromeShown(show);
        }
    }

    private void Scroller_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_doc is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(Scroller);
        int delta = point.Properties.MouseWheelDelta;

        bool ctrlDown = InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);

        // While presenting, the wheel advances slides instead of scrolling
        // (which would reveal the gap between slides).
        if (_presenting && !ctrlDown)
        {
            JumpToPage(_currentPage + (delta < 0 ? 1 : -1), animate: true);
            e.Handled = true;
            return;
        }

        if (!ctrlDown)
        {
            return;
        }

        StepZoom(delta > 0 ? +1 : -1, point.Position);
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

        // Already on the hand tool: the next Escape leaves presentation.
        if (_presenting)
        {
            ExitPresentation();
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

    private void NoteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // N drops a post-it on the current page (works while presenting too);
        // don't fire while typing into a field or an open note.
        if (_doc is null || IsTextBoxFocused())
        {
            args.Handled = false;
            return;
        }

        args.Handled = true;
        ToolState.Current.RequestAddNote((uint)(_currentPage - 1), _lastPointerInRoot);
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

            // While presenting, arrows and Space advance/retreat slides (and
            // preempt the scroll viewer's own arrow-key nudge).
            case VirtualKey.Right or VirtualKey.Down or VirtualKey.Space when _presenting:
                JumpToPage(_currentPage + 1, animate: true);
                break;
            case VirtualKey.Left or VirtualKey.Up when _presenting:
                JumpToPage(_currentPage - 1, animate: true);
                break;

            case VirtualKey.L when _presenting:
                ToggleLaser();
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
