using Pithyx.Daemon.Auth;

namespace Pithyx.Daemon.Tests.Auth;

public sealed class RecoveryKeyTests
{
    [Fact]
    public void NewKeysCarryThePrefixAndTwentySixCodeCharacters()
    {
        var key = AuthService.NewRecoveryKey();

        Assert.Matches("^PTHX-[0-9A-HJKMNP-TV-Z]{4}(-[0-9A-HJKMNP-TV-Z]{4}){5}-[0-9A-HJKMNP-TV-Z]{2}$", key);
        Assert.Equal(26, AuthService.NormalizeRecoveryKey(key).Length);
    }

    [Theory]
    [InlineData("PTHX-7KQ2-MZ9P-3HT8-WB6N-R2DC-5FJX-Q8")]
    [InlineData("pthx 7kq2 mz9p 3ht8 wb6n r2dc 5fjx q8")]
    [InlineData("7KQ2-MZ9P-3HT8-WB6N-R2DC-5FJX-Q8")]
    [InlineData("7kq2mz9p3ht8wb6nr2dc5fjxq8")]
    public void KeysNormalizeTheSameWithAndWithoutThePrefix(string typed)
    {
        Assert.Equal("7KQ2MZ9P3HT8WB6NR2DC5FJXQ8", AuthService.NormalizeRecoveryKey(typed));
    }

    [Fact]
    public void AKeyThatHappensToStartWithThePrefixLettersKeepsThem()
    {
        Assert.Equal("PTHXMZ9P3HT8WB6NR2DC5FQ8AB", AuthService.NormalizeRecoveryKey("PTHX-MZ9P-3HT8-WB6N-R2DC-5FQ8-AB"));
        Assert.Equal("PTHXMZ9P3HT8WB6NR2DC5FQ8AB", AuthService.NormalizeRecoveryKey("PTHX-PTHX-MZ9P-3HT8-WB6N-R2DC-5FQ8-AB"));
    }
}
