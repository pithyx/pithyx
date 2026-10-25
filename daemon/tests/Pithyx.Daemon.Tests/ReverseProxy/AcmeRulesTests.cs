using Pithyx.Daemon.Errors;
using Pithyx.Daemon.ReverseProxy;

namespace Pithyx.Daemon.Tests.ReverseProxy;

/// <summary>Which names may get a certificate from an ACME authority, when the box asks and how it reads refusals (D378).</summary>
public sealed class AcmeRulesTests
{
    private static readonly string[] _nas = ["nas.example.com"];
    private static readonly string[] _both = ["nas.example.com", "www.example.com"];
    private static readonly DateTimeOffset _issued = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("*.example.com", "proxy.acme.wildcard")]
    [InlineData("nas.local", "proxy.acme.name_not_public")]
    [InlineData("nas.home.arpa", "proxy.acme.name_not_public")]
    [InlineData("nas", "proxy.acme.name_not_public")]
    [InlineData("192.168.1.5", "proxy.acme.name_not_public")]
    [InlineData("fd00::5", "proxy.acme.name_not_public")]
    public void OnlyPublicNamesWithoutWildcardsGetACertificate(string domain, string code)
    {
        var error = Assert.Throws<PithyxException>(() => AcmeRules.CheckDomains([domain]));

        Assert.Equal(code, error.Code);
        Assert.Equal(domain, error.Parameters["name"]);
        AcmeRules.CheckDomains(_both);
    }

    [Fact]
    public void TheDirectoryIsHttpsAndTheContactAnAddress()
    {
        Assert.Equal(AcmeSettings.Default, AcmeRules.Apply(AcmeSettings.Default, new AcmeAccountRequest(), null));
        Assert.Equal("proxy.acme.directory_invalid", Assert.Throws<PithyxException>(() => AcmeRules.Apply(AcmeSettings.Default, new AcmeAccountRequest("http://ca.example/directory"), null)).Code);
        Assert.Equal("proxy.acme.email_invalid", Assert.Throws<PithyxException>(() => AcmeRules.Apply(AcmeSettings.Default, new AcmeAccountRequest(Email: "not mail"), null)).Code);
        Assert.Equal("proxy.acme.unreachable", Assert.Throws<PithyxException>(() => AcmeRules.Apply(AcmeSettings.Default, new AcmeAccountRequest(AgreeToTerms: true), null)).Code);
    }

    [Fact]
    public void AnAgreementHoldsForItsAuthorityOnly()
    {
        var agreed = AcmeRules.Apply(AcmeSettings.Default, new AcmeAccountRequest(Email: " admin@example.com ", AgreeToTerms: true), "https://ca.example/terms.pdf");
        var kept = AcmeRules.Apply(agreed, new AcmeAccountRequest(Email: "admin@example.com"), null);
        var other = AcmeRules.Apply(agreed, new AcmeAccountRequest("https://other.example/directory"), null);

        Assert.Equal(new AcmeSettings(AcmeSettings.LetsEncrypt, "admin@example.com", "https://ca.example/terms.pdf"), agreed);
        Assert.Equal(agreed, kept);
        Assert.Null(other.TermsAgreed);
    }

    [Fact]
    public void ACertificateIsRequestedAtOnceThenAThirdOfItsLifetimeBeforeItsEnd()
    {
        var end = _issued.AddDays(90);

        Assert.Equal(DateTimeOffset.MinValue, AcmeRules.NextAttempt(_nas, null, null, null, null, 0));
        Assert.Equal(end.AddDays(-30), AcmeRules.NextAttempt(_nas, _nas, _issued, end, _issued, 0));
        Assert.Equal(DateTimeOffset.MinValue, AcmeRules.NextAttempt(_both, _nas, _issued, end, _issued, 0));
        Assert.Equal(_issued.AddDays(4), AcmeRules.NextAttempt(_nas, _nas, _issued, _issued.AddDays(6), _issued, 0));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 16)]
    [InlineData(6, 24)]
    [InlineData(40, 24)]
    public void FailuresWaitLongerUpToADay(int failures, int hours)
    {
        Assert.Equal(_issued.AddHours(hours), AcmeRules.NextAttempt(_nas, null, null, null, _issued, failures));
        Assert.Equal(_issued.AddDays(60), AcmeRules.NextAttempt(_nas, _nas, _issued, _issued.AddDays(90), _issued, failures));
    }

    [Theory]
    [InlineData("urn:ietf:params:acme:error:dns", "proxy.acme.dns")]
    [InlineData("urn:ietf:params:acme:error:connection", "proxy.acme.connection")]
    [InlineData("urn:ietf:params:acme:error:incorrectResponse", "proxy.acme.connection")]
    [InlineData("urn:ietf:params:acme:error:unauthorized", "proxy.acme.unauthorized")]
    [InlineData("urn:ietf:params:acme:error:rateLimited", "proxy.acme.rate_limited")]
    [InlineData("urn:ietf:params:acme:error:caa", "proxy.acme.caa")]
    [InlineData("urn:ietf:params:acme:error:rejectedIdentifier", "proxy.acme.name_rejected")]
    [InlineData("urn:ietf:params:acme:error:serverInternal", "proxy.acme.failed")]
    [InlineData(null, "proxy.acme.failed")]
    public void TheAuthoritysProblemsBecomeErrorCodesWithItsWords(string? type, string code)
    {
        var (mapped, parameters) = AcmeRules.Problem(type, "Timeout during connect\n(likely firewall problem)", "nas.example.com");

        Assert.Equal(code, mapped);
        Assert.Equal("nas.example.com", parameters["name"]);
        Assert.Equal("Timeout during connect (likely firewall problem)", parameters["detail"]);
        _ = ErrorCodes.StatusOf(mapped);
    }
}
