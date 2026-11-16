using System.ComponentModel;
using AirscapeWhf.Automation;
using AirscapeWhf.Fan;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AirscapeWhf.Mcp;

/// <summary>What a model needs to know about the fan, without the diagnostics noise.</summary>
public sealed record McpFanStatus(
    bool Online,
    int AgeSeconds,
    int Speed,
    int? TargetSpeed,
    int MaxSpeed,
    bool DoorMoving,
    bool Interlocked,
    int TimerMinutes,
    int? HouseTempF,
    int? AtticTempF,
    int? OutsideTempF,
    int? Cfm,
    int? PowerWatts,
    string Verdict,
    string Headline,
    string? LastError)
{
    public static McpFanStatus From(FanSnapshot s) => new(
        s.Online, s.AgeSeconds, s.Speed, s.TargetSpeed, s.MaxSpeed, s.DoorMoving, s.Interlocked,
        s.TimerMinutes, s.HouseTempF, s.AtticTempF, s.OutsideTempF, s.Cfm, s.PowerWatts,
        s.Verdict.ToString(), s.Headline, s.LastError);
}

public sealed record McpCommandResult(bool Accepted, string Message, McpFanStatus Status);

/// <summary>Tools that only read. Registered even when Mcp:ReadOnly is set.</summary>
[McpServerToolType]
public sealed class FanReadTools
{
    private readonly FanService _fan;

    public FanReadTools(FanService fan) => _fan = fan;

    [McpServerTool(Name = "get_fan_status", Title = "Get fan status", ReadOnly = true, Idempotent = true,
        OpenWorld = false)]
    [Description(
        "Current state of the whole house fan: speed (0 = off), targetSpeed (where a command in " +
        "progress is heading, or null), whether the damper doors are moving, timer minutes left, " +
        "house/attic/outside temperatures in °F, airflow, power draw, and a one-line headline. " +
        "Running the fan cools the house only when outside is cooler than inside; 'verdict' says " +
        "whether it is worth running now. 'online' false or a large ageSeconds means the reading " +
        "is stale. While the doors move the controller reports speed 0; use targetSpeed then.")]
    public McpFanStatus GetFanStatus() => McpFanStatus.From(_fan.GetStatus());

    [McpServerTool(Name = "get_cooldown_rules", Title = "Get cooldown rules", ReadOnly = true,
        Idempotent = true, OpenWorld = false)]
    [Description(
        "The cooldown rules: a ladder of steps, each 'when the house (inside) temperature is below " +
        "belowF °F, run no faster than speed' (speed 0 = off). Rules only ever slow or stop a " +
        "running fan; they never start it or speed it up. 'next' is the step that will fire next, " +
        "if any. A step that has fired stays latched until the house warms back past its " +
        "threshold plus releaseMarginF.")]
    public CooldownView GetCooldownRules() => _fan.GetCooldown();
}

/// <summary>Tools that change the fan or its rules. Omitted when Mcp:ReadOnly is set.</summary>
[McpServerToolType]
public sealed class FanControlTools
{
    private readonly FanService _fan;

    public FanControlTools(FanService fan) => _fan = fan;

    [McpServerTool(Name = "set_fan_speed", Title = "Set fan speed", Idempotent = true, Destructive = false,
        OpenWorld = false)]
    [Description(
        "Sets the whole house fan to an absolute speed, 1 to maxSpeed (see get_fan_status), or 0 " +
        "for off. A whole house fan pulls outside air in through open windows and exhausts it " +
        "through the attic: only start it when windows are open. Starting from off takes about " +
        "30 seconds while the damper doors open, then the fan steps to the requested speed. This " +
        "call returns as soon as the command is queued, before the fan arrives; poll " +
        "get_fan_status and watch targetSpeed/speed to confirm.")]
    public McpCommandResult SetFanSpeed(
        [Description("Target speed: 0 (off) to maxSpeed.")] int speed)
    {
        var outcome = _fan.SetSpeed(speed);
        if (outcome.Error is { } error) throw new McpException(error);

        var message = speed == 0
            ? "Turning the fan off."
            : $"Heading for speed {speed}. From off this takes about 30 seconds for the damper doors.";
        return new McpCommandResult(true, message, McpFanStatus.From(_fan.GetStatus()));
    }

    [McpServerTool(Name = "turn_fan_off", Title = "Turn fan off", Idempotent = true, Destructive = false,
        OpenWorld = false)]
    [Description("Turns the whole house fan off and clears its timer. Always safe to call.")]
    public McpCommandResult TurnFanOff()
    {
        _fan.TurnOff();
        return new McpCommandResult(true, "Turning the fan off.", McpFanStatus.From(_fan.GetStatus()));
    }

    [McpServerTool(Name = "add_timer_hours", Title = "Add timer hours", Destructive = false, OpenWorld = false)]
    [Description(
        "Adds hours (1 to 12) to the fan's run timer; the fan turns itself off when the timer runs " +
        "out. If the fan is off, this STARTS it (at speed 1), so only use it when windows are open. " +
        "Not idempotent: each call adds more time.")]
    public McpCommandResult AddTimerHours(
        [Description("Hours to add, 1 to 12. Values outside the range are clamped.")] int hours)
    {
        var added = _fan.AddTimerHours(hours);
        return new McpCommandResult(true, $"Adding {added} hour(s) to the timer.",
            McpFanStatus.From(_fan.GetStatus()));
    }

    [McpServerTool(Name = "set_cooldown_rules", Title = "Set cooldown rules", Idempotent = true,
        Destructive = false, OpenWorld = false)]
    [Description(
        "Replaces the cooldown rules. Each step means 'when the house (inside) temperature is below " +
        "belowF °F, slow the fan to speed' (0 = off). Rules can only slow or stop a running fan; " +
        "they never start it or speed it up. Colder steps must have lower speeds, thresholds must " +
        "be unique and 20–100 °F, at most six steps. Invalid rules are rejected with an " +
        "explanation and the existing rules are kept.")]
    public async Task<CooldownView> SetCooldownRules(
        [Description("Whether the rules are active.")] bool enabled,
        [Description("The ladder of steps. Order does not matter.")] CooldownStep[] steps,
        [Description("Degrees the house must warm past a fired step before it can fire again (1–15). " +
                     "Omit to keep the current value.")] int? releaseMarginF = null,
        CancellationToken ct = default)
    {
        var current = _fan.GetCooldown();
        var outcome = await _fan.SetCooldownAsync(new CooldownSettings
        {
            Enabled = enabled,
            Steps = steps,
            ReleaseMarginF = releaseMarginF ?? current.ReleaseMarginF,
        }, ct).ConfigureAwait(false);

        if (outcome.Error is { } error) throw new McpException(error);
        return _fan.GetCooldown();
    }
}
