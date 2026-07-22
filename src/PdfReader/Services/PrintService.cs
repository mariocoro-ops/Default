using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using PdfReader.ViewModels;
using Windows.Data.Pdf;
using Windows.Graphics.Printing;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace PdfReader.Services;

/// <summary>
/// Prints the open document via the standard Windows print dialog.
/// Pages are pre-rendered to bitmaps at print resolution before the dialog
/// is shown, then wrapped in XAML elements for the PrintDocument pipeline.
/// </summary>
public sealed class PrintService
{
    // Total pixel budget across all pre-rendered print pages, to keep memory
    // bounded for very long documents. DPI degrades gracefully below 150.
    private const double MaxTotalPixels = 250_000_000;
    private const double MaxDpi = 150.0;
    private const double MinDpi = 72.0;

    private readonly Window _window;
    private PrintDocument? _printDocument;
    private IPrintDocumentSource? _printSource;
    private List<BitmapImage> _pageImages = new();
    private List<UIElement> _previewPages = new();
    private string _jobName = "Document";
    private bool _printManagerRegistered;

    public PrintService(Window window)
    {
        _window = window;
    }

    public bool IsSupported => PrintManager.IsSupported();

    public async Task PrintAsync(DocumentViewModel doc)
    {
        var hwnd = WindowNative.GetWindowHandle(_window);

        if (!_printManagerRegistered)
        {
            var printManager = PrintManagerInterop.GetForWindow(hwnd);
            printManager.PrintTaskRequested += OnPrintTaskRequested;
            _printManagerRegistered = true;
        }

        _jobName = doc.FileName;
        _pageImages = await RenderPagesForPrintAsync(doc);

        // A fresh PrintDocument per print session; the previous one (if any)
        // is dropped along with its event handlers.
        _printDocument = new PrintDocument();
        _printDocument.Paginate += OnPaginate;
        _printDocument.GetPreviewPage += OnGetPreviewPage;
        _printDocument.AddPages += OnAddPages;
        _printSource = _printDocument.DocumentSource;

        await PrintManagerInterop.ShowPrintUIForWindowAsync(hwnd);
    }

    private static async Task<List<BitmapImage>> RenderPagesForPrintAsync(DocumentViewModel doc)
    {
        // Pick a DPI that keeps the total pixel count under budget.
        double totalSquareInches = doc.Pages.Sum(p => (p.BaseWidth / 96.0) * (p.BaseHeight / 96.0));
        double dpi = Math.Clamp(Math.Sqrt(MaxTotalPixels / Math.Max(totalSquareInches, 1)), MinDpi, MaxDpi);

        var images = new List<BitmapImage>(doc.Pages.Count);
        foreach (var pageVm in doc.Pages)
        {
            uint pixelWidth = (uint)Math.Max(1, Math.Round(pageVm.BaseWidth * dpi / 96.0));
            using var page = doc.Document.GetPage(pageVm.Index);
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions
            {
                DestinationWidth = pixelWidth,
            });

            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            images.Add(bitmap);
        }

        return images;
    }

    private void OnPrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        var printTask = args.Request.CreatePrintTask(_jobName, sourceArgs =>
        {
            sourceArgs.SetSource(_printSource);
        });

        printTask.Completed += (_, _) =>
        {
            // Release print bitmaps once the job is done (or cancelled).
            _window.DispatcherQueue.TryEnqueue(() =>
            {
                _pageImages = new List<BitmapImage>();
                _previewPages = new List<UIElement>();
            });
        };
    }

    private void OnPaginate(object sender, PaginateEventArgs e)
    {
        var description = e.PrintTaskOptions.GetPageDescription(0);

        _previewPages = new List<UIElement>(_pageImages.Count);
        foreach (var image in _pageImages)
        {
            _previewPages.Add(BuildPrintPage(image, description));
        }

        _printDocument?.SetPreviewPageCount(_previewPages.Count, PreviewPageCountType.Final);
    }

    private void OnGetPreviewPage(object sender, GetPreviewPageEventArgs e)
    {
        if (_printDocument is null || e.PageNumber < 1 || e.PageNumber > _previewPages.Count)
        {
            return;
        }

        _printDocument.SetPreviewPage(e.PageNumber, _previewPages[e.PageNumber - 1]);
    }

    private void OnAddPages(object sender, AddPagesEventArgs e)
    {
        if (_printDocument is null)
        {
            return;
        }

        // Build fresh elements for the final output — a UIElement can only
        // live in one visual tree, and the preview surface owns _previewPages.
        var description = e.PrintTaskOptions.GetPageDescription(0);
        foreach (var image in _pageImages)
        {
            _printDocument.AddPage(BuildPrintPage(image, description));
        }

        _printDocument.AddPagesComplete();
    }

    private static UIElement BuildPrintPage(BitmapImage image, PrintPageDescription description)
    {
        var page = new Grid
        {
            Width = description.PageSize.Width,
            Height = description.PageSize.Height,
            Background = new SolidColorBrush(Colors.White),
        };

        var content = new Image
        {
            Source = image,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            // Keep the content inside the printer's imageable area.
            Margin = new Thickness(
                description.ImageableRect.X,
                description.ImageableRect.Y,
                Math.Max(0, description.PageSize.Width - description.ImageableRect.X - description.ImageableRect.Width),
                Math.Max(0, description.PageSize.Height - description.ImageableRect.Y - description.ImageableRect.Height)),
        };

        page.Children.Add(content);
        return page;
    }
}
