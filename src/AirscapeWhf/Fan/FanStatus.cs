using System.Text.Json;
using System.Text.Json.Serialization;

namespace AirscapeWhf.Fan;

/// <summary>Configuration for the single fan this instance fronts.</summary>
public sealed class FanOptions
{
    public const string Section = "Fan";

    /// <summary>IP or hostname of the AirScape controller. Required.</summary>
    public string Host { get; set; } = "";

    /// <summary>How often to poll the controller for status.</summary>
    public int PollIntervalSeconds { get; set; } = 20;

    /// <summary>Per-request timeout. The controller is slow but not <i>that</i> slow.</summary>
    public int TimeoutSeconds { get; set; } = 6;

    /// <summary>
    /// Highest speed the controller accepts. Varies by model (3.5e is 5; larger units
    /// go higher). If we ever observe a speed above this we raise it automatically.
    /// </summary>
    public int MaxSpeed { get; set; } = 5;

    /// <summary>
    /// Delay between the discrete step commands that make up a speed change, once the
    /// damper doors are settled.
    /// </summary>
    public int StepDelayMilliseconds { get; set; } = 350;

    /// <summary>
    /// How often to re-read status while the damper doors are moving. The controller
    /// does not honour speed commands during door travel, so the stepper waits on this
    /// rather than on a fixed delay. Floored at 500 ms.
    /// </summary>
    public int DoorPollMilliseconds { get; set; } = 2000;

    /// <summary>Give up waiting for the doors after this long. They normally take ~30 s.</summary>
    public int DoorTimeoutSeconds { get; set; } = 90;

    /// <summary>Consecutive poll failures before the UI is told the fan is unreachable.</summary>
    public int FailuresBeforeOffline { get; set; } = 3;
}

/// <summary>
/// The controller's status document, as it comes off the wire.
/// <para>
/// Field names and value types vary across Gen 2 firmware revisions: some builds emit
/// every number as a JSON string, and the temperature keys differ. The names declared
/// here are the ones observed on real 2.15.1 hardware (<c>inside</c>, <c>attic</c>,
/// <c>oa</c>); older documented spellings are handled as aliases in <see cref="Resolve"/>.
/// Everything is nullable, numbers are parsed leniently, and unrecognised keys are
/// captured in <see cref="Extra"/>.
/// </para>
/// </summary>
public sealed record FanRawStatus
{
    [JsonPropertyName("fanspd")] public int? FanSpeed { get; init; }
    [JsonPropertyName("doorinprocess")] public int? DoorInProcess { get; init; }
    [JsonPropertyName("timeremaining")] public int? TimerMinutes { get; init; }
    [JsonPropertyName("cfm")] public int? Cfm { get; init; }
    [JsonPropertyName("power")] public int? PowerWatts { get; init; }

    // 2.15.1 spellings. Older docs describe house_temp / attic_temp / oa_temp;
    // Resolve() picks those up if this firmware uses them instead.
    [JsonPropertyName("inside")] public int? HouseTempF { get; init; }
    [JsonPropertyName("attic")] public int? AtticTempF { get; init; }
    [JsonPropertyName("oa")] public int? OutsideTempF { get; init; }

    [JsonPropertyName("interlock1")] public int? Interlock1 { get; init; }
    [JsonPropertyName("interlock2")] public int? Interlock2 { get; init; }
    [JsonPropertyName("setpoint")] public int? Setpoint { get; init; }
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("softver")] public string? SoftwareVersion { get; init; }
    [JsonPropertyName("ipaddr")] public string? IpAddress { get; init; }
    [JsonPropertyName("macaddr")] public string? MacAddress { get; init; }
    [JsonPropertyName("dns")] public string? Dns { get; init; }

    /// <summary>
    /// Board configuration switches, as a bit string. The controller reports these but
    /// AirScape does not publish what each position means, so they are surfaced verbatim
    /// for diagnostics rather than interpreted.
    /// </summary>
    [JsonPropertyName("dip_switches")] public string? DipSwitches { get; init; }

    /// <summary>State of the hardwired wall control, as a bit string. Also undocumented.</summary>
    [JsonPropertyName("remote_switch")] public string? RemoteSwitch { get; init; }

    // Deliberately not modelled: server_response. On 2.15.1 it carries a raw binary
    // buffer that is not valid inside a JSON string at all — see JsonSalvage, which
    // discards it before parsing.

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    /// <summary>
    /// Fills in any field that came back null by checking known alternate spellings
    /// seen on other firmware revisions.
    /// </summary>
    public FanRawStatus Resolve() => this with
    {
        HouseTempF = HouseTempF ?? AltInt("house_temp", "house", "room_temp", "insidetemp"),
        AtticTempF = AtticTempF ?? AltInt("attic_temp", "attictemp"),
        OutsideTempF = OutsideTempF ?? AltInt("oa_temp", "outside", "outsidetemp", "oat"),
        TimerMinutes = TimerMinutes ?? AltInt("timeremaining_min", "timer"),
        FanSpeed = FanSpeed ?? AltInt("fanspeed", "speed"),
        Dns = Dns ?? AltString("DNS1", "dns1", "prim_dns"),
    };

    private int? AltInt(params string[] keys)
    {
        foreach (var element in Lookup(keys))
            if (LenientInt32Converter.TryRead(element, out var parsed))
                return parsed;
        return null;
    }

    private string? AltString(params string[] keys)
    {
        foreach (var element in Lookup(keys))
            if (element.ValueKind == JsonValueKind.String)
                return element.GetString();
        return null;
    }

    private IEnumerable<JsonElement> Lookup(string[] keys)
    {
        if (Extra is null) yield break;
        foreach (var key in keys)
            foreach (var (k, v) in Extra)
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                    yield return v;
    }
}

/// <summary>
/// Reads an int from either a JSON number or a JSON string, because Gen 2 firmware
/// does both depending on the revision. Non-numeric junk reads as null rather than
/// throwing — one weird field should not cost us the whole status document.
/// </summary>
public sealed class LenientInt32Converter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number:
                if (reader.TryGetInt32(out var i)) return i;
                if (reader.TryGetDouble(out var d)) return (int)Math.Round(d);
                return null;
            case JsonTokenType.String:
                return FromString(reader.GetString());
            case JsonTokenType.True:
                return 1;
            case JsonTokenType.False:
                return 0;
            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteNumberValue(value.Value);
    }

    internal static bool TryRead(JsonElement element, out int? value)
    {
        value = element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out var i) ? i
                                  : element.TryGetDouble(out var d) ? (int)Math.Round(d)
                                  : null,
            JsonValueKind.String => FromString(element.GetString()),
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => null,
        };
        return value is not null;
    }

    private static int? FromString(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        if (int.TryParse(trimmed, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var i))
            return i;
        if (double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
            return (int)Math.Round(d);
        return null;
    }
}
