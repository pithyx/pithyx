using System.Text.Json;
using System.Text.Json.Serialization;
using Vyxis.Daemon;
using Vyxis.Daemon.Commands;
using Vyxis.Daemon.Health;

const int HttpPort = 8994;

var builder = WebApplication.CreateSlimBuilder(args);

builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(HttpPort));
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

var mode = builder.Configuration["VYXIS_SIMULATE"] == "1" ? DaemonMode.Simulated : DaemonMode.Real;
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

// Plain HTTP is the development transport. HTTPS on 443 arrives with the TLS work in M5.
HostLog.PlainHttp(app.Logger, HttpPort, mode);

app.MapGet("/api/health", () => TypedResults.Ok(new HealthResponse("ok", mode)));

app.Run();

public partial class Program;
