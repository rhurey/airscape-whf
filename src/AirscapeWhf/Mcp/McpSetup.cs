using System.Text;

namespace AirscapeWhf.Mcp;

/// <summary>
/// Wires the MCP endpoint at <c>/mcp</c>. The endpoint is never served without auth:
/// if no valid token is configured, it is simply not mapped and the rest of the app
/// carries on.
/// </summary>
internal static class McpSetup
{
    public const string Path = "/mcp";
    public const string Scheme = "McpToken";
    public const string Policy = "Mcp";
    public const int MinimumTokenLength = 32;

    /// <summary>Registers services. Returns whether /mcp should be mapped, and why not if not.</summary>
    public static (bool Serve, string Reason) AddMcp(WebApplicationBuilder builder, string configDir)
    {
        var config = builder.Configuration;
        if (!config.GetValue("Mcp:Enabled", true))
            return (false, "MCP is disabled (Mcp:Enabled is false).");

        var mode = config["Mcp:Auth:Mode"] ?? "Token";
        if (!string.Equals(mode, "Token", StringComparison.OrdinalIgnoreCase))
            return (false, $"MCP auth mode '{mode}' is not supported; only 'Token' is implemented.");

        var tokenFile = System.IO.Path.Combine(configDir, "mcp-token");
        var token = ReadToken(config, tokenFile);
        if (token is null)
            return (false, $"MCP is off: no token. Create one with `openssl rand -hex 32 > {tokenFile}` " +
                           "(or set Mcp__Token) and restart.");
        if (token.Length < MinimumTokenLength)
            return (false, $"MCP is off: the token must be at least {MinimumTokenLength} characters. " +
                           $"Regenerate it with `openssl rand -hex 32 > {tokenFile}` and restart.");

        // Auth is a normal ASP.NET Core scheme + policy so the endpoint doesn't care how
        // a caller proved itself. Future 'Entra' mode: register JwtBearer with authority
        // and audience from Mcp:Auth, point the Mcp policy at that scheme, and serve the
        // MCP protected-resource metadata so clients can discover the authorization server.
        builder.Services
            .AddAuthentication(Scheme)
            .AddScheme<StaticTokenOptions, StaticTokenAuthenticationHandler>(Scheme,
                options => options.Token = Encoding.UTF8.GetBytes(token));
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(Policy, policy => policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser());

        var mcp = builder.Services
            .AddMcpServer(options => options.ServerInfo = new() { Name = "airscape-whf", Version = "1.0" })
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools<FanReadTools>();

        var readOnly = config.GetValue("Mcp:ReadOnly", false);
        if (!readOnly) mcp.WithTools<FanControlTools>();

        return (true, $"MCP at {Path} ({(readOnly ? "read-only" : "read and control")}, bearer token).");
    }

    public static void MapMcp(WebApplication app) =>
        app.MapMcp(Path).RequireAuthorization(Policy);

    private static string? ReadToken(IConfiguration config, string tokenFile)
    {
        if (config["Mcp:Token"] is { } fromConfig && !string.IsNullOrWhiteSpace(fromConfig))
            return fromConfig.Trim();

        if (!File.Exists(tokenFile)) return null;
        var firstLine = File.ReadLines(tokenFile).FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(firstLine) ? null : firstLine;
    }
}
