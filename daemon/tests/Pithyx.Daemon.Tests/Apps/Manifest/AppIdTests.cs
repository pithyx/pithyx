using Pithyx.Daemon.Apps.Manifest;

namespace Pithyx.Daemon.Tests.Apps.Manifest;

public sealed class AppIdTests
{
    [Theory]
    [InlineData("com.mueller.chat")]
    [InlineData("io.github.schecher1.chat")]
    [InlineData("io.gitlab.jan.photo-tools.chat")]
    [InlineData("org.pithyx.files")]
    [InlineData("de.1und1.mail")]
    public void ValidIds(string id) => Assert.True(AppId.IsValid(id));

    [Theory]
    [InlineData("files")]
    [InlineData("com.chat")]
    [InlineData("a.b.c.d.e.f")]
    [InlineData("Com.mueller.chat")]
    [InlineData("1com.mueller.chat")]
    [InlineData("com.mueller.-chat")]
    [InlineData("com..chat")]
    [InlineData("com.mueller.chat\n")]
    [InlineData("com.mueller.chat.")]
    [InlineData(null)]
    public void InvalidIds(string? id) => Assert.False(AppId.IsValid(id));

    [Fact]
    public void IdsHaveAtMostOneHundredCharacters()
    {
        Assert.True(AppId.IsValid("com." + new string('a', 91) + ".chat"));
        Assert.False(AppId.IsValid("com." + new string('a', 92) + ".chat"));
    }

    [Theory]
    [InlineData("org.pithyx.files", true)]
    [InlineData("org.pithyxx.files", false)]
    [InlineData("com.pithyx.files", false)]
    public void TheSystemNamespaceIsReserved(string id, bool reserved) => Assert.Equal(reserved, AppId.IsReserved(id));
}
