using GrimesGolf.Relay;

// The relay for Grimes Golf online play. Run it anywhere Docker runs (see Dockerfile and README.md):
//   GET /       a line saying what this is
//   GET /health lobby and PC counts
//   GET /ws     the WebSocket the game connects to
var builder = WebApplication.CreateSlimBuilder(args);
var options = new RelayOptions();
builder.Configuration.GetSection("Relay").Bind(options);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<LobbyRegistry>();
builder.Services.AddHostedService<LobbySweeper>();

var app = builder.Build();
// Pings keep idle connections open through proxies and find PCs that vanished without closing.
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15), KeepAliveTimeout = TimeSpan.FromSeconds(30) });

// Behind Fly.io the PC's address comes in a header; anywhere else that header could be made up, so it's opt-in.
bool trustFly = app.Configuration.GetValue<bool>("Relay:TrustFlyClientIp");

app.MapGet("/", () => "Grimes Golf relay");
app.MapGet("/health", (LobbyRegistry r) => Results.Text($"lobbies={r.LobbyCount} pcs={r.PeerCount}"));
app.Map("/ws", async (HttpContext ctx, LobbyRegistry registry, RelayOptions opts) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) return Results.BadRequest("WebSocket only.");
    string ip = (trustFly ? ctx.Request.Headers["Fly-Client-IP"].FirstOrDefault() : null)
                ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "local";
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await new RelayConnection(ws, registry, opts, ip).RunAsync(ctx.RequestAborted);
    return Results.Empty;
});

app.Run();

/// <summary>Closes expired lobbies every 30 seconds.</summary>
internal sealed class LobbySweeper(LobbyRegistry registry, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), time);
        try { while (await timer.WaitForNextTickAsync(stoppingToken)) registry.Sweep(); }
        catch (OperationCanceledException) { }
    }
}

public partial class Program;
