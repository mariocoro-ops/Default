using System.Collections.ObjectModel;
using Windows.Data.Pdf;
using Windows.Storage;

namespace PdfReader.ViewModels;

/// <summary>
/// The open PDF document: the underlying PdfDocument plus one PageViewModel
/// per page. Page sizes are read up front (cheap — no rasterization) so the
/// scroll extent is correct before any page has rendered.
/// </summary>
public sealed class DocumentViewModel
{
    private DocumentViewModel(PdfDocument document, StorageFile file)
    {
        Document = document;
        File = file;
    }

    public PdfDocument Document { get; }

    public StorageFile File { get; }

    public string FileName => File.Name;

    public ObservableCollection<PageViewModel> Pages { get; } = new();

    public double Zoom { get; private set; } = 1.0;

    public static async Task<DocumentViewModel> LoadAsync(StorageFile file)
    {
        var document = await PdfDocument.LoadFromFileAsync(file);
        var vm = new DocumentViewModel(document, file);

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
