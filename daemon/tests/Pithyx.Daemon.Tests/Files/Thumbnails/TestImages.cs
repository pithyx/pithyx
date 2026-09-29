using SkiaSharp;

namespace Pithyx.Daemon.Tests.Files.Thumbnails;

/// <summary>Images made on the fly, so the tests need no binary fixtures.</summary>
internal static class TestImages
{
    public static byte[] Encode(SKEncodedImageFormat format, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.SteelBlue);
            using var paint = new SKPaint { Color = SKColors.Orange };
            canvas.DrawRect(0, 0, width / 2f, height / 2f, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    /// <summary>
    /// A PNG whose header claims <paramref name="width"/> by <paramref name="height"/> pixels but that holds almost no
    /// data: a few hundred bytes that would need gigabytes once decoded.
    /// </summary>
    public static byte[] PngBomb(int width, int height)
    {
        var real = Encode(SKEncodedImageFormat.Png, 1, 1);
        // IHDR data starts at byte 16 (8 signature, 4 length, 4 type): width and height, big-endian.
        var bomb = (byte[])real.Clone();
        WriteBigEndian(bomb, 16, width);
        WriteBigEndian(bomb, 20, height);
        // The chunk's CRC covers its type and data; a wrong one would be refused as broken, not as too large.
        WriteBigEndian(bomb, 29, (int)Crc32(bomb.AsSpan(12, 17)));
        return bomb;
    }

    /// <summary>A 1 by 1 GIF (Skia decodes GIF but cannot write it).</summary>
    public static readonly byte[] Gif = Convert.FromBase64String("R0lGODlhAQABAIAAAP///wAAACH5BAEAAAAALAAAAAABAAEAAAICRAEAOw==");

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }
        return ~crc;
    }

    private static void WriteBigEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    public static (int Width, int Height, SKEncodedImageFormat Format) Inspect(byte[] encoded)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(encoded));
        return (codec.Info.Width, codec.Info.Height, codec.EncodedFormat);
    }
}
