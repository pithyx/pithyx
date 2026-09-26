using Vyxis.Daemon.Auth;

namespace Vyxis.Daemon.Tests.Auth;

public sealed class PasswordHasherTests : IDisposable
{
    private readonly PasswordHasher _hasher = new();

    public void Dispose() => _hasher.Dispose();

    [Fact]
    public async Task HashVerifiesOnlyTheSamePassword()
    {
        var hash = await _hasher.HashAsync("correct horse battery", TestContext.Current.CancellationToken);

        Assert.StartsWith("$argon2id$v=19$m=65536,t=3,p=1$", hash, StringComparison.Ordinal);
        Assert.Equal((true, false), await _hasher.VerifyAsync("correct horse battery", hash, TestContext.Current.CancellationToken));
        Assert.False((await _hasher.VerifyAsync("correct horse batterY", hash, TestContext.Current.CancellationToken)).Valid);
    }

    [Fact]
    public async Task HashesWithWeakerParametersAskForARehash()
    {
        var weak = await _hasher.HashAsync("correct horse battery", 19 * 1024, 2, TestContext.Current.CancellationToken);

        Assert.Equal((true, true), await _hasher.VerifyAsync("correct horse battery", weak, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("$argon2i$v=19$m=65536,t=3,p=1$AAAA$AAAA")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=4$AAAA$AAAA")]
    [InlineData("not a hash")]
    public async Task MalformedHashesNeverVerify(string hash) =>
        Assert.False((await _hasher.VerifyAsync("anything", hash, TestContext.Current.CancellationToken)).Valid);
}
