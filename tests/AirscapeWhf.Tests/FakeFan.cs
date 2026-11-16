using AirscapeWhf.Fan;

namespace AirscapeWhf.Tests;

/// <summary>
/// A scripted controller. Mirrors the parts of the real one the stepper has to cope
/// with: crossing the on/off boundary starts the damper doors, the reported speed is 0
/// while they move, and speed commands sent during door travel are ignored.
/// Door travel is counted in status reads rather than time, so tests are deterministic.
/// </summary>
internal sealed class FakeFan : IFanClient
{
    public int Speed { get; set; }
    public int MaxSpeed { get; set; } = 5;

    /// <summary>Status reads that still report the doors as moving.</summary>
    public int DoorReadsRemaining { get; set; }

    /// <summary>How many status reads a door transition lasts.</summary>
    public int DoorReadsPerTransition { get; set; } = 3;

    /// <summary>False models firmware that queues speed commands during door travel.</summary>
    public bool IgnoreSpeedCommandsDuringDoors { get; set; } = true;

    public List<(FanCommand Command, bool DuringDoors)> Commands { get; } = new();
    public int StatusReads { get; private set; }

    /// <summary>Runs before each status read is answered, for injecting events mid-sequence.</summary>
    public Action<int>? OnStatusRead { get; set; }

    private bool DoorsMoving => DoorReadsRemaining > 0;

    public Task<FanRawStatus> GetStatusAsync(CancellationToken ct)
    {
        StatusReads++;
        OnStatusRead?.Invoke(StatusReads);
        var status = Report();
        if (DoorReadsRemaining > 0 && DoorReadsRemaining != int.MaxValue) DoorReadsRemaining--;
        return Task.FromResult(status);
    }

    public Task<FanRawStatus> SendCommandAsync(FanCommand command, CancellationToken ct)
    {
        var during = DoorsMoving;
        Commands.Add((command, during));
        var before = Speed;

        var ignored = during && IgnoreSpeedCommandsDuringDoors;
        switch (command)
        {
            case FanCommand.SpeedUp when !ignored:
                Speed = Math.Min(MaxSpeed, Speed + 1);
                break;
            case FanCommand.SpeedDown when !ignored:
                Speed = Math.Max(0, Speed - 1);
                break;
            case FanCommand.Off:
                Speed = 0;
                break;
            case FanCommand.AddTimerHour when Speed == 0:
                Speed = 1;
                break;
        }

        if ((before == 0) != (Speed == 0)) DoorReadsRemaining = DoorReadsPerTransition;
        return Task.FromResult(Report());
    }

    private FanRawStatus Report() => new()
    {
        FanSpeed = DoorsMoving ? 0 : Speed,
        DoorInProcess = DoorsMoving ? 1 : 0,
    };
}
