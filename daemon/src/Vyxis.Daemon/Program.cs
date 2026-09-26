using System.Text.Json;
using System.Text.Json.Serialization;
using Scalar.AspNetCore;
using Serilog;
using Vyxis.Daemon;
using Vyxis.Daemon.Commands;
using Vyxis.Daemon.Errors;
using Vyxis.Daemon.Health;
using Vyxis.Daemon.Hosting;

const int HttpPort = 8994;

Tracing.EnsureActivities();

var builder = WebApplication.CreateSlimBuilder(args);

var mode = builder.Configuration["VYXIS_SIMULATE"] == "1" ? DaemonMode.Simulated : DaemonMode.Real;
var paths = DaemonPaths.Resolve(builder.Configuration, builder.Environment, mode);

builder.AddVyxisLogging(paths.LogDirectory);
builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(HttpPort));
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.RespectNullableAnnotations = true;
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.AddVyxisErrors();
builder.Services.AddVyxisOpenApi();

if (mode == DaemonMode.Simulated)
{
    var fixtures = builder.Configuration["VYXIS_FIXTURES"]
        ?? Path.Combine(builder.Environment.ContentRootPath, "..", "..", "tests", "fixtures");
    var scenario = builder.Configuration["VYXIS_SIMULATE_SCENARIO"] ?? "default";
    builder.Services.AddSingleton<ICommandRunner>(FixtureCommandRunner.Load(Path.Combine(fixtures, "commands", scenario)));
}
else
{
    builder.Services.AddSingleton<ICommandRunner, ProcessCommandRunner>();
}

var app = builder.Build();

if (!BuildTimeDocument.IsGenerating)
{
    // Plain HTTP is the development transport. HTTPS on 443 arrives with the TLS work in M5.
    HostLog.PlainHttp(app.Logger, HttpPort, mode);
}

app.UseMiddleware<RequestIdMiddleware>();
app.UseSerilogRequestLogging(options => options.Logger = app.Services.GetRequiredService<Serilog.ILogger>());
app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapOpenApi(OpenApiSetup.DocumentPath);
if (app.Environment.IsDevelopment())
{
    app.MapScalarApiReference("/api/docs", options => options.WithOpenApiRoutePattern(OpenApiSetup.DocumentPath));
}

app.MapGet("/api/health", () => TypedResults.Ok(new HealthResponse("ok", mode)))
    .WithName("GetHealth")
    .WithTags("System");

app.Run();

public partial class Program;
