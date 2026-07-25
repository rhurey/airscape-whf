using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace AirscapeWhf.Fan;

/// <summary>
/// Holds the last good reading and fans it out to every connected browser.
/// <para>
/// This is what decouples browser count from fan load: one poller writes here, any
/// number of readers subscribe, and none of them ever touch the controller.
/// </para>
/// </summary>
public sealed class FanStateStore
{
    private readonly ConcurrentDictionary<Guid, Channel<FanSnapshot>> _subscribers = new();
    private readonly FanOptions _options;
    private readonly ILogger<FanStateStore> _log;
    private readonly object _sync = new();

    private FanSnapshot _current = FanSnapshot.Unknown;
    private int _consecutiveFailures;

    public FanStateStore(IOptions<FanOptions> options, ILogger<FanStateStore> log)
    {
        _options = options.Value;
        _log = log;
        _current = FanSnapshot.Unknown with { MaxSpeed = _options.MaxSpeed };
    }

    /// <summary>Last known state, with its age recomputed as of now.</summary>
    public FanSnapshot Current
    {
        get { lock (_sync) return _current.WithAge(DateTimeOffset.UtcNow); }
    }

    public int SubscriberCount => _subscribers.Count;

    public void RecordSuccess(FanRawStatus raw)
    {
        FanSnapshot snapshot;
        lock (_sync)
        {
            _consecutiveFailures = 0;
            snapshot = FanSnapshot.From(raw, Math.Max(_options.MaxSpeed, _current.MaxSpeed), DateTimeOffset.UtcNow);
            _current = snapshot;
        }
        Broadcast(snapshot);
    }

    public void RecordFailure(Exception error)
    {
        FanSnapshot snapshot;
        bool justWentOffline;

        lock (_sync)
        {
            _consecutiveFailures++;
            justWentOffline = _consecutiveFailures == _options.FailuresBeforeOffline;

            // Keep serving the last good reading. A single dropped poll is normal on a
            // device with two sockets; blanking the UI over it would recreate exactly
            // the flakiness this service exists to hide.
            var online = _consecutiveFailures < _options.FailuresBeforeOffline;
            _current = _current with { Online = online && _current.ObservedAt is not null, LastError = error.Message };
            snapshot = _current.WithAge(DateTimeOffset.UtcNow);
        }

        if (justWentOffline)
            _log.LogWarning("Fan unreachable after {Count} attempts: {Error}",
                _options.FailuresBeforeOffline, error.Message);

        Broadcast(snapshot);
    }

    private void Broadcast(FanSnapshot snapshot)
    {
        foreach (var channel in _subscribers.Values)
        {
            // Bounded + DropOldest: a browser that stops reading costs us one stale
            // snapshot, not unbounded memory.
            channel.Writer.TryWrite(snapshot);
        }
    }

    public async IAsyncEnumerable<FanSnapshot> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<FanSnapshot>(new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        _subscribers[id] = channel;
        try
        {
            // Seed the new subscriber immediately so the page paints without waiting
            // for the next poll tick.
            yield return Current;

            await foreach (var snapshot in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return snapshot;
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
        }
    }
}
