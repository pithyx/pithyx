using Vyxis.Daemon.Errors;
using Vyxis.Daemon.Files;

namespace Vyxis.Daemon.Tests.Files;

public sealed class FsPathTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("Photos", "Photos")]
    [InlineData("/Photos/2026/", "Photos/2026")]
    [InlineData("a//b", "a/b")]
    [InlineData("/etc/passwd", "etc/passwd")]
    [InlineData("name with spaces/ü.txt", "name with spaces/ü.txt")]
    public void NormalizesPathsRelativeToTheShare(string? path, string expected) =>
        Assert.Equal(expected, FsPath.Normalize(path));

    [Theory]
    [InlineData("..")]
    [InlineData("a/../../etc")]
    [InlineData("./a")]
    [InlineData("a/.")]
    [InlineData("a\0b")]
    public void RefusesDotSegmentsAndNul(string path)
    {
        var error = Assert.Throws<VyxisException>(() => FsPath.Normalize(path));
        Assert.Equal(ErrorCodes.FsPathInvalid, error.Code);
    }

    [Fact]
    public void RefusesOverlongNamesAndPaths()
    {
        Assert.Throws<VyxisException>(() => FsPath.Normalize(new string('a', 256)));
        Assert.Equal(new string('a', 255), FsPath.Normalize(new string('a', 255)));
        // 128 two-byte characters are 256 bytes.
        Assert.Throws<VyxisException>(() => FsPath.Normalize(new string('ü', 128)));
        Assert.Throws<VyxisException>(() => FsPath.Normalize(string.Join('/', Enumerable.Repeat(new string('a', 200), 21))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData(".vyxis-upload-1234")]
    [InlineData("tab\tname")]
    public void RefusesNamesThatCannotBeCreated(string name)
    {
        var error = Assert.Throws<VyxisException>(() => FsPath.CheckName(name));
        Assert.Equal(ErrorCodes.FsNameInvalid, error.Code);
    }

    [Fact]
    public void TellsWhetherAPathIsInsideAFolder()
    {
        Assert.True(FsPath.IsSameOrInside("a/b", "a"));
        Assert.True(FsPath.IsSameOrInside("a", "a"));
        Assert.False(FsPath.IsSameOrInside("ab", "a"));
        Assert.True(FsPath.IsSameOrInside("anything", ""));
    }
}
