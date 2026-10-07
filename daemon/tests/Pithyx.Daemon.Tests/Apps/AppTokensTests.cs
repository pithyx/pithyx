using Microsoft.Extensions.Time.Testing;
using Pithyx.Daemon.Apps;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Tests.Apps;

/// <summary>The token store on its own: kinds, lifetimes, one-time tickets, revocations and the cap per session.</summary>
public sealed class AppTokensTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-07T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private readonly AppTokens _tokens;

    public AppTokensTests() => _tokens = new AppTokens(_time);

    private IssuedAppToken Host(string session = "s1", string app = "com.example.alpha") =>
        _tokens.Issue(AppTokenKind.Host, 7, session, app, "w1");

    [Fact]
    public void ATokenIs256RandomBitsAndWorksOnlyForItsKind()
    {
        var host = Host();
        var backend = _tokens.Issue(AppTokenKind.Backend, 7, "s1", "com.example.alpha", "w1");

        Assert.Equal(43, host.Token.Length);
        Assert.NotEqual(host.Token, backend.Token);
        Assert.Equal(_time.GetUtcNow() + AppTokens.TokenLifetime, host.ExpiresAt);
        Assert.Equal(new AppTokenGrant(AppTokenKind.Host, 7, "s1", "com.example.alpha", "w1", host.ExpiresAt), _tokens.Validate(host.Token, AppTokenKind.Host));
        Assert.Null(_tokens.Validate(host.Token, AppTokenKind.Backend));
        Assert.Null(_tokens.Validate(backend.Token, AppTokenKind.Host));
        Assert.NotNull(_tokens.Validate(backend.Token, AppTokenKind.Backend));
        Assert.Null(_tokens.Validate(host.Token + "x", AppTokenKind.Host));
        Assert.Null(_tokens.Validate("", AppTokenKind.Host));
        Assert.Null(_tokens.Validate(null, AppTokenKind.Host));
        Assert.Throws<ArgumentOutOfRangeException>(() => _tokens.Issue(AppTokenKind.Ticket, 7, "s1", "com.example.alpha", "w1"));
    }

    [Fact]
    public void ATokenEndsAfterFiveMinutes()
    {
        var host = Host();
        _time.Advance(AppTokens.TokenLifetime - TimeSpan.FromSeconds(1));
        Assert.NotNull(_tokens.Validate(host.Token, AppTokenKind.Host));
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(_tokens.Validate(host.Token, AppTokenKind.Host));
        Assert.Equal(0, _tokens.Count());
    }

    [Fact]
    public void ATicketWorksOnceWithinThirtySecondsAndNeverAsAToken()
    {
        var grant = _tokens.Validate(Host().Token, AppTokenKind.Host)!;
        var ticket = _tokens.IssueTicket(grant, "api");

        Assert.Null(_tokens.Validate(ticket.Token, AppTokenKind.Host));
        Assert.Null(_tokens.Validate(ticket.Token, AppTokenKind.Ticket));
        var redeemed = _tokens.RedeemTicket(ticket.Token);
        Assert.Equal(grant with { Kind = AppTokenKind.Ticket, ExpiresAt = ticket.ExpiresAt, Endpoint = "api" }, redeemed);
        Assert.Null(_tokens.RedeemTicket(ticket.Token));

        var late = _tokens.IssueTicket(grant, "api");
        _time.Advance(AppTokens.TicketLifetime);
        Assert.Null(_tokens.RedeemTicket(late.Token));

        // A host token is no ticket, and trying it as one does not use it up.
        var host = Host();
        Assert.Null(_tokens.RedeemTicket(host.Token));
        Assert.NotNull(_tokens.Validate(host.Token, AppTokenKind.Host));
        Assert.Throws<ArgumentException>(() => _tokens.IssueTicket(grant with { Kind = AppTokenKind.Backend }, "api"));
    }

    [Fact]
    public void ATicketIsRedeemedOnlyOnceAcrossThreads()
    {
        var grant = _tokens.Validate(Host().Token, AppTokenKind.Host)!;
        var ticket = _tokens.IssueTicket(grant, "api");
        var results = new AppTokenGrant?[16];
        Parallel.For(0, results.Length, index => results[index] = _tokens.RedeemTicket(ticket.Token));
        Assert.Single(results, result => result is not null);
    }

    [Fact]
    public void RevokingASessionOrAnAppEndsOnlyTheirTokens()
    {
        var first = Host("s1", "com.example.alpha");
        var otherSession = Host("s2", "com.example.alpha");
        var otherApp = Host("s1", "com.example.beta");

        _tokens.RevokeSession("s1");
        Assert.Null(_tokens.Validate(first.Token, AppTokenKind.Host));
        Assert.Null(_tokens.Validate(otherApp.Token, AppTokenKind.Host));
        Assert.NotNull(_tokens.Validate(otherSession.Token, AppTokenKind.Host));

        _tokens.RevokeApp("com.example.alpha");
        Assert.Null(_tokens.Validate(otherSession.Token, AppTokenKind.Host));
    }

    [Fact]
    public void ASessionHoldsAtMostItsCapAndExpiredTokensMakeRoom()
    {
        for (var i = 0; i < AppTokens.MaxPerSession; i++)
        {
            Host();
        }
        var refused = Assert.Throws<PithyxException>(() => Host());
        Assert.Equal(ErrorCodes.AuthRateLimited, refused.Code);
        Host("s2");

        _time.Advance(AppTokens.TokenLifetime);
        Host();
        Assert.Equal(1, _tokens.Count());
    }

    [Theory]
    [InlineData("w1", true)]
    [InlineData("0b4f1c2e-9d7a-4c1b-8e2f-3a5b6c7d8e9f", true)]
    [InlineData("", false)]
    [InlineData("../w1", false)]
    [InlineData("w 1", false)]
    [InlineData(null, false)]
    public void WindowInstancesAreShortPlainNames(string? instance, bool valid) => Assert.Equal(valid, AppTokens.IsInstance(instance));
}
