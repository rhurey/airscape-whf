using AirscapeWhf.Fan;
using Microsoft.Extensions.Options;

namespace AirscapeWhf.Automation;

/// <summary>
/// Watches the inside (house) temperature and eases the fan off as it falls.
/// <para>
/// One invariant governs everything here: <b>this can only slow the fan down or stop
/// it.</b> It never speeds the fan up and never starts it. That is what makes the
/// feature safe to leave switched on — a rule that could start a fan at 3am because
/// a temperature reading blipped is a rule nobody would trust.
/// </para>
/// <para>
/// The second piece is the latch. Once a step has fired it will not fire again until
/// the temperature climbs back past its threshold plus a release margin. Without that,
/// standing at a threshold while the reading jitters by a degree would have the
/// governor fighting the user for control of the speed. With it, a manual override
/// sticks until morning.
/// </para>
/// <para>
/// The state machine this implements is exercised in tools/cooldown_check.py.
/// </para>
/// </summary>
public sealed class CooldownGovernor : BackgroundService
{
    private readonly FanStateStore _state;
    private readonly FanCommandQueue _queue;
    private readonly CooldownStore _settings;
    private readonly FanOptions _fanOptions;
    private readonly ILogger<CooldownGovernor> _log;

    /// <summary>Thresholds (in °F) that have already fired, keyed by <c>BelowF</c>.</summary>
    private readonly HashSet<int> _latched = new();
    private readonly object _sync = new();

    public CooldownGovernor(FanStateStore state, FanCommandQueue queue, CooldownStore settings,
        IOptions<FanOptions> fanOptions, ILogger<CooldownGovernor> log)
    {
        _state = state;
        _queue = queue;
        _settings = settings;
        _fanOptions = fanOptions.Value;
        _log = log;
    }

    /// <summary>Latched thresholds, for the forecast shown in the UI.</summary>
    public IReadOnlySet<int> Latched
    {
        get { lock (_sync) return _latched.ToHashSet(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Piggyback on the existing broadcast rather than polling the fan again —
        // the device has two sockets and this feature is not worth one of them.
        await foreach (var snapshot in _state.SubscribeAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                Evaluate(snapshot);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Cooldown evaluation failed; leaving the fan alone.");
            }
        }
    }

    private void Evaluate(FanSnapshot snapshot)
    {
        var settings = _settings.Current;
        if (!settings.IsActive)
        {
            _log.LogDebug("Cooldown is disabled; nothing to do.");
            return;
        }

        // Never act on a reading we don't trust. A stale snapshot is the fan's last
        // known state, not its current one, and acting on it could stop a fan that
        // someone just turned on.
        var staleAfter = Math.Max(60, _fanOptions.PollIntervalSeconds * 3);
        if (!snapshot.Online || snapshot.AgeSeconds > staleAfter) return;
        if (snapshot.HouseTempF is not { } temp) return;

        CooldownStep? firing;

        lock (_sync)
        {
            // Release steps the temperature has climbed back above. This happens even
            // while the fan is off, so the rules are armed again by the next evening.
            foreach (var step in settings.Steps)
                if (temp >= step.BelowF + settings.ReleaseMarginF)
                    _latched.Remove(step.BelowF);

            // The invariant: never start the fan.
            if (snapshot.Speed <= 0) return;

            firing = null;
            foreach (var step in settings.Steps)
            {
                if (temp >= step.BelowF) continue;              // not cold enough yet
                if (_latched.Contains(step.BelowF)) continue;      // already acted on this
                if (step.Speed >= snapshot.Speed) continue;        // would not slow it; never raise
                if (firing is null || step.Speed < firing.Speed)
                    firing = step;                                 // take the most restrictive
            }

            if (firing is null) return;
            _latched.Add(firing.BelowF);
        }

        _log.LogInformation(
            firing.Speed == 0
                ? "House is {Inside}°, below the {Threshold}° step — stopping the fan."
                : "House is {Inside}°, below the {Threshold}° step — easing from {From} to {To}.",
            temp, firing.BelowF, snapshot.Speed, firing.Speed);

        _queue.Enqueue(new FanIntent.SetSpeed(firing.Speed));
    }
}
