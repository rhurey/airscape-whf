using Microsoft.Extensions.Options;

namespace AirscapeWhf.Fan;

/// <summary>
/// The single reader. Polls status on an interval and backs off when the controller
/// stops answering, so a wedged fan doesn't turn into a tight retry loop against a
/// device that only has two sockets to give.
/// </summary>
public sealed class FanPoller : BackgroundService
{
    private readonly FanClient _client;
    private readonly FanStateStore _store;
    private readonly FanOptions _options;
    private readonly ILogger<FanPoller> _log;

    public FanPoller(FanClient client, FanStateStore store, IOptions<FanOptions> options, ILogger<FanPoller> log)
    {
        _client = client;
        _store = store;
        _options = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var baseInterval = TimeSpan.FromSeconds(Math.Max(5, _options.PollIntervalSeconds));
        var failures = 0;

        _log.LogInformation("Polling {Host} every {Interval}s", _options.Host, baseInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var raw = await _client.GetStatusAsync(stoppingToken).ConfigureAwait(false);
                _store.RecordSuccess(raw);
                failures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                _store.RecordFailure(ex);
                _log.LogDebug(ex, "Poll attempt {Attempt} failed", failures);
            }

            var delay = failures == 0
                ? baseInterval
                // 2x, 4x, 8x ... capped at 8x. Enough to stop hammering, short enough
                // that recovery is noticed within a couple of minutes.
                : TimeSpan.FromTicks(baseInterval.Ticks * Math.Min(8, 1L << Math.Min(failures, 3)));

            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
