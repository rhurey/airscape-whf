using System.Globalization;

namespace AirscapeWhf.Fan;

/// <summary>
/// Reads the response <c>fanspd.cgi</c> returns, which is not JSON.
/// <para>
/// Only <c>status.json.cgi</c> serves JSON. A command endpoint answers with a stream of
/// label-and-tag pairs that looks like XML but isn't — the labels sit outside the tags
/// (<c>inside: &lt;house_temp&gt;74&lt;/house_temp&gt;</c>), the spacing is inconsistent,
/// and <c>server_response</c> carries the same raw binary buffer that breaks the JSON
/// document. Handing it to an XML parser fails.
/// </para>
/// <para>
/// So this doesn't parse the document; it looks up the handful of tags it wants by name
/// and ignores everything else. The binary field is never read, which makes its contents
/// irrelevant.
/// </para>
/// <para>
/// Note the tag names are the <i>older</i> documented ones — <c>house_temp</c>,
/// <c>attic_temp</c>, <c>oa_temp</c> — even on firmware whose JSON uses
/// <c>inside</c>, <c>attic</c>, and <c>oa</c>. The two endpoints on the same controller
/// disagree about what the fields are called.
/// </para>
/// </summary>
internal static class TagStreamParser
{
    /// <summary>Returns null when this doesn't look like a controller response at all.</summary>
    public static FanRawStatus? Parse(string body)
    {
        // Without a speed there is nothing worth acting on, and its absence is the
        // clearest signal that we're looking at something else entirely.
        if (Number(body, "fanspd") is not { } speed) return null;

        return new FanRawStatus
        {
            FanSpeed = speed,
            DoorInProcess = Number(body, "doorinprocess"),
            TimerMinutes = Number(body, "timeremaining"),
            Cfm = Number(body, "cfm"),
            PowerWatts = Number(body, "power"),
            HouseTempF = Number(body, "house_temp"),
            AtticTempF = Number(body, "attic_temp"),
            OutsideTempF = Number(body, "oa_temp"),
            Interlock1 = Number(body, "interlock1"),
            Interlock2 = Number(body, "interlock2"),
            Setpoint = Number(body, "Setpoint"),
            Model = Text(body, "model"),
            SoftwareVersion = Text(body, "softver"),
            IpAddress = Text(body, "ipaddr"),
            MacAddress = Text(body, "macaddr"),
            Dns = Text(body, "DNS1"),
            DipSwitches = Text(body, "DIPS"),
            RemoteSwitch = Text(body, "switch2"),

            // server_response is deliberately never read.
        };
    }

    private static string? Text(string body, string tag)
    {
        var open = body.IndexOf($"<{tag}>", StringComparison.Ordinal);
        if (open < 0) return null;

        var valueStart = open + tag.Length + 2;
        var close = body.IndexOf($"</{tag}>", valueStart, StringComparison.Ordinal);
        if (close < 0) return null;

        var value = body[valueStart..close].Trim();
        return value.Length == 0 ? null : value;
    }

    private static int? Number(string body, string tag)
    {
        if (Text(body, tag) is not { } text) return null;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
