using AirscapeWhf.Automation;
using AirscapeWhf.Fan;

namespace AirscapeWhf;

/// <summary>Either a value or a message written for the person (or model) who asked.</summary>
public readonly record struct Outcome<T>(T? Value, string? Error)
{
    public static Outcome<T> Ok(T value) => new(value, null);
    public static Outcome<T> Fail(string error) => new(default, error);
}

/// <summary>Cooldown rules plus which step fires next, so callers don't reimplement the rules.</summary>
public sealed record CooldownView(
    bool Enabled,
    IReadOnlyList<CooldownStep> Steps,
    int ReleaseMarginF,
    int MaxSpeed,
    CooldownStep? Next);

/// <summary>
/// Everything a caller can ask of the fan. The REST endpoints and the MCP tools both
/// go through here, so range checks and rule validation live in one place. Commands
/// are queued, not synchronous: they return once accepted, not once the fan arrives.
/// </summary>
public sealed class FanService
{
    private readonly FanStateStore _state;
    private readonly FanCommandQueue _queue;
    private readonly CooldownStore _cooldown;
    private readonly CooldownGovernor _governor;

    public FanService(FanStateStore state, FanCommandQueue queue, CooldownStore cooldown,
        CooldownGovernor governor)
    {
        _state = state;
        _queue = queue;
        _cooldown = cooldown;
        _governor = governor;
    }

    public FanSnapshot GetStatus() => _state.Current;

    public Outcome<int> SetSpeed(int speed)
    {
        var max = _state.Current.MaxSpeed;
        if (speed < 0 || speed > max)
            return Outcome<int>.Fail($"Speed must be between 0 and {max}.");

        // Publish the destination now so a status read straight after this call
        // already shows where the fan is heading.
        _state.SetTarget(speed);
        _queue.Enqueue(speed == 0 ? new FanIntent.Off() : new FanIntent.SetSpeed(speed));
        return Outcome<int>.Ok(speed);
    }

    public void Nudge(int steps) => _queue.Enqueue(new FanIntent.Nudge(Math.Clamp(steps, -10, 10)));

    /// <summary>Adds 1–12 hours (clamped). Returns the hours actually queued.</summary>
    public int AddTimerHours(int hours)
    {
        var clamped = Math.Clamp(hours, 1, 12);
        _queue.Enqueue(new FanIntent.AddTimer(clamped));
        return clamped;
    }

    public void TurnOff()
    {
        _state.SetTarget(0);
        _queue.Enqueue(new FanIntent.Off());
    }

    public CooldownView GetCooldown()
    {
        var settings = _cooldown.Current;
        var snapshot = _state.Current;
        // Rules act on the house temperature, so that is what the forecast reads.
        var next = settings.Forecast(snapshot.HouseTempF, snapshot.Speed, _governor.Latched);
        return new CooldownView(settings.Enabled, settings.Steps, settings.ReleaseMarginF, snapshot.MaxSpeed, next);
    }

    public async Task<Outcome<CooldownSettings>> SetCooldownAsync(CooldownSettings incoming, CancellationToken ct = default)
    {
        var settings = incoming with
        {
            Steps = (incoming.Steps ?? Array.Empty<CooldownStep>())
                .OrderByDescending(step => step.BelowF)
                .ToList(),
        };

        if (settings.Validate(_state.Current.MaxSpeed) is { } error)
            return Outcome<CooldownSettings>.Fail(error);

        await _cooldown.SaveAsync(settings, ct).ConfigureAwait(false);
        return Outcome<CooldownSettings>.Ok(settings);
    }
}
