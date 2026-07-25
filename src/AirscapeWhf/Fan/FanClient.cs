using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AirscapeWhf.Fan;

public enum FanCommand
{
    SpeedUp = 1,
    AddTimerHour = 2,
    SpeedDown = 3,
    Off = 4,
}

/// <summary>
/// The only thing in this process that is allowed to open a socket to the controller.
/// <para>
/// The Microchip TCP/IP stack the fan runs is compiled with a fixed, very small number
/// of concurrent HTTP connections — typically two — and it has no listen backlog, so
/// excess SYNs are dropped rather than refused. A browser opening its normal six
/// parallel connections will therefore stall on SYN retransmit timers and look
/// intermittently dead. Every request in this class is serialised through
/// <see cref="_gate"/>, capped at one connection by the handler, and closed rather than
/// kept alive, so the fan never sees more than one connection from us at a time.
/// </para>
/// </summary>
public sealed class FanClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    /// <summary>
    /// Substitutes rather than throws on malformed input. The controller's status body
    /// contains bytes that are not valid UTF-8, so a strict decoder would fail before
    /// the salvage in <see cref="Parse"/> ever got a chance to run.
    /// </summary>
    private static readonly UTF8Encoding LenientUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<FanClient> _log;
    private readonly FanOptions _options;

    /// <summary>
    /// Some firmware revisions serve status at <c>.html</c> rather than <c>.cgi</c>.
    /// We discover which on first use and then stop guessing.
    /// </summary>
    private string? _statusPath;

    /// <summary>Set once we've told the operator this firmware emits invalid JSON.</summary>
    private bool _warnedAboutSalvage;

    public FanClient(IOptions<FanOptions> options, ILogger<FanClient> log)
    {
        _options = options.Value;
        _log = log;

        if (string.IsNullOrWhiteSpace(_options.Host))
            throw new InvalidOperationException(
                "No fan host configured. Set Fan__Host to the controller's IP or hostname.");

        var handler = new SocketsHttpHandler
        {
            // Belt: never let the pool widen beyond a single connection.
            MaxConnectionsPerServer = 1,
            // The controller answers every request with Connection: close and no
            // Content-Length, so responses are delimited by the close itself and no
            // connection is ever reusable. These settings therefore mostly agree with
            // what the device already does; they are kept so our behaviour does not
            // depend on the firmware continuing to be well-behaved about it.
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(1),
            PooledConnectionLifetime = TimeSpan.FromSeconds(30),
            ConnectTimeout = TimeSpan.FromSeconds(Math.Max(2, _options.TimeoutSeconds - 1)),
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://{_options.Host}/"),
            Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("airscape-whf/1.0");
    }

    public async Task<FanRawStatus> GetStatusAsync(CancellationToken ct)
    {
        if (_statusPath is not null)
            return await SendAsync(_statusPath, ct).ConfigureAwait(false);

        // First call: find out which spelling this firmware answers to.
        foreach (var candidate in new[] { "status.json.cgi", "status.json.html" })
        {
            try
            {
                var status = await SendAsync(candidate, ct).ConfigureAwait(false);
                _statusPath = candidate;
                _log.LogInformation("Fan status endpoint is /{Path}", candidate);
                return status;
            }
            catch (FanUnavailableException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                _log.LogDebug("/{Path} returned 404, trying the next spelling", candidate);
            }
        }

        throw new FanUnavailableException(
            "Controller answered but serves no known status endpoint. If /mpfsupload is all " +
            "it returns, its MPFS filesystem image is likely missing.");
    }

    /// <summary>
    /// Issues one command. The controller returns its post-command status in the same
    /// response, so callers get a fresh reading for free — no follow-up poll needed.
    /// </summary>
    public async Task<FanRawStatus> SendCommandAsync(FanCommand command, CancellationToken ct)
        => await SendAsync($"fanspd.cgi?dir={(int)command}", ct).ConfigureAwait(false);

    private async Task<FanRawStatus> SendAsync(string path, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            // Suspenders: tell the stack we are done the moment it has answered.
            request.Headers.ConnectionClose = true;

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new FanUnavailableException(
                    $"Controller returned {(int)response.StatusCode} for /{path}.",
                    response.StatusCode);

            // Decode explicitly instead of using ReadAsStringAsync. The controller
            // labels this document text/html with no charset, so the framework would be
            // picking an encoding from a header we know to be wrong.
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var body = LenientUtf8.GetString(bytes);

            // Only status.json.cgi serves JSON. fanspd.cgi answers with a tag stream
            // that is not JSON and not valid XML either.
            return path.StartsWith("fanspd.cgi", StringComparison.Ordinal)
                ? ParseTagStream(body, path)
                : Parse(body, path);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new FanUnavailableException(
                $"Controller did not answer within {_options.TimeoutSeconds}s.", inner: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new FanUnavailableException($"Could not reach the controller: {ex.Message}", inner: ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private FanRawStatus Parse(string body, string path)
    {
        var trimmed = Trim(body)
            ?? throw new FanUnavailableException(
                $"/{path} did not return JSON. The controller's filesystem image may be missing.");

        // Fast path: firmware that emits a well-formed document.
        if (TryDeserialize(trimmed, out var raw))
            return raw.Resolve();

        // 2.15.1 puts a raw binary buffer in server_response, which is not legal inside
        // a JSON string. Cut that one field out and the rest parses normally.
        if (!JsonRepair.TryExcise(trimmed, out var repaired) || !TryDeserialize(repaired, out raw))
            throw new FanUnavailableException(
                $"/{path} returned JSON this build can't parse. Capture it and run it " +
                "through tools/salvage_check.py to see what changed.");

        if (!_warnedAboutSalvage)
        {
            _warnedAboutSalvage = true;
            _log.LogInformation(
                "Controller emits an unparseable server_response field; excising it on every " +
                "read. This is a known firmware defect, not a fault here.");
        }

        return raw.Resolve();
    }

    private FanRawStatus ParseTagStream(string body, string path)
    {
        return TagStreamParser.Parse(body)?.Resolve()
               ?? throw new FanUnavailableException(
                   $"/{path} returned something that isn't a controller status response.");
    }

    private static string? Trim(string body)
    {
        var start = body.IndexOf('{');
        var end = body.LastIndexOf('}');
        return start >= 0 && end > start ? body[start..(end + 1)] : null;
    }

    private static bool TryDeserialize(string json, out FanRawStatus raw)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<FanRawStatus>(json, JsonOptions);
            raw = parsed!;
            return parsed is not null;
        }
        catch (JsonException)
        {
            raw = null!;
            return false;
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
        };
        options.Converters.Add(new LenientInt32Converter());
        return options;
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}

/// <summary>Anything that stops us getting a usable reading. Expected, not exceptional.</summary>
public sealed class FanUnavailableException : Exception
{
    public FanUnavailableException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
        : base(message, inner) => StatusCode = statusCode;

    public HttpStatusCode? StatusCode { get; }
}
