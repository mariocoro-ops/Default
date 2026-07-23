using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PdfReader.ViewModels;

/// <summary>
/// The open PDF document: the underlying PdfDocument plus one PageViewModel
/// per page. The file is read fully into memory and the renderer is fed from
/// that buffer, so the original file is never locked — saving over it always
/// works — and the same bytes feed PdfPig (text geometry) and PDFsharp (save).
/// Page sizes are read up front (cheap — no rasterization) so the scroll
/// extent is correct before any page has rendered.
/// </summary>
public sealed class DocumentViewModel
{
    // Keeps the renderer's backing stream alive for the document's lifetime.
    private readonly InMemoryRandomAccessStream _renderStream;

    private DocumentViewModel(
        PdfDocument document,
        StorageFile file,
        byte[] sourceBytes,
        InMemoryRandomAccessStream renderStream)
    {
        Document = document;
        File = file;
        SourceBytes = sourceBytes;
        _renderStream = renderStream;
    }

    public PdfDocument Document { get; }

    public StorageFile File { get; }

    public byte[] SourceBytes { get; }

    public string FileName => File.Name;

    public ObservableCollection<PageViewModel> Pages { get; } = new();

    public double Zoom { get; private set; } = 1.0;

    public static async Task<DocumentViewModel> LoadAsync(StorageFile file)
    {
        var buffer = await FileIO.ReadBufferAsync(file);
        byte[] bytes = buffer.ToArray();

        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);

        var document = await PdfDocument.LoadFromStreamAsync(stream);
        var vm = new DocumentViewModel(document, file, bytes, stream);

        for (uint i = 0; i < document.PageCount; i++)
        {
            using var page = document.GetPage(i);
            var size = page.Size;
            // PdfPage.Size is in PDF points (1/72"); XAML layout uses DIPs (1/96").
            vm.Pages.Add(new PageViewModel(
                document,
                i,
                size.Width * 96.0 / 72.0,
                size.Height * 96.0 / 72.0));
        }

        return vm;
    }

    public void SetZoom(double zoom)
    {
        Zoom = zoom;
        foreach (var page in Pages)
        {
            page.Zoom = zoom;
        }
    }

    public void SetRasterizationScale(double scale)
    {
        foreach (var page in Pages)
        {
            page.RasterizationScale = scale;
        }
    }
}
