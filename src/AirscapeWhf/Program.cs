using System.Text.Json;
using System.Text.Json.Serialization;
using AirscapeWhf.Automation;
using AirscapeWhf.Fan;

var builder = WebApplication.CreateBuilder(args);

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

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

app.MapGet("/api/status", (FanStateStore store) => Results.Ok(store.Current));

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

app.MapPost("/api/speed/{speed:int}", (int speed, FanCommandQueue queue, FanStateStore store) =>
{
    if (speed < 0 || speed > store.Current.MaxSpeed)
        return Results.BadRequest(new { error = $"Speed must be between 0 and {store.Current.MaxSpeed}." });

    queue.Enqueue(new FanIntent.SetSpeed(speed));
    return Results.Accepted();
});

app.MapPost("/api/nudge/{steps:int}", (int steps, FanCommandQueue queue) =>
{
    queue.Enqueue(new FanIntent.Nudge(Math.Clamp(steps, -10, 10)));
    return Results.Accepted();
});

app.MapPost("/api/timer/{hours:int}", (int hours, FanCommandQueue queue) =>
{
    queue.Enqueue(new FanIntent.AddTimer(Math.Clamp(hours, 1, 12)));
    return Results.Accepted();
});

app.MapPost("/api/off", (FanCommandQueue queue) =>
{
    queue.Enqueue(new FanIntent.Off());
    return Results.Accepted();
});

app.MapGet("/api/cooldown", (CooldownStore store, CooldownGovernor governor, FanStateStore state) =>
{
    var settings = store.Current;
    var snapshot = state.Current;
    var next = settings.Forecast(snapshot.OutsideTempF, snapshot.Speed, governor.Latched);

    return Results.Ok(new
    {
        settings.Enabled,
        settings.Steps,
        settings.ReleaseMarginF,
        maxSpeed = snapshot.MaxSpeed,
        // What will happen next, so the UI doesn't have to reimplement the rules.
        next = next is null ? null : new { next.BelowF, next.Speed },
    });
});

app.MapPut("/api/cooldown", async (CooldownSettings incoming, CooldownStore store, FanStateStore state) =>
{
    var settings = incoming with
    {
        Steps = (incoming.Steps ?? Array.Empty<CooldownStep>())
            .OrderByDescending(step => step.BelowF)
            .ToList(),
    };

    if (settings.Validate(state.Current.MaxSpeed) is { } error)
        return Results.BadRequest(new { error });

    await store.SaveAsync(settings);
    return Results.Ok(new { settings.Enabled, settings.Steps, settings.ReleaseMarginF });
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

app.Run();
