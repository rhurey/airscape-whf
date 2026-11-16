using AirscapeWhf.Fan;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AirscapeWhf.Tests;

public sealed class FanCommandQueueTests
{
    private readonly FakeFan _fan = new();
    private readonly FakeTimeProvider _time = new();
    private readonly FanStateStore _store;
    private readonly FanCommandQueue _queue;

    public FanCommandQueueTests()
    {
        var options = Options.Create(new FanOptions
        {
            Host = "fake",
            MaxSpeed = 5,
            StepDelayMilliseconds = 350,
            DoorPollMilliseconds = 2000,
            DoorTimeoutSeconds = 90,
        });
        _store = new FanStateStore(options, NullLogger<FanStateStore>.Instance);
        _queue = new FanCommandQueue(_fan, _store, options, _time, NullLogger<FanCommandQueue>.Instance);
    }

    /// <summary>Runs an intent to completion, advancing fake time while it waits.</summary>
    private async Task RunAsync(FanIntent intent)
    {
        var task = _queue.RunIntentAsync(intent, CancellationToken.None);
        for (var i = 0; !task.IsCompleted; i++)
        {
            if (i > 5000) throw new TimeoutException("Intent never completed");
            await Task.Delay(1);
            _time.Advance(TimeSpan.FromMilliseconds(250));
        }
        await task;
    }

    private FanCommand[] Sent => _fan.Commands.Select(c => c.Command).ToArray();

    [Fact]
    public async Task Starting_from_off_waits_for_the_doors_before_stepping_the_rest()
    {
        await RunAsync(new FanIntent.SetSpeed(3));

        Assert.Equal(3, _fan.Speed);
        Assert.Equal(new[] { FanCommand.SpeedUp, FanCommand.SpeedUp, FanCommand.SpeedUp }, Sent);
        Assert.DoesNotContain(_fan.Commands, c => c.DuringDoors);
        Assert.Null(_store.Current.TargetSpeed);
    }

    [Fact]
    public async Task Firmware_that_queues_commands_during_doors_still_lands_on_target()
    {
        _fan.IgnoreSpeedCommandsDuringDoors = false;

        await RunAsync(new FanIntent.SetSpeed(4));

        Assert.Equal(4, _fan.Speed);
        Assert.DoesNotContain(_fan.Commands, c => c.DuringDoors);
    }

    [Fact]
    public async Task Stepping_between_running_speeds_needs_no_door_wait()
    {
        _fan.Speed = 2;

        await RunAsync(new FanIntent.SetSpeed(5));

        Assert.Equal(5, _fan.Speed);
        Assert.Equal(3, _fan.Commands.Count);
        Assert.Equal(1, _fan.StatusReads); // just the fresh read at the start
    }

    [Fact]
    public async Task Target_is_published_while_the_doors_move()
    {
        int? seenDuringDoors = null;
        _fan.OnStatusRead = n => { if (n == 2) seenDuringDoors = _store.Current.TargetSpeed; };

        await RunAsync(new FanIntent.SetSpeed(3));

        Assert.Equal(3, seenDuringDoors);
        Assert.Null(_store.Current.TargetSpeed);
    }

    [Fact]
    public async Task A_newer_intent_abandons_the_door_wait()
    {
        _fan.OnStatusRead = n => { if (n == 2) _queue.Enqueue(new FanIntent.Off()); };

        await RunAsync(new FanIntent.SetSpeed(3));

        Assert.Single(_fan.Commands); // the first step, then it stood aside
    }

    [Fact]
    public async Task Off_is_sent_immediately_even_while_the_doors_move()
    {
        _fan.Speed = 1;
        _fan.DoorReadsRemaining = 3; // the fan is mid-start

        await RunAsync(new FanIntent.Off());

        Assert.Equal(FanCommand.Off, _fan.Commands[0].Command);
        Assert.True(_fan.Commands[0].DuringDoors);
        Assert.Equal(0, _fan.Speed);
    }

    [Fact]
    public async Task Doors_that_never_settle_give_up_and_clear_the_target()
    {
        _fan.DoorReadsPerTransition = int.MaxValue;

        await RunAsync(new FanIntent.SetSpeed(3));

        Assert.Single(_fan.Commands);
        Assert.Null(_store.Current.TargetSpeed);
    }

    [Fact]
    public async Task Steps_from_the_real_speed_when_something_else_moved_it()
    {
        // The cache says 1; the wall switch has since put it at 4.
        _store.RecordSuccess(new FanRawStatus { FanSpeed = 1, DoorInProcess = 0 });
        _fan.Speed = 4;

        await RunAsync(new FanIntent.SetSpeed(2));

        Assert.Equal(2, _fan.Speed);
        Assert.Equal(new[] { FanCommand.SpeedDown, FanCommand.SpeedDown }, Sent);
    }

    [Fact]
    public async Task Nudge_during_door_travel_counts_from_the_target()
    {
        _store.SetTarget(3);
        _fan.Speed = 1;
        _fan.DoorReadsRemaining = 2;

        await RunAsync(new FanIntent.Nudge(1));

        Assert.Equal(4, _fan.Speed);
    }

    [Fact]
    public async Task Timer_hours_wait_for_the_doors_the_first_hour_opens()
    {
        await RunAsync(new FanIntent.AddTimer(2));

        Assert.Equal(new[] { FanCommand.AddTimerHour, FanCommand.AddTimerHour }, Sent);
        Assert.False(_fan.Commands[1].DuringDoors);
    }
}
