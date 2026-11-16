using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AirscapeWhf.Fan;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AirscapeWhf.Tests;

/// <summary>The app as deployed, with the controller replaced by a fake.</summary>
internal sealed class AppFactory : WebApplicationFactory<Program>
{
    public const string Token = "0123456789abcdef0123456789abcdef-test-token";

    private readonly Dictionary<string, string?> _settings;

    public AppFactory(Dictionary<string, string?>? settings = null)
    {
        _settings = new Dictionary<string, string?>
        {
            ["Fan:Host"] = "fake",
            ["Mcp:Token"] = Token,
            ["Cooldown:Path"] = Path.Combine(Path.GetTempPath(), $"whf-test-{Guid.NewGuid():N}.json"),
        };
        foreach (var (k, v) in settings ?? new()) _settings[k] = v;
    }

    public FakeFan Fan { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (key, value) in _settings) builder.UseSetting(key, value);
        builder.ConfigureTestServices(services => services.AddSingleton<IFanClient>(Fan));
    }

    public async Task<McpClient> ConnectAsync(string token = Token)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(Server.BaseAddress, "/mcp"),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        }, CreateClient(), ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }
}

public sealed class McpEndpointTests
{
    private static HttpRequestMessage Initialize(string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
                Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task No_header_is_rejected()
    {
        await using var app = new AppFactory();
        var response = await app.CreateClient().SendAsync(Initialize(null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Wrong_token_is_rejected()
    {
        await using var app = new AppFactory();
        var response = await app.CreateClient().SendAsync(Initialize("not-the-token-at-all-but-just-as-long!"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Right_token_reaches_the_mcp_handler()
    {
        await using var app = new AppFactory();
        var response = await app.CreateClient().SendAsync(Initialize(AppFactory.Token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Without_a_token_mcp_is_not_served_but_the_app_is()
    {
        await using var app = new AppFactory(new() { ["Mcp:Token"] = null });
        var client = app.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Initialize(AppFactory.Token))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task A_short_token_is_refused()
    {
        await using var app = new AppFactory(new() { ["Mcp:Token"] = "too-short" });
        var response = await app.CreateClient().SendAsync(Initialize("too-short"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_rest_api_stays_open()
    {
        await using var app = new AppFactory();
        Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().GetAsync("/api/status")).StatusCode);
    }

    [Fact]
    public async Task Lists_all_six_tools()
    {
        await using var app = new AppFactory();
        await using var client = await app.ConnectAsync();

        var names = (await client.ListToolsAsync()).Select(t => t.Name).Order().ToArray();
        Assert.Equal(new[]
        {
            "add_timer_hours", "get_cooldown_rules", "get_fan_status",
            "set_cooldown_rules", "set_fan_speed", "turn_fan_off",
        }, names);
    }

    [Fact]
    public async Task Read_only_mode_lists_only_the_read_tools()
    {
        await using var app = new AppFactory(new() { ["Mcp:ReadOnly"] = "true" });
        await using var client = await app.ConnectAsync();

        var names = (await client.ListToolsAsync()).Select(t => t.Name).Order().ToArray();
        Assert.Equal(new[] { "get_cooldown_rules", "get_fan_status" }, names);
    }

    [Fact]
    public async Task Set_fan_speed_queues_and_publishes_the_target()
    {
        await using var app = new AppFactory();
        await using var client = await app.ConnectAsync();

        var result = await client.CallToolAsync("set_fan_speed", new Dictionary<string, object?> { ["speed"] = 3 });
        Assert.NotEqual(true, result.IsError);
        Assert.Contains("\"targetSpeed\":3", Text(result));
    }

    [Fact]
    public async Task Out_of_range_speed_is_a_tool_error()
    {
        await using var app = new AppFactory();
        await using var client = await app.ConnectAsync();

        var result = await client.CallToolAsync("set_fan_speed", new Dictionary<string, object?> { ["speed"] = 9 });
        Assert.True(result.IsError);
        Assert.Contains("between 0 and 5", Text(result));
    }

    [Fact]
    public async Task Invalid_cooldown_rules_come_back_with_the_reason()
    {
        await using var app = new AppFactory();
        await using var client = await app.ConnectAsync();

        var result = await client.CallToolAsync("set_cooldown_rules", new Dictionary<string, object?>
        {
            ["enabled"] = true,
            // The colder step runs faster, so it could never fire.
            ["steps"] = new[] { new { belowF = 65, speed = 1 }, new { belowF = 60, speed = 3 } },
        });

        Assert.True(result.IsError);
        Assert.Contains("would never fire", Text(result));
    }

    [Fact]
    public async Task Valid_cooldown_rules_are_saved()
    {
        await using var app = new AppFactory();
        await using var client = await app.ConnectAsync();

        var result = await client.CallToolAsync("set_cooldown_rules", new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["steps"] = new[] { new { belowF = 66, speed = 0 } },
        });

        Assert.NotEqual(true, result.IsError);
        var rest = await app.CreateClient().GetStringAsync("/api/cooldown");
        Assert.Contains("\"belowF\":66", rest);
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
