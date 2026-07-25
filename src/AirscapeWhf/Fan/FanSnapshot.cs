using System.Text.Json.Serialization;

namespace AirscapeWhf.Fan;

/// <summary>Whether running the fan right now actually helps.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunVerdict
{
    /// <summary>No temperature sensor package, or no reading yet.</summary>
    Unknown,
    /// <summary>Outside is warmer than inside. Running pulls heat in.</summary>
    Counterproductive,
    /// <summary>Outside is cooler, but not by enough to be worth the watts.</summary>
    Marginal,
    /// <summary>Outside is meaningfully cooler. Good time to run.</summary>
    Good,
}

/// <summary>
/// What the API and the browser see: last-known controller state, everything derived
/// from it, and enough freshness metadata for the UI to be honest about staleness.
/// </summary>
public sealed record FanSnapshot
{
    public static readonly FanSnapshot Unknown = new();

    // ---- liveness -------------------------------------------------------
    public bool Online { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public int AgeSeconds { get; init; }
    public string? LastError { get; init; }

    // ---- controller state ----------------------------------------------
    public int Speed { get; init; }
    public int MaxSpeed { get; init; } = 5;
    public bool Running => Speed > 0;
    public bool DoorMoving { get; init; }
    public bool Interlocked { get; init; }
    public int TimerMinutes { get; init; }

    public int? Cfm { get; init; }
    public int? PowerWatts { get; init; }
    public int? HouseTempF { get; init; }
    public int? AtticTempF { get; init; }
    public int? OutsideTempF { get; init; }

    public string? Model { get; init; }
    public string? SoftwareVersion { get; init; }
    public string? IpAddress { get; init; }
    public string? MacAddress { get; init; }

    /// <summary>Board configuration switches, verbatim. AirScape doesn't document the
    /// bit meanings, so this is passed through for diagnostics rather than interpreted.</summary>
    public string? DipSwitches { get; init; }

    /// <summary>Hardwired wall control state, verbatim. Also undocumented.</summary>
    public string? RemoteSwitch { get; init; }

    /// <summary>True when the controller reports any temperature sensor.</summary>
    public bool HasSensors => HouseTempF is not null || OutsideTempF is not null || AtticTempF is not null;

    // ---- derived --------------------------------------------------------
    // The vendor page ships these as if they were device state. They aren't —
    // they're arithmetic on four numbers, so we do them here and keep one source
    // of truth instead of asking the controller for values it also just computed.

    /// <summary>Degrees F that outside is cooler than the house. Negative means warmer.</summary>
    public int? DeltaF =>
        HouseTempF is { } house && OutsideTempF is { } outside ? house - outside : null;

    public double? CfmPerWatt =>
        Cfm is { } cfm && PowerWatts is { } watts && watts > 0
            ? Math.Round(cfm / (double)watts, 1)
            : null;

    /// <summary>
    /// Sensible cooling delivered, BTU/hr: 1.08 x CFM x deltaT. The 1.08 folds together
    /// the specific heat and density of air with the minutes-to-hours conversion.
    /// </summary>
    public int? CoolingBtuh =>
        Cfm is { } cfm && DeltaF is { } delta && Running && delta > 0
            ? (int)Math.Round(1.08 * cfm * delta)
            : Running ? 0 : null;

    /// <summary>Cooling delivered per watt consumed. Compare with ~13 for a good AC unit.</summary>
    public int? Eer =>
        CoolingBtuh is { } btuh && PowerWatts is { } watts && watts > 0
            ? (int)Math.Round(btuh / (double)watts)
            : null;

    public RunVerdict Verdict => DeltaF switch
    {
        null => RunVerdict.Unknown,
        <= 0 => RunVerdict.Counterproductive,
        < 4 => RunVerdict.Marginal,
        _ => RunVerdict.Good,
    };

    /// <summary>
    /// One line the UI can show without doing its own reasoning. Written for someone
    /// standing in the kitchen at 9pm, not for someone reading a datasheet.
    /// </summary>
    public string Headline
    {
        get
        {
            if (!Online && ObservedAt is null) return "Waiting for the fan to answer";
            if (!Online) return "Fan not responding — showing last known state";
            if (DoorMoving) return "Damper doors moving";
            if (Interlocked) return "Interlock engaged — fan will not start";

            return Verdict switch
            {
                RunVerdict.Good when Running => $"Pulling in air {DeltaF}\u00B0 cooler than the house",
                RunVerdict.Good => $"Outside is {DeltaF}\u00B0 cooler. Good time to run.",
                RunVerdict.Marginal when Running => $"Only {DeltaF}\u00B0 of cooling to gain right now",
                RunVerdict.Marginal => $"Outside is {DeltaF}\u00B0 cooler. Not much to gain.",
                RunVerdict.Counterproductive when Running => "Outside is warmer — this is pulling heat in",
                RunVerdict.Counterproductive => "Outside is warmer than inside. Leave it off.",
                _ when Running => $"Running at speed {Speed}",
                _ => "Fan is off",
            };
        }
    }

    public static FanSnapshot From(FanRawStatus raw, int maxSpeed, DateTimeOffset observedAt)
    {
        var speed = Math.Max(0, raw.FanSpeed ?? 0);
        return new FanSnapshot
        {
            Online = true,
            ObservedAt = observedAt,
            AgeSeconds = 0,
            Speed = speed,
            MaxSpeed = Math.Max(maxSpeed, speed),
            DoorMoving = raw.DoorInProcess is not null and not 0,
            Interlocked = raw.Interlock1 is not null and not 0 || raw.Interlock2 is not null and not 0,
            TimerMinutes = Math.Max(0, raw.TimerMinutes ?? 0),
            Cfm = raw.Cfm,
            PowerWatts = raw.PowerWatts,
            HouseTempF = raw.HouseTempF,
            AtticTempF = raw.AtticTempF,
            OutsideTempF = raw.OutsideTempF,
            Model = raw.Model,
            SoftwareVersion = raw.SoftwareVersion,
            IpAddress = raw.IpAddress,
            MacAddress = raw.MacAddress,
            DipSwitches = raw.DipSwitches,
            RemoteSwitch = raw.RemoteSwitch,
        };
    }

    /// <summary>Re-stamps age without touching the reading, for serving cached state.</summary>
    public FanSnapshot WithAge(DateTimeOffset now) => this with
    {
        AgeSeconds = ObservedAt is { } at ? (int)Math.Max(0, (now - at).TotalSeconds) : 0,
    };
}
