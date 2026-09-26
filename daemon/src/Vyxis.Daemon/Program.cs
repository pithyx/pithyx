using Vyxis.Daemon.Health;

var builder = WebApplication.CreateSlimBuilder(args);

builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(8994));

var app = builder.Build();

app.MapGet("/api/health", () => TypedResults.Ok(new HealthResponse("ok")));

app.Run();

public partial class Program;
