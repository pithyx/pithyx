using Microsoft.Extensions.Time.Testing;
using Pithyx.Daemon.Logs;

namespace Pithyx.Daemon.Tests.Logs;

public sealed class AppLogLimiterTests
{
    [Fact]
    public void AnAppGetsABurstThenItsRateAndTheLossIsReportedOnceAMinute()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
        var limiter = new AppLogLimiter(time);

        Assert.Equal(AppLogLimiter.Burst, limiter.Take("chatty", 300));
        Assert.Equal(100, limiter.Take("quiet", 100));
        Assert.Equal(0, limiter.Take("chatty", 5));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(AppLogLimiter.PerSecond, limiter.Take("chatty", 100));
        Assert.Empty(limiter.TakeReports());

        time.Advance(AppLogLimiter.ReportInterval);

        Assert.Equal([("chatty", 100L + 5 + 80)], limiter.TakeReports());
        Assert.Empty(limiter.TakeReports());
    }
}
