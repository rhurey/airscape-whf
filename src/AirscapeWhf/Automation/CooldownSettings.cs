using System.Text.Json.Serialization;

namespace AirscapeWhf.Automation;

/// <summary>One rung of the ladder: below this temperature, run no faster than this.</summary>
/// <param name="BelowF">Inside (house) temperature, in °F, at which this step applies.</param>
/// <param name="Speed">Speed to drop to. Zero means stop.</param>
public sealed record CooldownStep(int BelowF, int Speed);

/// <summary>
/// Rules for easing the fan off as the house cools.
/// <para>
/// The governing invariant is that these rules can only ever <i>slow the fan down or
/// stop it</i> — never speed it up, never start it. That is what makes the feature
/// safe to leave switched on: a rule that could start a fan at 3am because the
/// temperature reading blipped is a rule nobody would trust.
/// </para>
/// </summary>
public sealed record CooldownSettings
{
    public static readonly CooldownSettings Disabled = new();

    public bool Enabled { get; init; }

    /// <summary>Rungs of the ladder. Order doesn't matter; the coldest applicable wins.</summary>
    public IReadOnlyList<CooldownStep> Steps { get; init; } = Array.Empty<CooldownStep>();

    /// <summary>
    /// How far the temperature must climb back above a step before that step can fire
    /// again. Without this, standing at a threshold while the reading jitters by a
    /// degree would have the governor fighting the user for control of the speed.
    /// </summary>
    public int ReleaseMarginF { get; init; } = 2;

    [JsonIgnore]
    public bool IsActive => Enabled && Steps.Count > 0;

    /// <summary>Returns null when valid, or a message written for the person who typed it.</summary>
    public string? Validate(int maxSpeed)
    {
        if (Steps.Count > 6)
            return "Six steps is the most that makes sense. Remove a few.";

        foreach (var step in Steps)
        {
            if (step.BelowF is < 20 or > 100)
                return $"{step.BelowF}° is outside the range this can work with (20–100°F).";
            if (step.Speed < 0 || step.Speed > maxSpeed)
                return $"Speed {step.Speed} isn't available on this fan (0–{maxSpeed}).";
        }

        var thresholds = Steps.Select(s => s.BelowF).ToList();
        if (thresholds.Distinct().Count() != thresholds.Count)
            return "Two steps share the same temperature. Each needs its own.";

        // A colder step that runs the fan faster can never fire — the warmer step will
        // already have slowed things below it. Better to say so than to silently ignore it.
        var ordered = Steps.OrderByDescending(s => s.BelowF).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Speed >= ordered[i - 1].Speed)
                return $"The step at {ordered[i].BelowF}° would never fire, because the one " +
                       $"at {ordered[i - 1].BelowF}° has already slowed the fan to " +
                       $"{ordered[i - 1].Speed}. Colder steps need lower speeds.";
        }

        if (ReleaseMarginF is < 1 or > 15)
            return "The release margin should be between 1 and 15 degrees.";

        return null;
    }

    /// <summary>The next step that will fire, for display. Null when nothing is pending.</summary>
    public CooldownStep? Forecast(int? houseF, int speed, IReadOnlySet<int> latched)
    {
        if (!IsActive || houseF is not { } house || speed <= 0) return null;

        return Steps
            .Where(s => house >= s.BelowF && !latched.Contains(s.BelowF) && s.Speed < speed)
            .OrderByDescending(s => s.BelowF)
            .FirstOrDefault();
    }
}
