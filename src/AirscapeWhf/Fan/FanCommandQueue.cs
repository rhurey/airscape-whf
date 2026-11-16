using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace AirscapeWhf.Fan;

/// <summary>What the user asked for, as opposed to what the controller understands.</summary>
public abstract record FanIntent
{
    public sealed record SetSpeed(int Speed) : FanIntent;
    public sealed record Nudge(int Steps) : FanIntent;
    public sealed record AddTimer(int Hours) : FanIntent;
    public sealed record Off : FanIntent;
}

/// <summary>
/// Translates absolute intents into the relative commands the controller actually has.
/// <para>
/// The API is step-based: up, down, add an hour, off. There is no "go to speed 5". So a
/// slider drag from 1 to 5 is four sequential round trips, and a user who drags across
/// the whole range would otherwise queue a dozen redundant commands. Intents are
/// therefore coalesced — only the newest destination survives — and a sequence in
/// flight is abandoned the moment a newer intent lands.
/// </para>
/// <para>
/// Starting from off, the damper doors take ~30 s to open. While they move the
/// controller reports speed 0 and does not honour speed commands, so stepping on a
/// fixed delay sends every command into the void. This class therefore only steps on
/// a <i>settled</i> reading: it waits for the doors, reads where the fan actually is,
/// and steps the remainder.
/// </para>
/// </summary>
public sealed class FanCommandQueue : BackgroundService
{
    private readonly Channel<FanIntent> _intents = Channel.CreateUnbounded<FanIntent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly IFanClient _client;
    private readonly FanStateStore _store;
    private readonly FanOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<FanCommandQueue> _log;

    public FanCommandQueue(IFanClient client, FanStateStore store, IOptions<FanOptions> options,
        TimeProvider time, ILogger<FanCommandQueue> log)
    {
        _client = client;
        _store = store;
        _options = options.Value;
        _time = time;
        _log = log;
    }

    /// <summary>
    /// How many intents are waiting. Tracked by hand because a channel created with
    /// SingleReader uses SingleConsumerUnboundedChannel, whose reader does not
    /// implement Count -- it throws NotSupportedException rather than returning zero.
    /// </summary>
    private int _pending;

    public void Enqueue(FanIntent intent)
    {
        Interlocked.Increment(ref _pending);
        if (!_intents.Writer.TryWrite(intent)) Interlocked.Decrement(ref _pending);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var first in _intents.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            Interlocked.Decrement(ref _pending);
            var intent = Coalesce(first);
            try
            {
                await RunIntentAsync(intent, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _store.RecordFailure(ex);
                _log.LogWarning(ex, "Command {Intent} failed", intent);
            }
        }
    }

    /// <summary>Drains everything already queued and reduces it to a single destination.</summary>
    private FanIntent Coalesce(FanIntent seed)
    {
        var result = seed;
        var pendingHours = seed is FanIntent.AddTimer t ? t.Hours : 0;
        var pendingNudge = seed is FanIntent.Nudge n ? n.Steps : 0;

        while (_intents.Reader.TryRead(out var next))
        {
            Interlocked.Decrement(ref _pending);
            switch (next)
            {
                case FanIntent.AddTimer add:
                    pendingHours += add.Hours;
                    result = new FanIntent.AddTimer(pendingHours);
                    break;

                case FanIntent.Nudge nudge when result is FanIntent.Nudge:
                    pendingNudge += nudge.Steps;
                    result = new FanIntent.Nudge(pendingNudge);
                    break;

                default:
                    // Newest absolute destination wins outright.
                    result = next;
                    pendingHours = 0;
                    pendingNudge = 0;
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// Runs one intent and owns the published target. A superseded intent leaves the
    /// target for its successor to replace; anything else — done, gave up, or failed —
    /// clears it so the UI stops showing a destination nobody is heading for.
    /// </summary>
    internal async Task RunIntentAsync(FanIntent intent, CancellationToken ct)
    {
        try
        {
            await ExecuteIntentAsync(intent, ct).ConfigureAwait(false);
        }
        catch
        {
            _store.SetTarget(null);
            throw;
        }

        if (!Superseded()) _store.SetTarget(null);
    }

    private async Task ExecuteIntentAsync(FanIntent intent, CancellationToken ct)
    {
        switch (intent)
        {
            case FanIntent.Off:
                _store.SetTarget(0);
                await TurnOffAsync(ct).ConfigureAwait(false);
                break;

            case FanIntent.AddTimer add:
                await AddTimerAsync(add.Hours, ct).ConfigureAwait(false);
                break;

            case FanIntent.Nudge nudge:
                // During door travel the reported speed is 0, so nudge from where we are
                // heading if anything is in flight, otherwise from a settled reading.
                int from;
                if (_store.TargetSpeed is { } target)
                {
                    from = target;
                }
                else
                {
                    var settled = await ReadSettledAsync(ct).ConfigureAwait(false);
                    if (settled is null) return;
                    from = settled.FanSpeed ?? 0;
                }
                await StepToAsync(from + nudge.Steps, ct).ConfigureAwait(false);
                break;

            case FanIntent.SetSpeed set:
                await StepToAsync(set.Speed, ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task StepToAsync(int target, CancellationToken ct)
    {
        var maxSpeed = _store.Current.MaxSpeed;
        target = Math.Clamp(target, 0, maxSpeed);
        _store.SetTarget(target);

        if (target == 0)
        {
            await TurnOffAsync(ct).ConfigureAwait(false);
            return;
        }

        // Start from a fresh reading rather than the cache. The wall switch and the
        // handheld remote can move the speed between polls, and stepping from a stale
        // number is how a stepper overshoots.
        var raw = await ReadAsync(ct).ConfigureAwait(false);

        // Hard cap. If something keeps moving the speed under us we must not loop
        // forever chasing a target we can't reach.
        var budget = maxSpeed + 2;
        var commands = 0;

        while (true)
        {
            if (Superseded())
            {
                _log.LogDebug("Abandoning step to {Target}; a newer intent arrived", target);
                return;
            }

            if (DoorsMoving(raw))
            {
                if (await WaitForDoorsAsync(ct).ConfigureAwait(false) is not { } settled) return;
                raw = settled;
                continue;
            }

            var speed = raw.FanSpeed ?? 0;
            if (speed == target) return;

            if (commands >= budget)
            {
                _log.LogInformation("Stopped at speed {Speed} while stepping to {Target}", speed, target);
                return;
            }

            raw = await IssueAsync(speed < target ? FanCommand.SpeedUp : FanCommand.SpeedDown, ct)
                .ConfigureAwait(false);
            commands++;

            // A step that starts the doors is followed by a wait, not a pace.
            if (!DoorsMoving(raw)) await PaceAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Off always wins: it is sent immediately, even mid door travel, and repeated once
    /// if the fan turns out to still be running once things settle.
    /// </summary>
    private async Task TurnOffAsync(CancellationToken ct)
    {
        var raw = await IssueAsync(FanCommand.Off, ct).ConfigureAwait(false);

        var settled = DoorsMoving(raw) ? await WaitForDoorsAsync(ct).ConfigureAwait(false) : raw;
        if (settled is null || Superseded()) return;

        if (settled.FanSpeed is > 0)
        {
            _log.LogInformation("Fan still at speed {Speed} after Off; sending it again", settled.FanSpeed);
            await IssueAsync(FanCommand.Off, ct).ConfigureAwait(false);
        }
    }

    private async Task AddTimerAsync(int hours, CancellationToken ct)
    {
        // A timer hour has no destination speed (it starts an idle fan at 1), so there
        // is nothing to show as a target.
        _store.SetTarget(null);

        if (_store.Current.DoorMoving && await WaitForDoorsAsync(ct).ConfigureAwait(false) is null) return;

        for (var i = 0; i < Math.Clamp(hours, 1, 12); i++)
        {
            if (Superseded()) return;
            var raw = await IssueAsync(FanCommand.AddTimerHour, ct).ConfigureAwait(false);

            // The first hour on an idle fan starts it and opens the doors.
            if (DoorsMoving(raw))
            {
                if (await WaitForDoorsAsync(ct).ConfigureAwait(false) is null) return;
            }
            else
            {
                await PaceAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>A fresh reading, waited on until the doors are still. Null if superseded or timed out.</summary>
    private async Task<FanRawStatus?> ReadSettledAsync(CancellationToken ct)
    {
        var raw = await ReadAsync(ct).ConfigureAwait(false);
        return DoorsMoving(raw) ? await WaitForDoorsAsync(ct).ConfigureAwait(false) : raw;
    }

    /// <summary>
    /// Re-reads status until the damper doors stop. Returns the first settled reading,
    /// or null when a newer intent arrives or the doors never settle.
    /// </summary>
    private async Task<FanRawStatus?> WaitForDoorsAsync(CancellationToken ct)
    {
        var poll = TimeSpan.FromMilliseconds(Math.Max(500, _options.DoorPollMilliseconds));
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.DoorTimeoutSeconds));
        var deadline = _time.GetUtcNow() + timeout;

        while (true)
        {
            if (Superseded()) return null;

            if (_time.GetUtcNow() >= deadline)
            {
                _log.LogWarning("Damper doors still moving after {Timeout}s; giving up on this command",
                    timeout.TotalSeconds);
                return null;
            }

            await Task.Delay(poll, _time, ct).ConfigureAwait(false);
            if (Superseded()) return null;

            var raw = await ReadAsync(ct).ConfigureAwait(false);
            if (!DoorsMoving(raw)) return raw;
        }
    }

    private async Task<FanRawStatus> ReadAsync(CancellationToken ct)
    {
        var raw = await _client.GetStatusAsync(ct).ConfigureAwait(false);
        _store.RecordSuccess(raw);
        return raw;
    }

    private async Task<FanRawStatus> IssueAsync(FanCommand command, CancellationToken ct)
    {
        // Every command response carries a full status document, so publishing it here
        // keeps the UI current without spending an extra round trip on the fan.
        var raw = await _client.SendCommandAsync(command, ct).ConfigureAwait(false);
        _store.RecordSuccess(raw);
        return raw;
    }

    private static bool DoorsMoving(FanRawStatus raw) => raw.DoorInProcess is not null and not 0;

    private bool Superseded() => Volatile.Read(ref _pending) > 0;

    private Task PaceAsync(CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(Math.Max(50, _options.StepDelayMilliseconds)), _time, ct);
}
