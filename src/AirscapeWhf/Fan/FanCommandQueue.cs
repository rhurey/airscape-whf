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
/// </summary>
public sealed class FanCommandQueue : BackgroundService
{
    private readonly Channel<FanIntent> _intents = Channel.CreateUnbounded<FanIntent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly FanClient _client;
    private readonly FanStateStore _store;
    private readonly FanOptions _options;
    private readonly ILogger<FanCommandQueue> _log;

    public FanCommandQueue(FanClient client, FanStateStore store, IOptions<FanOptions> options,
        ILogger<FanCommandQueue> log)
    {
        _client = client;
        _store = store;
        _options = options.Value;
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
                await ExecuteIntentAsync(intent, stoppingToken).ConfigureAwait(false);
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

    private async Task ExecuteIntentAsync(FanIntent intent, CancellationToken ct)
    {
        switch (intent)
        {
            case FanIntent.Off:
                await IssueAsync(FanCommand.Off, ct).ConfigureAwait(false);
                break;

            case FanIntent.AddTimer add:
                for (var i = 0; i < Math.Clamp(add.Hours, 1, 12); i++)
                {
                    if (Superseded()) return;
                    await IssueAsync(FanCommand.AddTimerHour, ct).ConfigureAwait(false);
                    await PaceAsync(ct).ConfigureAwait(false);
                }
                break;

            case FanIntent.Nudge nudge:
                await StepToAsync(_store.Current.Speed + nudge.Steps, ct).ConfigureAwait(false);
                break;

            case FanIntent.SetSpeed set:
                await StepToAsync(set.Speed, ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task StepToAsync(int target, CancellationToken ct)
    {
        var current = _store.Current;
        target = Math.Clamp(target, 0, current.MaxSpeed);

        if (target == 0)
        {
            await IssueAsync(FanCommand.Off, ct).ConfigureAwait(false);
            return;
        }

        // Hard iteration cap. If our idea of the current speed is wrong — the wall
        // switch or the handheld remote can move it under us — we must not loop forever
        // chasing a target we can't reach.
        var budget = current.MaxSpeed + 2;
        var speed = current.Speed;

        for (var i = 0; i < budget && speed != target; i++)
        {
            if (Superseded())
            {
                _log.LogDebug("Abandoning step to {Target}; a newer intent arrived", target);
                return;
            }

            var command = speed < target ? FanCommand.SpeedUp : FanCommand.SpeedDown;
            var raw = await IssueAsync(command, ct).ConfigureAwait(false);

            // Trust the controller's own report over our arithmetic -- except while
            // the damper doors are moving, when it reports 0 regardless of the speed
            // it is heading for. Believing that would have us issue speed-up commands
            // repeatedly for the ~30s the doors take, and overshoot badly.
            var stepped = speed + (command == FanCommand.SpeedUp ? 1 : -1);
            var doorsMoving = raw.DoorInProcess is not null and not 0;
            speed = doorsMoving ? stepped : raw.FanSpeed ?? stepped;

            if (speed != target)
                await PaceAsync(ct).ConfigureAwait(false);
        }

        if (speed != target)
            _log.LogInformation("Stopped at speed {Speed} while stepping to {Target}", speed, target);
    }

    private async Task<FanRawStatus> IssueAsync(FanCommand command, CancellationToken ct)
    {
        // Every command response carries a full status document, so publishing it here
        // keeps the UI current without spending an extra round trip on the fan.
        var raw = await _client.SendCommandAsync(command, ct).ConfigureAwait(false);
        _store.RecordSuccess(raw);
        return raw;
    }

    private bool Superseded() => Volatile.Read(ref _pending) > 0;

    private Task PaceAsync(CancellationToken ct)
        => Task.Delay(Math.Max(50, _options.StepDelayMilliseconds), ct);
}
