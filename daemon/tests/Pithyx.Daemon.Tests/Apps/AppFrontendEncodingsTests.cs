using Pithyx.Daemon.Apps;

namespace Pithyx.Daemon.Tests.Apps;

/// <summary>Which encoding a browser accepts and which frontend files may be cached for a year (N14).</summary>
public sealed class AppFrontendEncodingsTests
{
    [Theory]
    [InlineData("gzip, deflate, br, zstd", "br", true)]
    [InlineData("gzip, deflate, br, zstd", "gzip", true)]
    [InlineData("gzip;q=1.0, br;q=0", "br", false)]
    [InlineData("gzip;q=1.0, br;q=0.000", "br", false)]
    [InlineData("br;q=0.5", "br", true)]
    [InlineData("*", "br", true)]
    [InlineData("*;q=0", "gzip", false)]
    [InlineData("*, br;q=0", "br", false)]
    [InlineData("identity", "br", false)]
    [InlineData("", "gzip", false)]
    [InlineData("brotli", "br", false)]
    public void AcceptsOnlyWhatTheBrowserAllows(string header, string token, bool expected) =>
        Assert.Equal(expected, AppFrontendEncodings.Accepts(header, token));

    [Theory]
    [InlineData("assets/index-B4kz_x9Q.js", true)]
    [InlineData("assets/index-a-c3dEfg.js", true)]
    [InlineData("assets/chunks/vendor-Dk3j9sLa.css", true)]
    [InlineData("assets/logo-0b1c2d3e.svg", true)]
    [InlineData("assets/my-elements.js", false)]
    [InlineData("assets/component.js", false)]
    [InlineData("assets/index.js", false)]
    [InlineData("index-B4kz_x9Q.js", false)]
    [InlineData("lib/assets/index-B4kz_x9Q.js", false)]
    [InlineData("index.html", false)]
    public void OnlyBuildHashedAssetsAreCachedForAYear(string path, bool expected) =>
        Assert.Equal(expected, AppFrontendEncodings.IsHashed(path));
}
