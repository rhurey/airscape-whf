namespace AirscapeWhf.Fan;

/// <summary>
/// Cuts the one field that makes the controller's status document unparseable.
/// <para>
/// Firmware 2.15.1 emits <c>server_response</c> carrying a 20-byte binary buffer —
/// raw control bytes and invalid UTF-8, none of it legal inside a JSON string. The
/// buffer is constant across reads, the firmware is frozen (Gen 2 controls are
/// discontinued), and the field order is fixed, so there is exactly one defect to
/// handle and it will not move.
/// </para>
/// <para>
/// This does not try to parse or rebuild the document. It finds the bad field, finds
/// where the <i>next</i> field starts, and deletes everything between them — leaving
/// the rest of the bytes untouched for a normal parser. Anchoring on the following
/// field rather than on the bad field's closing quote is what makes the junk's length
/// and content irrelevant: it doesn't matter what is in there, only that a real field
/// follows it.
/// </para>
/// </summary>
internal static class JsonRepair
{
    /// <summary>The only field known to carry unparseable content on any Gen 2 firmware.</summary>
    private const string BadField = "\"server_response\"";

    private const int MaxKeyLength = 64;

    /// <summary>
    /// Removes <c>server_response</c> and its value. Returns false when the field isn't
    /// present, or when no field follows it — in that second case the document would
    /// need its preceding comma removed too, and guessing is worse than failing the
    /// read, which the poller already handles by serving last-known state.
    /// </summary>
    public static bool TryExcise(string body, out string repaired)
    {
        repaired = body;

        var start = body.IndexOf(BadField, StringComparison.Ordinal);
        if (start < 0) return false;

        var next = FindNextFieldStart(body, start + BadField.Length);
        if (next < 0) return false;

        // Cutting [start, next) takes this field's own trailing comma with it and
        // leaves the previous field's comma in place, which is exactly what the
        // document needs to stay well-formed.
        repaired = body.Remove(start, next - start);
        return true;
    }

    /// <summary>
    /// Finds the next <c>"key":</c> at or after <paramref name="from"/>. Requires the
    /// quote to be preceded by a comma so that a stray quote inside binary junk cannot
    /// masquerade as the start of a field.
    /// </summary>
    private static int FindNextFieldStart(string body, int from)
    {
        for (var i = from; i < body.Length; i++)
        {
            if (body[i] != '"' || !PrecededByComma(body, i)) continue;

            var keyEnd = -1;
            for (var j = i + 1; j < body.Length && j - i <= MaxKeyLength; j++)
            {
                var c = body[j];
                if (c == '"') { keyEnd = j; break; }
                if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.')) break;
            }
            if (keyEnd <= i + 1) continue;

            var k = keyEnd + 1;
            while (k < body.Length && char.IsWhiteSpace(body[k])) k++;
            if (k < body.Length && body[k] == ':') return i;
        }

        return -1;
    }

    private static bool PrecededByComma(string body, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(body[i])) continue;
            return body[i] == ',';
        }
        return false;
    }
}
