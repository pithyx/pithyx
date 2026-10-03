using System.Net;
using Pithyx.Daemon.Files;
using Pithyx.Daemon.FileServices;
using Pithyx.Daemon.FileServices.Ftp;

namespace Pithyx.Daemon.Tests.FileServices;

public sealed class ProtocolFormatTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("/", "Media", "/Media")]
    [InlineData("/Media", "photos/2026", "/Media/photos/2026")]
    [InlineData("/Media/photos", "..", "/Media")]
    [InlineData("/Media", "../../..", "/")]
    [InlineData("/Media", "/Docs/./a", "/Docs/a")]
    [InlineData("/Media", "", "/Media")]
    public void PathsAreResolvedFromTheCurrentFolderAndNeverLeaveTheRoot(string current, string argument, string expected) =>
        Assert.Equal(expected, FtpFormat.Combine(current, argument));

    [Fact]
    public void CommandsListingsAndRepliesHaveTheirFormats()
    {
        Assert.Equal(("RETR", "a b.txt"), FtpFormat.Parse("retr a b.txt\r\n"));
        Assert.Equal(("PWD", ""), FtpFormat.Parse("PWD"));
        Assert.Equal("\"/say \"\"hi\"\"\"", FtpFormat.Quote("/say \"hi\""));
        var file = new FileEntry("song.mp3", FileKind.File, 1234, new DateTimeOffset(2026, 9, 30, 8, 5, 0, TimeSpan.Zero));
        Assert.Equal("-rw-r--r-- 1 pithyx pithyx         1234 Sep 30 08:05 song.mp3", FtpFormat.ListLine(file, _now));
        Assert.Equal("drwxr-xr-x 1 pithyx pithyx            0 Jan 01  2020 old", FtpFormat.ListLine(new FileEntry("old", FileKind.Folder, 0, new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)), _now));
        Assert.Equal("type=file;size=1234;modify=20260930080500; song.mp3", FtpFormat.Facts(file, "song.mp3"));
        Assert.Equal("227 Entering Passive Mode (192,168,1,10,195,80).", FtpFormat.PassiveReply(IPAddress.Parse("::ffff:192.168.1.10"), 50000));
        Assert.Equal("229 Entering Extended Passive Mode (|||50001|).", FtpFormat.ExtendedPassiveReply(50001));
    }

    [Fact]
    public void WebDavAnswersEscapeNamesAndDescribeFilesAndFolders()
    {
        Assert.Equal("/webdav/", WebDavXml.Href("/", folder: true));
        Assert.Equal("/webdav/My%20Media/a%23b.txt", WebDavXml.Href("/My Media/a#b.txt", folder: false));
        var xml = WebDavXml.Multistatus(
        [
            new WebDavResource("/webdav/Media/", new FileEntry("Media", FileKind.Folder, 0, _now)),
            new WebDavResource("/webdav/Media/a.txt", new FileEntry("a.txt", FileKind.File, 5, _now)),
        ]);
        Assert.Contains("<D:href>/webdav/Media/</D:href>", xml, StringComparison.Ordinal);
        Assert.Contains("<D:collection />", xml, StringComparison.Ordinal);
        Assert.Contains("<D:getcontentlength>5</D:getcontentlength>", xml, StringComparison.Ordinal);
        Assert.Contains("<D:getcontenttype>text/plain</D:getcontenttype>", xml, StringComparison.Ordinal);
        Assert.Contains("Sat, 03 Oct 2026 12:00:00 GMT", xml, StringComparison.Ordinal);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml, StringComparison.Ordinal);
        var patched = WebDavXml.PropPatched("/webdav/Media/a.txt", "<?xml version=\"1.0\"?><D:propertyupdate xmlns:D=\"DAV:\" xmlns:Z=\"urn:schemas-microsoft-com:\"><D:set><D:prop><Z:Win32LastModifiedTime>x</Z:Win32LastModifiedTime></D:prop></D:set></D:propertyupdate>");
        Assert.Contains("Win32LastModifiedTime", patched, StringComparison.Ordinal);
        Assert.Contains("HTTP/1.1 200 OK", patched, StringComparison.Ordinal);
        Assert.Equal(404, WebDav.StatusOf("fs.share.not_found", "GET"));
        Assert.Equal(403, WebDav.StatusOf("fs.share.read_only", "PUT"));
        Assert.Equal(405, WebDav.StatusOf("fs.path.invalid", "MKCOL"));
    }
}
