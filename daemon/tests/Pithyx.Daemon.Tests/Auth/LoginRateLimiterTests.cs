using Microsoft.Extensions.Time.Testing;
using Pithyx.Daemon.Auth;
using Pithyx.Daemon.Errors;

namespace Pithyx.Daemon.Tests.Auth;

/// <summary>The limit on failed sign-ins and the moment it is reached, which the administrators hear about (D379).</summary>
public sealed class LoginRateLimiterTests
{
    [Fact]
    public void ANameIsBlockedAfterFiveFailuresAndTheAddressAfterThirty()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.Zero));
        var limiter = new LoginRateLimiter(time);
        var blocks = new List<SignInBlock>();
        limiter.Blocked += blocks.Add;

        for (var i = 0; i < LoginRateLimiter.FailuresPerUser; i++)
        {
            limiter.RecordFailure("192.168.1.9", " Admin ");
        }

        Assert.Equal([new SignInBlock("192.168.1.9", "Admin", LoginRateLimiter.FailuresPerUser)], blocks);
        Assert.Equal(ErrorCodes.AuthRateLimited, Assert.Throws<PithyxException>(() => limiter.EnsureAllowed("192.168.1.9", "admin")).Code);
        limiter.EnsureAllowed("192.168.1.9", "bob");

        for (var i = LoginRateLimiter.FailuresPerUser; i < LoginRateLimiter.FailuresPerAddress; i++)
        {
            limiter.RecordFailure("192.168.1.9", $"user{i}");
        }
        Assert.Equal(new SignInBlock("192.168.1.9", null, LoginRateLimiter.FailuresPerAddress), blocks[^1]);

        time.Advance(LoginRateLimiter.Window);
        limiter.EnsureAllowed("192.168.1.9", "admin");
    }
}
