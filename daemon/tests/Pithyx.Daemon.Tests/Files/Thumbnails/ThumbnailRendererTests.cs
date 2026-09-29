using System.Text;
using Pithyx.Daemon.Files.Thumbnails;
using SkiaSharp;

namespace Pithyx.Daemon.Tests.Files.Thumbnails;

public sealed class ThumbnailRendererTests
{
    private static byte[] Render(byte[] content, int edge = 256, long? length = null)
    {
        using var stream = new MemoryStream(content);
        return ThumbnailRenderer.Render(stream, length ?? content.Length, edge);
    }

    private static ThumbnailRefusal Refused(byte[] content, long? length = null) =>
        Assert.Throws<ThumbnailRefusedException>(() => Render(content, length: length)).Refusal;

    [Theory]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    [InlineData(SKEncodedImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Webp)]
    public void MakesAWebpThatFitsTheEdgeAndKeepsTheRatio(SKEncodedImageFormat format)
    {
        var thumbnail = Render(TestImages.Encode(format, 1200, 600));

        Assert.Equal((256, 128, SKEncodedImageFormat.Webp), TestImages.Inspect(thumbnail));
    }

    [Fact]
    public void AGifGetsAThumbnailToo() => Assert.Equal((1, 1, SKEncodedImageFormat.Webp), TestImages.Inspect(Render(TestImages.Gif)));

    [Fact]
    public void NeverEnlargesASmallImage() =>
        Assert.Equal((40, 30, SKEncodedImageFormat.Webp), TestImages.Inspect(Render(TestImages.Encode(SKEncodedImageFormat.Png, 40, 30))));

    [Theory]
    [InlineData(40_000, 40_000)]
    [InlineData(31_000, 10)]
    [InlineData(10_000, 6_000)]
    public void RefusesADecompressionBombBeforeDecodingIt(int width, int height) =>
        Assert.Equal(ThumbnailRefusal.TooLarge, Refused(TestImages.PngBomb(width, height)));

    [Fact]
    public void RefusesAFileThatIsTooLargeWithoutReadingIt() =>
        Assert.Equal(ThumbnailRefusal.TooLarge, Refused([1, 2, 3], length: ThumbnailRenderer.MaxFileBytes + 1));

    [Fact]
    public void RefusesContentThatIsNoImageWhateverItsName()
    {
        Assert.NotEqual(ThumbnailRefusal.TooLarge, Refused(Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>")));
        Assert.NotEqual(
            ThumbnailRefusal.TooLarge,
            Refused(Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"><rect width=\"10\" height=\"10\"/></svg>")));
    }

    [Fact]
    public void RefusesFormatsItDoesNotRender()
    {
        var png = TestImages.Encode(SKEncodedImageFormat.Png, 8, 8);
        // ICO decodes in Skia, but only the four web formats get thumbnails.
        Assert.Equal(ThumbnailRefusal.Unsupported, Refused(IcoOf(png)));
    }

    [Fact]
    public void RefusesABrokenImage()
    {
        var jpeg = TestImages.Encode(SKEncodedImageFormat.Jpeg, 400, 300);
        Assert.Equal(ThumbnailRefusal.Broken, Refused([.. jpeg.Take(20)]));
        Assert.Equal(ThumbnailRefusal.Broken, Refused([0xFF, 0xD8, 0xFF, 0x00, 0x01]));
    }

    [Fact]
    public void ACutOffImageStillGetsAThumbnailOfWhatIsThere()
    {
        var png = TestImages.Encode(SKEncodedImageFormat.Png, 600, 400);

        var thumbnail = Render([.. png.Take(png.Length * 2 / 3)]);

        Assert.Equal(SKEncodedImageFormat.Webp, TestImages.Inspect(thumbnail).Format);
    }

    [Theory]
    [InlineData(1000, 500, 256, 256, 128)]
    [InlineData(500, 1000, 256, 128, 256)]
    [InlineData(100, 50, 256, 100, 50)]
    [InlineData(10_000, 1, 64, 64, 1)]
    public void FitsIntoTheSquare(int width, int height, int edge, int fitWidth, int fitHeight) =>
        Assert.Equal((fitWidth, fitHeight), ThumbnailRenderer.Fit(width, height, edge));

    /// <summary>A one-image ICO file around a PNG.</summary>
    private static byte[] IcoOf(byte[] png)
    {
        var header = new byte[] { 0, 0, 1, 0, 1, 0, 8, 8, 0, 0, 1, 0, 32, 0 };
        var size = BitConverter.GetBytes(png.Length);
        var offset = BitConverter.GetBytes(22);
        return [.. header, .. size, .. offset, .. png];
    }
}
