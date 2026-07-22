using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PdfReader.Services;
using PdfReader.ViewModels;
using Windows.ApplicationModel.DataTransfer;
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
    private DocumentViewModel? _doc;
    private int _currentPage = 1;
    private bool _fitWidthMode = true;

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

        // Ctrl+mouse-wheel zoom. handledEventsToo because the ScrollViewer
        // marks wheel events handled.
        Scroller.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(Scroller_PointerWheelChanged),
            handledEventsToo: true);
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

        PagesRepeater.ItemsSource = doc.Pages;
        EmptyState.Visibility = Visibility.Collapsed;

        FileNameText.Text = $"—  {file.Name}";
        Title = $"{file.Name} - Slate PDF";
        PageCountText.Text = $"/ {doc.Pages.Count}";
        PageBox.Text = "1";
        _currentPage = 1;

        PrintButton.IsEnabled = _printService.IsSupported;
        PageBox.IsEnabled = true;
        PrevPageButton.IsEnabled = true;
        NextPageButton.IsEnabled = true;
        ZoomInButton.IsEnabled = true;
        ZoomOutButton.IsEnabled = true;
        FitWidthButton.IsEnabled = true;

        _fitWidthMode = true;
        Root.UpdateLayout();
        ApplyFitWidth();
        Scroller.ChangeView(0, 0, null, disableAnimation: true);
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

    // ---------------------------------------------------------------- page navigation

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateCurrentPageFromScroll();
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
            if (FocusManager.GetFocusedElement(Root.XamlRoot) != PageBox)
            {
                PageBox.Text = page.ToString();
            }
        }
    }

    private void JumpToPage(int pageNumber)
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

        Scroller.ChangeView(null, offset, null, disableAnimation: true);
        _currentPage = pageNumber;
        PageBox.Text = pageNumber.ToString();
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

    private void SetZoom(double zoom, bool keepAnchor = true)
    {
        if (_doc is null)
        {
            return;
        }

        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        double oldZoom = _doc.Zoom;
        if (Math.Abs(zoom - oldZoom) < 0.001)
        {
            return;
        }

        // Keep the point at the viewport's center stable across the zoom change.
        double anchor = keepAnchor
            ? (Scroller.VerticalOffset + Scroller.ViewportHeight / 2) / oldZoom
            : 0;

        _doc.SetZoom(zoom);
        ZoomText.Text = $"{Math.Round(zoom * 100)}%";

        if (keepAnchor)
        {
            Root.UpdateLayout();
            double newOffset = anchor * zoom - Scroller.ViewportHeight / 2;
            Scroller.ChangeView(null, Math.Max(0, newOffset), null, disableAnimation: true);
        }
    }

    private void StepZoom(int direction)
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
        SetZoom(target);
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

        int delta = e.GetCurrentPoint(Scroller).Properties.MouseWheelDelta;
        StepZoom(delta > 0 ? +1 : -1);
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

    private async void PrintAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await PrintAsync();
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
