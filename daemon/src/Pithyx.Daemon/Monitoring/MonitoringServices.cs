using Pithyx.Daemon.Hosting;

namespace Pithyx.Daemon.Monitoring;

public static class MonitoringServices
{
    /// <summary><c>system/info</c> for every user, and the resource monitor's topics for administrators.</summary>
    public static IServiceCollection AddPithyxMonitoring(this IServiceCollection services)
    {
        services.AddSingleton<SystemInfoSampler>();
        services.AddSingleton(provider => SystemInfoPublisher.Definition(provider.GetRequiredService<SystemInfoSampler>()));
        services.AddSingleton(new ResourceFiles());
        services.AddSingleton<ResourceSampler>();
        services.AddSingleton<ProcessSampler>();
        services.AddSingleton<ResourceRing>();
        services.AddSingleton<ResourceMonitor>();
        services.AddSingleton(provider => ResourceMonitor.ResourcesDefinition(provider.GetRequiredService<ResourceSampler>(), provider.GetRequiredService<ResourceRing>()));
        services.AddSingleton(provider => ResourceMonitor.ProcessesDefinition(provider.GetRequiredService<ProcessSampler>()));
        services.AddHostedService<SystemInfoPublisher>();
        // The build-time OpenAPI run only needs the topic types; it never samples the build machine.
        if (!BuildTimeDocument.IsGenerating)
        {
            services.AddHostedService(provider => provider.GetRequiredService<ResourceMonitor>());
        }
        return services;
    }
}
