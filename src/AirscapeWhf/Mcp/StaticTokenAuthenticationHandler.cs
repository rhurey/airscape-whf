using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AirscapeWhf.Mcp;

public sealed class StaticTokenOptions : AuthenticationSchemeOptions
{
    /// <summary>UTF-8 bytes of the shared token. Never logged.</summary>
    public byte[] Token { get; set; } = [];
}

/// <summary>
/// Accepts <c>Authorization: Bearer &lt;token&gt;</c> where the token matches the one in
/// <c>/config/mcp-token</c>. Built as an ordinary ASP.NET Core authentication scheme so
/// that moving to OAuth2 (Entra ID) later is a matter of registering JwtBearer and
/// pointing the <c>Mcp</c> policy at it, with no change to the endpoint.
/// </summary>
public sealed class StaticTokenAuthenticationHandler : AuthenticationHandler<StaticTokenOptions>
{
    public StaticTokenAuthenticationHandler(IOptionsMonitor<StaticTokenOptions> options,
        ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());

        var presented = Encoding.UTF8.GetBytes(header[prefix.Length..].Trim());
        var expected = Options.Token;

        // Constant time for equal lengths; a length mismatch fails immediately, which
        // reveals only the length of a random token.
        if (expected.Length == 0 || !CryptographicOperations.FixedTimeEquals(presented, expected))
            return Task.FromResult(AuthenticateResult.Fail("Invalid MCP token."));

        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "mcp-token") }, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}
