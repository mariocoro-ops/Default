using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PdfReader.Services;

/// <summary>
/// Persists the user's signature image. On import the image is downscaled if
/// huge and near-white pixels are made transparent, so a signature scanned on
/// white paper stamps cleanly over document content. Stored as a PNG under
/// %LocalAppData%\SlatePdf (works packaged and unpackaged).
/// </summary>
public static class SignatureStore
{
    private const int MaxDimension = 1200;
    private const byte WhiteThreshold = 235;

    private static readonly string Directory = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SlatePdf");

    private static byte[]? _bytes;
    private static double _aspect = 2.5;
    private static bool _aspectKnown;
    private static BitmapImage? _bitmap;

    static SignatureStore()
    {
        try
        {
            if (System.IO.File.Exists(ImagePath))
            {
                _bytes = System.IO.File.ReadAllBytes(ImagePath);
            }
        }
        catch
        {
            // no saved signature
        }
    }

    public static string ImagePath => System.IO.Path.Combine(Directory, "signature.png");

    public static bool HasSignature => _bytes is not null;

    /// <summary>Width / height of the stored image.</summary>
    public static double AspectRatio => _aspect;

    /// <summary>The decoded overlay image, if <see cref="GetBitmapAsync"/> has run.</summary>
    public static BitmapImage? CachedBitmap => _bitmap;

    public static async Task ImportAsync(StorageFile file, bool removeWhiteBackground = true)
    {
        using var input = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(input);

        double scale = Math.Min(1.0, (double)MaxDimension / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };

        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);
        byte[] pixels = pixelData.DetachPixelData();

        if (removeWhiteBackground)
        {
            for (int i = 0; i < pixels.Length; i += 4)
            {
                // BGRA: paper-white becomes transparent so the ink floats.
                if (pixels[i] >= WhiteThreshold && pixels[i + 1] >= WhiteThreshold && pixels[i + 2] >= WhiteThreshold)
                {
                    pixels[i + 3] = 0;
                }
            }
        }

        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            transform.ScaledWidth,
            transform.ScaledHeight,
            96,
            96,
            pixels);
        await encoder.FlushAsync();

        var bytes = new byte[(int)output.Size];
        output.Seek(0);
        await output.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);

        System.IO.Directory.CreateDirectory(Directory);
        await System.IO.File.WriteAllBytesAsync(ImagePath, bytes);

        _bytes = bytes;
        _aspect = (double)transform.ScaledWidth / transform.ScaledHeight;
        _aspectKnown = true;
        _bitmap = null; // re-decode on next request
    }

    public static async Task<BitmapImage?> GetBitmapAsync()
    {
        if (_bytes is null)
        {
            return null;
        }

        if (_bitmap is not null)
        {
            return _bitmap;
        }

        await EnsureAspectAsync();

        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(_bytes.AsBuffer());
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        _bitmap = bitmap;
        return bitmap;
    }

    private static async Task EnsureAspectAsync()
    {
        if (_aspectKnown || _bytes is null)
        {
            return;
        }

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(_bytes.AsBuffer());
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            _aspect = (double)decoder.PixelWidth / decoder.PixelHeight;
            _aspectKnown = true;
        }
        catch
        {
            // keep the default aspect
        }
    }

    public static void Clear()
    {
        _bytes = null;
        _bitmap = null;
        _aspectKnown = false;
        try
        {
            System.IO.File.Delete(ImagePath);
        }
        catch
        {
            // best effort
        }
    }
}
