using System.Net;
using System.Text.Json;
using AirscapeWhf.Fan;
using AirscapeWhf.Metrics;

namespace AirscapeWhf.Tests;

public sealed class PrometheusMetricsTests
{
    private const int Poll = 20;
    private static readonly DateTimeOffset At = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private static FanSnapshot Fresh() => new()
    {
        Online = true,
        ObservedAt = At,
        AgeSeconds = 4,
        Speed = 3,
        Cfm = 1800,
        PowerWatts = 120,
        HouseTempF = 74,
        AtticTempF = 95,
        OutsideTempF = 61,
        Model = "3.5e",
        SoftwareVersion = "2.15.1",
    };

    private static string[] Lines(string body) => body.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string[] Samples(string body) => Lines(body).Where(l => !l.StartsWith('#')).ToArray();

    [Fact]
    public void Fresh_reading_with_all_sensors()
    {
        var body = PrometheusMetrics.Render(Fresh(), Poll);

        Assert.Equal(new[]
        {
            "airscape_up 1",
            "airscape_last_reading_age_seconds 4",
            "airscape_temperature_fahrenheit{sensor=\"outside\"} 61",
            "airscape_temperature_fahrenheit{sensor=\"inside\"} 74",
            "airscape_temperature_fahrenheit{sensor=\"attic\"} 95",
            "airscape_fan_speed 3",
            "airscape_fan_power_watts 120",
            "airscape_fan_airflow_cfm 1800",
            "airscape_info{model=\"3.5e\",software_version=\"2.15.1\"} 1",
        }, Samples(body));
        Assert.EndsWith("\n", body);
    }

    [Fact]
    public void Each_family_has_one_help_and_one_type_line_before_its_samples()
    {
        var lines = Lines(PrometheusMetrics.Render(Fresh(), Poll));
        var seen = new HashSet<string>();
        string? current = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("# HELP "))
            {
                current = line.Split(' ')[2];
                Assert.True(seen.Add(current), $"{current} declared twice");
            }
            else if (line.StartsWith("# TYPE "))
            {
                Assert.Equal($"# TYPE {current} gauge", line);
            }
            else
            {
                var name = line.Split('{', ' ')[0];
                Assert.Equal(current, name);
            }
        }
    }

    [Fact]
    public void A_missing_sensor_drops_only_its_own_series()
    {
        var body = PrometheusMetrics.Render(Fresh() with { AtticTempF = null }, Poll);

        Assert.Contains("airscape_temperature_fahrenheit{sensor=\"outside\"} 61", Samples(body));
        Assert.Contains("airscape_temperature_fahrenheit{sensor=\"inside\"} 74", Samples(body));
        Assert.DoesNotContain("attic", body);
    }

    [Fact]
    public void No_sensors_at_all_omits_the_temperature_family()
    {
        var body = PrometheusMetrics.Render(
            Fresh() with { HouseTempF = null, AtticTempF = null, OutsideTempF = null }, Poll);

        Assert.DoesNotContain("airscape_temperature_fahrenheit", body);
        Assert.Contains("airscape_up 1", Samples(body));
    }

    [Fact]
    public void Offline_reports_down_and_drops_the_cached_values()
    {
        var body = PrometheusMetrics.Render(Fresh() with { Online = false }, Poll);

        Assert.Equal(new[] { "airscape_up 0", "airscape_last_reading_age_seconds 4" }, Samples(body));
    }

    [Fact]
    public void Online_but_past_the_freshness_window_counts_as_down()
    {
        // max(60, 3 x 20) = 60 seconds.
        Assert.Contains("airscape_up 1", Samples(PrometheusMetrics.Render(Fresh() with { AgeSeconds = 60 }, Poll)));

        var body = PrometheusMetrics.Render(Fresh() with { AgeSeconds = 61 }, Poll);
        Assert.Equal(new[] { "airscape_up 0", "airscape_last_reading_age_seconds 61" }, Samples(body));
    }

    [Fact]
    public void Freshness_window_scales_with_a_long_poll_interval()
    {
        var snapshot = Fresh() with { AgeSeconds = 100 };
        Assert.Contains("airscape_up 1", Samples(PrometheusMetrics.Render(snapshot, 40)));
        Assert.Contains("airscape_up 0", Samples(PrometheusMetrics.Render(snapshot, 20)));
    }

    [Fact]
    public void Never_observed_reports_only_up_zero()
    {
        var body = PrometheusMetrics.Render(FanSnapshot.Unknown, Poll);

        Assert.Equal(new[] { "airscape_up 0" }, Samples(body));
    }

    [Fact]
    public void Negative_temperatures_render_with_invariant_culture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            // A culture whose negative sign is not ASCII hyphen-minus.
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("sv-SE");
            var body = PrometheusMetrics.Render(Fresh() with { OutsideTempF = -7 }, Poll);
            Assert.Contains("airscape_temperature_fahrenheit{sensor=\"outside\"} -7", Samples(body));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Label_values_are_escaped()
    {
        var body = PrometheusMetrics.Render(Fresh() with { Model = "a\\b\"c\nd" }, Poll);

        Assert.Contains("airscape_info{model=\"a\\\\b\\\"c\\nd\",software_version=\"2.15.1\"} 1", Samples(body));
    }

    [Fact]
    public void Null_info_labels_are_omitted()
    {
        var body = PrometheusMetrics.Render(Fresh() with { Model = null }, Poll);
        Assert.Contains("airscape_info{software_version=\"2.15.1\"} 1", Samples(body));

        body = PrometheusMetrics.Render(Fresh() with { Model = null, SoftwareVersion = null }, Poll);
        Assert.Contains("airscape_info 1", Samples(body));
    }

    // ---- through the app ------------------------------------------------

    private static async Task WaitForFirstReading(HttpClient client)
    {
        for (var i = 0; i < 100; i++)
        {
            using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/status"));
            if (doc.RootElement.GetProperty("online").GetBoolean()) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("The poller never recorded a reading.");
    }

    private static AppFactory WithSensors(Dictionary<string, string?>? settings = null)
    {
        var app = new AppFactory(settings);
        app.Fan.OutsideTempF = 58;
        app.Fan.HouseTempF = 72;
        app.Fan.AtticTempF = 80;
        return app;
    }

    [Fact]
    public async Task Metrics_is_served_without_a_token_when_mcp_is_enabled()
    {
        await using var app = WithSensors();
        var client = app.CreateClient();
        await WaitForFirstReading(client);

        var response = await client.GetAsync("/metrics");

        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(PrometheusMetrics.ContentType, response.Content.Headers.ContentType?.ToString());
        Assert.Contains("airscape_up 1\n", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Metrics_is_served_when_mcp_is_disabled()
    {
        await using var app = WithSensors(new() { ["Mcp:Enabled"] = "false" });
        var client = app.CreateClient();
        await WaitForFirstReading(client);

        var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(PrometheusMetrics.ContentType, response.Content.Headers.ContentType?.ToString());
        Assert.Contains("airscape_up 1\n", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Outside_temperature_matches_the_status_api()
    {
        await using var app = WithSensors();
        var client = app.CreateClient();
        await WaitForFirstReading(client);

        using var status = JsonDocument.Parse(await client.GetStringAsync("/api/status"));
        var outside = status.RootElement.GetProperty("outsideTempF").GetInt32();
        var metrics = await client.GetStringAsync("/metrics");

        Assert.Equal(58, outside);
        Assert.Contains($"airscape_temperature_fahrenheit{{sensor=\"outside\"}} {outside}\n", metrics);
    }
}
