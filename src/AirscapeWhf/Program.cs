using System.Text.Json;
using System.Text.Json.Serialization;
using AirscapeWhf;
using AirscapeWhf.Automation;
using AirscapeWhf.Fan;
using AirscapeWhf.Hosting;
using AirscapeWhf.Mcp;
using AirscapeWhf.Metrics;
using Microsoft.Extensions.Options;

if (args.Contains("--healthcheck")) return await ContainerSetup.HealthcheckAsync();

var builder = WebApplication.CreateBuilder(args);

var configDir = ContainerSetup.ConfigDir;
ContainerSetup.AddConfigDirectory(builder.Configuration, configDir);

if (string.IsNullOrWhiteSpace(builder.Configuration["Fan:Host"]))
{
    await Console.Error.WriteLineAsync(
        $"No fan host configured. Set \"Fan\": {{ \"Host\": \"<controller IP>\" }} in " +
        $"{Path.Combine(configDir, "appsettings.json")}, or the Fan__Host environment variable.");
    return 2;
}

var listeners = ContainerSetup.ConfigureListeners(builder, configDir);

builder.Services.Configure<FanOptions>(builder.Configuration.GetSection(FanOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IFanClient, FanClient>();
builder.Services.AddSingleton<FanStateStore>();
builder.Services.AddSingleton<FanCommandQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FanCommandQueue>());
builder.Services.AddHostedService<FanPoller>();
builder.Services.AddSingleton<CooldownStore>();
builder.Services.AddSingleton<CooldownGovernor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CooldownGovernor>());
builder.Services.AddSingleton<FanService>();

var mcp = McpSetup.AddMcp(builder, configDir);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();
app.Logger.LogInformation("Listening: {Listeners}", listeners);
if (mcp.Serve) app.Logger.LogInformation("{Mcp}", mcp.Reason);
else app.Logger.LogWarning("{Mcp}", mcp.Reason);

app.UseDefaultFiles();
app.UseStaticFiles();

if (mcp.Serve)
{
    // Only /mcp carries an authorization requirement; the REST API and UI stay open
    // on the LAN as before.
    app.UseAuthentication();
    app.UseAuthorization();
    McpSetup.MapMcp(app);
}

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

app.MapGet("/api/status", (FanService fan) => Results.Ok(fan.GetStatus()));

// Prometheus scrape target. Reads the same cached snapshot as /api/status, so a
// scraper costs the controller nothing. Open like the rest of the REST API.
app.MapGet("/metrics", (FanService fan, IOptions<FanOptions> options) =>
    Results.Text(PrometheusMetrics.Render(fan.GetStatus(), options.Value.PollIntervalSeconds),
        PrometheusMetrics.ContentType));

// Server-sent events. One poller feeds every connected browser; adding a tab costs
// this process a channel and costs the fan nothing.
app.MapGet("/api/stream", async (HttpContext ctx, FanStateStore store, CancellationToken ct) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    try
    {
        await foreach (var snapshot in store.SubscribeAsync(ct))
        {
            await ctx.Response.WriteAsync("data: ", ct);
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(snapshot, json), ct);
            await ctx.Response.WriteAsync("\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException)
    {
        // Browser navigated away. Normal.
    }
});

app.MapPost("/api/speed/{speed:int}", (int speed, FanService fan) =>
    fan.SetSpeed(speed).Error is { } error
        ? Results.BadRequest(new { error })
        : Results.Accepted());

app.MapPost("/api/nudge/{steps:int}", (int steps, FanService fan) =>
{
    fan.Nudge(steps);
    return Results.Accepted();
});

app.MapPost("/api/timer/{hours:int}", (int hours, FanService fan) =>
{
    fan.AddTimerHours(hours);
    return Results.Accepted();
});

app.MapPost("/api/off", (FanService fan) =>
{
    fan.TurnOff();
    return Results.Accepted();
});

app.MapGet("/api/cooldown", (FanService fan) => Results.Ok(fan.GetCooldown()));

app.MapPut("/api/cooldown", async (CooldownSettings incoming, FanService fan, CancellationToken ct) =>
{
    var outcome = await fan.SetCooldownAsync(incoming, ct);
    if (outcome.Error is { } error) return Results.BadRequest(new { error });

    var saved = outcome.Value!;
    return Results.Ok(new { saved.Enabled, saved.Steps, saved.ReleaseMarginF });
});

// Liveness only — deliberately not readiness. This container is useful even when the
// fan is unreachable, because it still serves the last known state and an honest
// explanation of why it's stale. Restarting it would not help.
app.MapGet("/healthz", (FanStateStore store) => Results.Ok(new
{
    status = "ok",
    fanOnline = store.Current.Online,
    lastReadingAgeSeconds = store.Current.AgeSeconds,
    subscribers = store.SubscriberCount,
}));

await app.RunAsync();
return 0;

/// <summary>Visible to WebApplicationFactory in the tests.</summary>
public partial class Program;
