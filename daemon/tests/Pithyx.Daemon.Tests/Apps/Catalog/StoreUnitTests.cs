using Pithyx.Daemon.Apps.Catalog;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Tests.Apps.Catalog;

/// <summary>The pure parts of the Store: version ranges, catalog URLs, git's failures and the check time.</summary>
public sealed class StoreUnitTests
{
    [Theory]
    [InlineData("0.1.0", "*", true)]
    [InlineData("0.1.0", ">=0.1.0", true)]
    [InlineData("0.1.0-dev", ">=0.1.0", false)]
    [InlineData("0.1.0-dev", ">=0.1.0-0", true)]
    [InlineData("0.2.0-beta.1", ">=0.1.0", true)]
    [InlineData("0.1.5", "^0.1.0", true)]
    [InlineData("0.2.0", "^0.1.0", false)]
    [InlineData("1.4.2", "^1.2", true)]
    [InlineData("2.0.0-rc.1", "^1.2", false)]
    [InlineData("1.2.9", "~1.2.3", true)]
    [InlineData("1.3.0", "~1.2.3", false)]
    [InlineData("1.5.0", "1.x", true)]
    [InlineData("2.0.0", "1", false)]
    [InlineData("1.2.0", ">1.1", true)]
    [InlineData("1.1.9", ">1.1", false)]
    [InlineData("1.1.9", "<=1.1", true)]
    [InlineData("1.2.0", "<1.2", false)]
    [InlineData("2.3.4", "1.2 - 2.3.4", true)]
    [InlineData("2.3.5", "1.2 - 2.3.4", false)]
    [InlineData("2.9.0", "1.2 - 2", true)]
    [InlineData("3.0.0", "<1.0.0 || >=3.0.0", true)]
    [InlineData("1.5.0", ">=1.0.0 <1.5.0", false)]
    public void RangesUsePlainPrecedence(string version, string range, bool expected) =>
        Assert.Equal(expected, VersionRange.Satisfies(version, range));

    [Theory]
    [InlineData("https://github.com/pithyx/apps-official.git", false, "github.com", 0)]
    [InlineData("https://GitHub.com/pithyx/apps-official/", false, "github.com", 0)]
    [InlineData("git@github.com:pithyx/catalog-test.git", true, "github.com", 22)]
    [InlineData("ssh://git@git.example.com:2222/apps.git", true, "git.example.com", 2222)]
    public void AcceptsHttpsAndSsh(string url, bool ssh, string host, int port)
    {
        var parsed = CatalogUrl.Parse(url);
        Assert.Equal(ssh, parsed.Ssh);
        Assert.Equal(host, parsed.Host);
        Assert.Equal(port, parsed.Port);
    }

    [Theory]
    [InlineData("http://github.com/pithyx/apps.git", "scheme")]
    [InlineData("file:///srv/catalog", "scheme")]
    [InlineData("https://user:secret@github.com/pithyx/apps.git", "host")]
    [InlineData("https://token@github.com/pithyx/apps.git", "host")]
    [InlineData("https://github.com", "path")]
    [InlineData("https://github.com/a b", "path")]
    [InlineData("ext::sh -c touch% /tmp/pwned", "path")]
    [InlineData("--upload-pack=touch /tmp/x", "path")]
    public void RefusesOtherUrls(string url, string reason)
    {
        var problem = Assert.Throws<PithyxException>(() => CatalogUrl.Parse(url));
        Assert.Equal(ErrorCodes.StoreCatalogUrlInvalid, problem.Code);
        Assert.Equal(reason, problem.Parameters["reason"]);
    }

    [Fact]
    public void AcceptsLocalFoldersOnlyWhereAllowed()
    {
        Assert.Equal("file:///srv/catalog", CatalogUrl.Parse("file:///srv/catalog", allowFile: true).Url);
        Assert.Throws<PithyxException>(() => CatalogUrl.Parse("file:///srv/catalog"));
    }

    [Fact]
    public void PinsGitHubsKeysAndOtherwiseTheStoredOne()
    {
        Assert.Equal(3, CatalogGit.KnownHosts(CatalogUrl.Parse("git@github.com:a/b.git"), null).Count);
        Assert.Empty(CatalogGit.KnownHosts(CatalogUrl.Parse("git@git.example.com:a/b.git"), null));
        Assert.Equal(["git.example.com ssh-ed25519 AAAA"], CatalogGit.KnownHosts(CatalogUrl.Parse("git@git.example.com:a/b.git"), "git.example.com ssh-ed25519 AAAA"));
    }

    [Theory]
    [InlineData("Host key verification failed.\nfatal: Could not read from remote repository.", "host_key")]
    [InlineData("git@github.com: Permission denied (publickey).\nfatal: Could not read from remote repository.", "auth")]
    [InlineData("remote: Repository not found.\nfatal: repository 'https://github.com/x/y.git/' not found", "auth")]
    [InlineData("fatal: couldn't find remote ref catalog-next", "branch")]
    [InlineData("fatal: unable to access 'https://x/': Could not resolve host: x", "timeout")]
    [InlineData("fatal: something else", "failed")]
    public void ClassifiesGitFailures(string standardError, string reason) =>
        Assert.Equal(reason, CatalogGit.Classify(standardError));

    [Fact]
    public void TheNextCheckIsTheSetTimePlusTheBoxsDelay()
    {
        var zone = TimeZoneInfo.Utc;
        var now = new DateTimeOffset(2026, 10, 8, 22, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 3, 37, 12, TimeSpan.Zero), StoreService.NextCheck("03:00", (37 * 60) + 12, now, zone));
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 22, 5, 0, TimeSpan.Zero), StoreService.NextCheck("21:30", 35 * 60, now, zone));
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 0, 20, 0, TimeSpan.Zero), StoreService.NextCheck("23:50", 30 * 60, now, zone));
        Assert.Null(StoreService.ParseTime("24:00"));
        Assert.Null(StoreService.ParseTime("3:00"));
        Assert.Equal(185, StoreService.ParseTime("03:05"));
    }
}
