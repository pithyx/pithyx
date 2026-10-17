using System.Diagnostics;
using Pithyx.Daemon.Apps.Docker;

namespace Pithyx.Daemon.Tests.Apps.Docker;

/// <summary>A schedule's call carries its id and a request id, like a request through the proxy (F5).</summary>
public sealed class ScheduleRequestTests
{
    private static readonly Uri _backend = new("http://172.30.0.2:8080/");

    [Fact]
    public void CarriesTheScheduleAndTheRunsRequestId()
    {
        using var activity = new Activity("apps.schedule").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var request = AppBackendSupervisor.ScheduleRequest(_backend, "/tasks/cleanup", "cleanup");
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri("http://172.30.0.2:8080/tasks/cleanup"), request.RequestUri);
        Assert.Equal("cleanup", request.Headers.GetValues(AppBackendSupervisor.ScheduleHeader).Single());
        Assert.Equal(activity.Id, request.Headers.GetValues("traceparent").Single());
        Assert.Null(request.Content);
    }

    [Fact]
    public void MakesARequestIdWhenNoActivityRuns()
    {
        Activity.Current = null;
        using var request = AppBackendSupervisor.ScheduleRequest(_backend, "tasks", "cleanup");
        Assert.Matches("^00-[0-9a-f]{32}-[0-9a-f]{16}-01$", request.Headers.GetValues("traceparent").Single());
    }
}
