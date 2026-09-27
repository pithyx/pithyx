using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Tests.Auth;

public sealed class CredentialRulesTests
{
    [Theory]
    [InlineData("admin", "admin")]
    [InlineData(" Gabriel ", "gabriel")]
    [InlineData("_svc-backup_1", "_svc-backup_1")]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345", "abcdefghijklmnopqrstuvwxyz012345")]
    public void AcceptsLinuxUserNamesInLowerCase(string input, string expected) =>
        Assert.Equal(expected, CredentialRules.NormalizeUsername(input));

    [Theory]
    [InlineData("-admin")]
    [InlineData("admin$")]
    [InlineData("jörg")]
    [InlineData("a/b")]
    public void RejectsInvalidUserNames(string input) =>
        Assert.Equal(ErrorCodes.AuthUsernameInvalid, Assert.Throws<PithyxException>(() => CredentialRules.NormalizeUsername(input)).Code);

    [Fact]
    public void DisplayNamesMayContainAnyPrintableCharacter() =>
        Assert.Equal("Jörg Müller-Łukasz 🙂", CredentialRules.NormalizeDisplayName("  Jörg Müller-Łukasz 🙂 "));

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void EmptyDisplayNameBecomesNull(string? input) => Assert.Null(CredentialRules.NormalizeDisplayName(input));

    [Fact]
    public void DisplayNameWithControlCharactersIsRejected() =>
        Assert.Equal(ErrorCodes.AuthDisplayNameInvalid, Assert.Throws<PithyxException>(() => CredentialRules.NormalizeDisplayName("a\nb")).Code);

    [Theory]
    [InlineData("qwertyuiopasdfghjkl")]
    [InlineData("QwertyuiopASDFGHJKL")]
    [InlineData("1234567890qwerty")]
    public void RejectsCommonPasswordsInAnyCase(string password) =>
        Assert.Equal(ErrorCodes.AuthPasswordTooCommon, Assert.Throws<PithyxException>(() => CredentialRules.ValidateNewPassword(password)).Code);

    [Theory]
    [InlineData("correct horse battery")]
    [InlineData("no composition rules at all")]
    public void AcceptsLongUncommonPasswordsWithoutCompositionRules(string password) => CredentialRules.ValidateNewPassword(password);
}
