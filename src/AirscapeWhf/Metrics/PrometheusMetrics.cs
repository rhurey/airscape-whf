using System.Globalization;
using System.Text;
using AirscapeWhf.Fan;

namespace AirscapeWhf.Metrics;

/// <summary>
/// Renders a snapshot in the Prometheus text exposition format, version 0.0.4.
/// <para>
/// Written by hand rather than with a metrics package: it is seven gauges, all read
/// from one cached snapshot at scrape time, so there is nothing to register or update.
/// </para>
/// <para>
/// When the reading is not fresh (see <see cref="FanSnapshot.IsFresh"/>) only
/// <c>airscape_up</c> and the reading age are emitted. The store keeps the last good
/// reading after the fan goes quiet; exporting it would draw a flat line at the last
/// temperature, which is wrong data that looks right. Dropping the series lets
/// Prometheus mark them stale, so the graph shows a gap instead.
/// </para>
/// </summary>
public static class PrometheusMetrics
{
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    public static string Render(FanSnapshot snapshot, int pollIntervalSeconds)
    {
        var fresh = snapshot.IsFresh(pollIntervalSeconds);
        var sb = new StringBuilder();

        Family(sb, "airscape_up", "1 if the fan controller answered recently, otherwise 0.");
        Sample(sb, "airscape_up", null, fresh ? 1 : 0);

        if (snapshot.ObservedAt is not null)
        {
            Family(sb, "airscape_last_reading_age_seconds", "Seconds since the controller last answered a status read.");
            Sample(sb, "airscape_last_reading_age_seconds", null, snapshot.AgeSeconds);
        }

        if (!fresh) return sb.ToString();

        var temperatures = new (string Sensor, int? Value)[]
        {
            ("outside", snapshot.OutsideTempF),
            ("inside", snapshot.HouseTempF),
            ("attic", snapshot.AtticTempF),
        };
        if (temperatures.Any(t => t.Value is not null))
        {
            Family(sb, "airscape_temperature_fahrenheit", "Temperature reported by the fan controller, in whole degrees Fahrenheit.");
            foreach (var (sensor, value) in temperatures)
                if (value is { } v)
                    Sample(sb, "airscape_temperature_fahrenheit", [("sensor", sensor)], v);
        }

        Family(sb, "airscape_fan_speed", "Fan speed as reported by the controller. Reads 0 while the damper doors move.");
        Sample(sb, "airscape_fan_speed", null, snapshot.Speed);

        if (snapshot.PowerWatts is { } watts)
        {
            Family(sb, "airscape_fan_power_watts", "Fan power draw reported by the controller, in watts.");
            Sample(sb, "airscape_fan_power_watts", null, watts);
        }

        if (snapshot.Cfm is { } cfm)
        {
            Family(sb, "airscape_fan_airflow_cfm", "Airflow reported by the controller, in cubic feet per minute.");
            Sample(sb, "airscape_fan_airflow_cfm", null, cfm);
        }

        var info = new List<(string, string)>();
        if (snapshot.Model is { } model) info.Add(("model", model));
        if (snapshot.SoftwareVersion is { } version) info.Add(("software_version", version));
        Family(sb, "airscape_info", "Controller model and firmware version. Always 1.");
        Sample(sb, "airscape_info", info, 1);

        return sb.ToString();
    }

    private static void Family(StringBuilder sb, string name, string help) =>
        sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n')
          .Append("# TYPE ").Append(name).Append(" gauge\n");

    private static void Sample(StringBuilder sb, string name, IReadOnlyList<(string Key, string Value)>? labels, long value)
    {
        sb.Append(name);
        if (labels is { Count: > 0 })
        {
            sb.Append('{');
            for (var i = 0; i < labels.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(labels[i].Key).Append("=\"").Append(EscapeLabelValue(labels[i].Value)).Append('"');
            }
            sb.Append('}');
        }
        sb.Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }

    internal static string EscapeLabelValue(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}
