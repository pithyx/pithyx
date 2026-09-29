using SkiaSharp;

namespace Pithyx.Daemon.Files.Thumbnails;

/// <summary>Why no thumbnail was made; the file itself is fine to download.</summary>
public enum ThumbnailRefusal
{
    /// <summary>Not a JPEG, PNG, WebP or GIF by its content, whatever its name says.</summary>
    Unsupported,

    /// <summary>The file or its pixels exceed the limits (a decompression bomb looks like this).</summary>
    TooLarge,

    /// <summary>The image could not be decoded.</summary>
    Broken,
}

/// <summary>A file that gets no thumbnail, and why.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Always carries a refusal.")]
public sealed class ThumbnailRefusedException(ThumbnailRefusal refusal) : Exception($"No thumbnail: {refusal}")
{
    public ThumbnailRefusal Refusal { get; } = refusal;
}

/// <summary>
/// Makes WebP thumbnails of JPEG, PNG, WebP and GIF images with SkiaSharp (MIT; Skia and its codecs are BSD-style),
/// whose native library ships in the package and needs no system packages. The format is taken from the content,
/// never from the name. Before any pixel is decoded, the header's dimensions are checked against
/// <see cref="MaxPixels"/>, so a small file that claims a huge image is refused instead of filling memory; JPEG and
/// WebP are decoded at a reduced scale when the codec can.
/// </summary>
public static class ThumbnailRenderer
{
    /// <summary>Files larger than this are not read at all.</summary>
    public const long MaxFileBytes = 256L * 1024 * 1024;

    /// <summary>At most 50 megapixels are decoded (200 MB at 4 bytes per pixel, briefly, one image at a time).</summary>
    public const long MaxPixels = 50_000_000;

    /// <summary>Longest side of an image in pixels; wider than any camera.</summary>
    public const int MaxEdge = 30_000;

    private static readonly HashSet<SKEncodedImageFormat> _formats = [SKEncodedImageFormat.Jpeg, SKEncodedImageFormat.Png, SKEncodedImageFormat.Webp, SKEncodedImageFormat.Gif];

    /// <param name="input">The image, readable and seekable.</param>
    /// <param name="length">Its size in bytes.</param>
    /// <param name="edge">Longest side of the thumbnail; smaller images are not enlarged.</param>
    /// <returns>The thumbnail as WebP.</returns>
    /// <exception cref="ThumbnailRefusedException">The file is not a supported image, too large or broken.</exception>
    public static byte[] Render(Stream input, long length, int edge)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (length > MaxFileBytes)
        {
            throw new ThumbnailRefusedException(ThumbnailRefusal.TooLarge);
        }
        using var data = SKData.Create(input);
        if (data is null)
        {
            throw new ThumbnailRefusedException(ThumbnailRefusal.Broken);
        }
        using var codec = SKCodec.Create(new SKMemoryStream(data), out var result);
        if (codec is null)
        {
            throw new ThumbnailRefusedException(result == SKCodecResult.Unimplemented ? ThumbnailRefusal.Unsupported : ThumbnailRefusal.Broken);
        }
        if (!_formats.Contains(codec.EncodedFormat))
        {
            throw new ThumbnailRefusedException(ThumbnailRefusal.Unsupported);
        }
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0)
        {
            throw new ThumbnailRefusedException(ThumbnailRefusal.Broken);
        }
        if (info.Width > MaxEdge || info.Height > MaxEdge || (long)info.Width * info.Height > MaxPixels)
        {
            throw new ThumbnailRefusedException(ThumbnailRefusal.TooLarge);
        }

        // The smallest size the codec can decode to that is still at least the thumbnail (JPEG: 1/2, 1/4, 1/8).
        var scale = Math.Min(1f, (float)edge / Math.Max(info.Width, info.Height));
        var decodeSize = codec.GetScaledDimensions(scale);
        if (decodeSize.Width < 1 || decodeSize.Height < 1 || Math.Max(decodeSize.Width, decodeSize.Height) < Math.Min(edge, Math.Max(info.Width, info.Height)))
        {
            decodeSize = info.Size;
        }
        var decodeInfo = new SKImageInfo(decodeSize.Width, decodeSize.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var decoded = new SKBitmap(decodeInfo);
        var decodedResult = codec.GetPixels(decodeInfo, decoded.GetPixels());
        if (decodedResult is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            throw new ThumbnailRefusedException(ThumbnailRefusal.Broken);
        }

        var (width, height) = Fit(decodeSize.Width, decodeSize.Height, edge);
        using var resized = decoded.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell))
            ?? throw new ThumbnailRefusedException(ThumbnailRefusal.Broken);
        using var oriented = Orient(resized, codec.EncodedOrigin);
        using var image = SKImage.FromBitmap(oriented);
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, 80)
            ?? throw new ThumbnailRefusedException(ThumbnailRefusal.Broken);
        return encoded.ToArray();
    }

    /// <summary>The size that fits into an <paramref name="edge"/> square, keeping the ratio; never larger than the image.</summary>
    public static (int Width, int Height) Fit(int width, int height, int edge)
    {
        var longest = Math.Max(width, height);
        if (longest <= edge)
        {
            return (width, height);
        }
        var factor = (double)edge / longest;
        return (Math.Max(1, (int)Math.Round(width * factor)), Math.Max(1, (int)Math.Round(height * factor)));
    }

    /// <summary>Applies the EXIF orientation, so photos taken on their side show upright.</summary>
    private static SKBitmap Orient(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return bitmap.Copy();
        }
        var (w, h) = (bitmap.Width, bitmap.Height);
        // Where pixel (x, y) of the stored image goes: x' = a*x + b*y + c, y' = d*x + e*y + f.
        var (a, b, c, d, e, f) = origin switch
        {
            SKEncodedOrigin.TopRight => (-1f, 0f, w, 0f, 1f, 0f),
            SKEncodedOrigin.BottomRight => (-1f, 0f, w, 0f, -1f, h),
            SKEncodedOrigin.BottomLeft => (1f, 0f, 0f, 0f, -1f, h),
            SKEncodedOrigin.LeftTop => (0f, 1f, 0f, 1f, 0f, 0f),
            SKEncodedOrigin.RightTop => (0f, -1f, h, 1f, 0f, 0f),
            SKEncodedOrigin.RightBottom => (0f, -1f, h, -1f, 0f, w),
            _ => (0f, 1f, 0f, -1f, 0f, (float)w),
        };
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var result = new SKBitmap(swap ? h : w, swap ? w : h, bitmap.ColorType, bitmap.AlphaType);
        using var canvas = new SKCanvas(result);
        canvas.SetMatrix(new SKMatrix(a, b, c, d, e, f, 0, 0, 1));
        using var source = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(source, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        return result;
    }
}
