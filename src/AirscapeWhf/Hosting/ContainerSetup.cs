using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

namespace AirscapeWhf.Hosting;

/// <summary>
/// The container contract: everything site-specific lives in one mapped-in config
/// directory (default <c>/config</c>), and state lives in <c>/data</c>. Nothing about
/// a particular house is baked into the image or the compose file.
/// <code>
/// /config/appsettings.json      required: Fan:Host; any other setting
/// /config/certs/fullchain.pem   optional: enables HTTPS with privkey.pem
/// /config/certs/privkey.pem
/// /config/mcp-token             required for /mcp to be served
/// </code>
/// Environment variables still override the file, so <c>Fan__Host=…</c> works for a
/// quick run.
/// </summary>
internal static class ContainerSetup
{
    public const string ConfigDirVariable = "AIRSCAPE_CONFIG_DIR";

    public static string ConfigDir =>
        Environment.GetEnvironmentVariable(ConfigDirVariable) is { Length: > 0 } dir ? dir : "/config";

    /// <summary>
    /// Adds <c>&lt;config&gt;/appsettings.json</c> just below the environment variables,
    /// so the file beats the built-in defaults and env vars beat the file.
    /// </summary>
    public static void AddConfigDirectory(IConfigurationBuilder configuration, string configDir)
    {
        var source = new JsonConfigurationSource
        {
            Path = Path.Combine(Path.GetFullPath(configDir), "appsettings.json"),
            Optional = true,
            ReloadOnChange = false,
        };
        source.ResolveFileProvider();

        var sources = configuration.Sources;
        var lastEnv = -1;
        for (var i = 0; i < sources.Count; i++)
            if (sources[i] is EnvironmentVariablesConfigurationSource) lastEnv = i;

        if (lastEnv < 0) sources.Add(source);
        else sources.Insert(lastEnv, source);
    }

    /// <summary>
    /// HTTP always; HTTPS only when a PEM pair is present. A missing certificate used
    /// to stop the container starting, which is the wrong failure for a LAN appliance.
    /// Returns a one-line description for the startup log.
    /// </summary>
    public static string ConfigureListeners(WebApplicationBuilder builder, string configDir)
    {
        var config = builder.Configuration;
        var httpPort = config.GetValue("Http:Port", 8080);
        var httpsPort = config.GetValue("Https:Port", 8443);
        var certPath = config["Https:CertificatePath"] ?? Path.Combine(configDir, "certs", "fullchain.pem");
        var keyPath = config["Https:KeyPath"] ?? Path.Combine(configDir, "certs", "privkey.pem");

        X509Certificate2? certificate = null;
        string description;

        if (File.Exists(certPath) && File.Exists(keyPath))
        {
            // Kestrel reads the PEM once; a renewed certificate needs a restart.
            certificate = X509Certificate2.CreateFromPemFile(certPath, keyPath);
            description = $"HTTP on {httpPort}, HTTPS on {httpsPort} using {certPath} " +
                          $"(expires {certificate.NotAfter:yyyy-MM-dd})";
        }
        else
        {
            description = $"HTTP on {httpPort}; HTTPS off because {certPath} and {keyPath} " +
                          "are not both present";
        }

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ListenAnyIP(httpPort);
            if (certificate is not null)
                kestrel.ListenAnyIP(httpsPort, listen => listen.UseHttps(certificate));
        });

        return description;
    }

    /// <summary>
    /// The chiseled image has no shell or curl, so the container healthcheck runs the
    /// app itself in this mode: one local GET of /healthz, exit 0 or 1.
    /// </summary>
    public static async Task<int> HealthcheckAsync()
    {
        var configuration = new ConfigurationBuilder();
        AddConfigDirectory(configuration, ConfigDir);
        configuration.AddEnvironmentVariables();
        var port = configuration.Build().GetValue("Http:Port", 8080);

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await http.GetAsync($"http://127.0.0.1:{port}/healthz");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"healthcheck failed: {ex.Message}");
            return 1;
        }
    }
}
