#!/usr/bin/env python3
"""
Reference implementation and test suite for the repair FanClient applies to
controller status documents.

Firmware 2.15.1 emits a `server_response` field carrying a 20-byte binary buffer —
raw control bytes and invalid UTF-8, none of it legal inside a JSON string. The
buffer is constant across reads, the firmware is frozen, and the field order is
fixed, so there is exactly one defect and it does not move.

The repair does not parse or rebuild the document. It finds the bad field, finds
where the *next* field starts, and deletes everything between them. Anchoring on the
following field rather than on the bad field's closing quote is what makes the junk's
length and content irrelevant: it doesn't matter what is in there, only that a real
field follows it.

Mirrors src/AirscapeWhf/Fan/JsonRepair.cs line for line, so the algorithm can be
exercised without a .NET SDK.

    python3 tools/salvage_check.py                                  # run the suite
    curl -s http://<fan>/status.json.cgi | python3 tools/salvage_check.py -

The second form is the useful one against real hardware.
"""

import json
import sys

BAD_FIELD = '"server_response"'
MAX_KEY = 64


# --- algorithm (mirrors JsonRepair.cs) -------------------------------------

def preceded_by_comma(body, index):
    for i in range(index - 1, -1, -1):
        if body[i].isspace():
            continue
        return body[i] == ","
    return False


def find_next_field_start(body, start):
    """Index of the next `"key":` at or after start, or -1."""
    for i in range(start, len(body)):
        if body[i] != '"' or not preceded_by_comma(body, i):
            continue
        key_end = -1
        for j in range(i + 1, min(len(body), i + 1 + MAX_KEY)):
            c = body[j]
            if c == '"':
                key_end = j
                break
            if not (c.isascii() and (c.isalnum() or c in "_-.")):
                break
        if key_end <= i + 1:
            continue
        k = key_end + 1
        while k < len(body) and body[k].isspace():
            k += 1
        if k < len(body) and body[k] == ":":
            return i
    return -1


def try_excise(body):
    """Removes server_response and its value. Returns the repaired text, or None."""
    start = body.find(BAD_FIELD)
    if start < 0:
        return None
    nxt = find_next_field_start(body, start + len(BAD_FIELD))
    if nxt < 0:
        # No field follows, so the preceding comma would need removing too.
        # Failing the read beats guessing; the poller serves last-known state.
        return None
    return body[:start] + body[nxt:]


def parse_status(raw_bytes):
    """The whole pipeline: decode leniently, try strict, excise, try again."""
    body = raw_bytes.decode("utf-8", errors="replace")
    open_i, close_i = body.find("{"), body.rfind("}")
    if open_i < 0 or close_i <= open_i:
        return None, "no JSON object present"
    trimmed = body[open_i:close_i + 1]

    try:
        return json.loads(trimmed), None
    except json.JSONDecodeError as e:
        first_error = f"{e.msg} at line {e.lineno}, col {e.colno}"

    repaired = try_excise(trimmed)
    if repaired is None:
        return None, f"{first_error}; nothing to excise"
    try:
        return json.loads(repaired), first_error
    except json.JSONDecodeError as e:
        return None, f"{first_error}; still bad after excision: {e.msg}"


# --- inspecting a real payload ---------------------------------------------

def inspect(raw_bytes):
    doc, note = parse_status(raw_bytes)
    if doc is None:
        print(f"Could not parse this document: {note}")
        return 1

    if note is None:
        print("Already valid JSON; no repair needed.\n")
    else:
        print(f"Strict parser rejected it: {note}")
        print("Excised server_response and reparsed.\n")

    print(f"Recovered {len(doc)} field(s):")
    width = max(len(k) for k in doc)
    for k, v in doc.items():
        print(f"  {k.ljust(width)}  {v!r}")
    return 0


# --- test suite ------------------------------------------------------------

def build_payload(junk_bytes):
    """The exact document shape observed on 2.15.1, including its inconsistent
    spacing after colons."""
    return (
        b'{\n'
        b'    "fanspd": 0,\n'
        b'    "doorinprocess": 0,\n'
        b'    "timeremaining": 0,\n'
        b'    "macaddr": "60:CB:FB:00:13:7D",\n'
        b'    "ipaddr": "192.168.36.193",\n'
        b'    "model": "3.5eWHF",\n'
        b'    "softver": "2.15.1",\n'
        b'    "interlock1":0,\n'
        b'    "interlock2": 0,\n'
        b'    "cfm": 0,\n'
        b'    "power": 0,\n'
        b'    "inside":71,\n'
        b'    "attic": 74,\n'
        b'    "oa":77,\n'
        b'    "server_response": "' + junk_bytes + b'",\n'
        b'    "dip_switches": "11100",\n'
        b'    "remote_switch": "1111",\n'
        b'    "setpoint": 0,\n'
        b'    "dns": "192.168.36.1"\n'
        b'    }\n\n\n'
    )


EXPECTED = {
    "fanspd": 0, "doorinprocess": 0, "timeremaining": 0,
    "macaddr": "60:CB:FB:00:13:7D", "ipaddr": "192.168.36.193",
    "model": "3.5eWHF", "softver": "2.15.1",
    "interlock1": 0, "interlock2": 0, "cfm": 0, "power": 0,
    "inside": 71, "attic": 74, "oa": 77,
    "dip_switches": "11100", "remote_switch": "1111",
    "setpoint": 0, "dns": "192.168.36.1",
}

# Captured verbatim from a 3.5eWHF on 2.15.1, byte-identical across repeated reads.
# Four raw control bytes (FF, SOH, TAB, BS) and nine that aren't valid UTF-8 --
# and notably neither a quote nor a comma.
REAL_JUNK = bytes([
    0x5D, 0x0C, 0x85, 0xB4, 0xAA, 0x24, 0x35, 0x01, 0xC9, 0x9C,
    0xC6, 0xF4, 0x44, 0xEA, 0xF0, 0x09, 0xBC, 0x08, 0x5E, 0x86,
])

_failures = 0


def check(name, ok, detail=""):
    global _failures
    if not ok:
        _failures += 1
    print(f"  {'PASS' if ok else 'FAIL'}  {name}{'' if ok else '  <- ' + detail}")


def case(name, junk, *, should_parse=True, keeps_server_response=False):
    print(f"\n[{name}]")
    doc, note = parse_status(build_payload(junk))

    if not should_parse:
        check("fails cleanly rather than returning garbage", doc is None, f"got {doc}")
        if doc is None:
            print(f"        {note}")
        return

    check("parsed", doc is not None, note or "")
    if doc is None:
        return

    label = "clean server_response kept" if keeps_server_response else "server_response excised"
    check(label, ("server_response" in doc) == keeps_server_response)
    survived = {k: v for k, v in doc.items() if k in EXPECTED}
    check(f"all {len(EXPECTED)} real fields intact", survived == EXPECTED,
          f"diff={ {k: (EXPECTED[k], doc.get(k)) for k in EXPECTED if EXPECTED[k] != doc.get(k)} }")


# --- tag stream (what fanspd.cgi actually returns) -------------------------

def tag_text(body, tag):
    open_at = body.find(f"<{tag}>")
    if open_at < 0:
        return None
    start = open_at + len(tag) + 2
    close = body.find(f"</{tag}>", start)
    if close < 0:
        return None
    value = body[start:close].strip()
    return value or None


def tag_number(body, tag):
    text = tag_text(body, tag)
    try:
        return int(text)
    except (TypeError, ValueError):
        return None


# Captured verbatim from the fan. Note the labels outside the tags, the
# inconsistent spacing, and the OLD field names -- house_temp, not inside.
REAL_TAG_STREAM = (
    b"fanspd<fanspd>0</fanspd>\n"
    b"doorinprocess<doorinprocess>1</doorinprocess>\n"
    b"timeremaining<timeremaining>0</timeremaining>\n"
    b"macaddr<macaddr>60:CB:FB:00:13:7D</macaddr>\n"
    b"ipaddr<ipaddr>192.168.36.193</ipaddr>\n"
    b"model<model>3.5eWHF</model>\n"
    b"softver: <softver>2.15.1</softver>\n"
    b"interlock1: <interlock1>0</interlock1>\n"
    b"interlock2: <interlock2>0</interlock2>\n"
    b"cfm: <cfm>0</cfm>\n"
    b"power: <power>0</power>\n"
    b"inside: <house_temp>74</house_temp>\n"
    b"<DNS1>192.168.36.1</DNS1>\n"
    b"attic: <attic_temp>63</attic_temp>\n"
    b"OA: <oa_temp>64</oa_temp>\n"
    b"server response: <server_response>" + REAL_JUNK + b"</server_response>\n"
    b"DIP Switches: <DIPS>11100</DIPS>\n"
    b"Remote Switch: <switch2>1111</switch2>\n"
    b"Setpoint:<Setpoint>0</Setpoint>"
)


def tag_suite():
    print("\n[fanspd.cgi tag stream, verbatim from hardware]")
    body = REAL_TAG_STREAM.decode("utf-8", errors="replace")

    check("not JSON, as the old code assumed", body.lstrip()[:1] != "{")
    check("speed", tag_number(body, "fanspd") == 0)
    check("door state", tag_number(body, "doorinprocess") == 1)
    check("inside temp via the OLD tag name", tag_number(body, "house_temp") == 74)
    check("attic temp", tag_number(body, "attic_temp") == 63)
    check("outside temp", tag_number(body, "oa_temp") == 64)
    check("labels outside the tags don't leak in", tag_text(body, "softver") == "2.15.1")
    check("mac", tag_text(body, "macaddr") == "60:CB:FB:00:13:7D")
    check("dip switches", tag_text(body, "DIPS") == "11100")
    check("remote switch", tag_text(body, "switch2") == "1111")
    check("setpoint, the last tag with no trailing newline",
          tag_number(body, "Setpoint") == 0)
    check("binary field is simply never read", tag_text(body, "fanspd") == "0")
    check("a tag that isn't there reads as None", tag_text(body, "nonesuch") is None)


def run_suite():
    tag_suite()
    case("verbatim capture from real hardware", REAL_JUNK)

    # Junk containing bytes that break naive quote-matching repairs.
    case("junk containing a quote", b'\x00\x01"\x02\x03\xff\xfe')
    case("junk containing a quote and a comma", b'\x00\x01" , "\x02\x03\xff\xfe')
    case("junk containing braces", b'{\x00}\x01\xff}')
    case("junk that is only control bytes", bytes(range(0, 32)))

    # KNOWN LIMITATION. Junk that imitates a complete field creates a false anchor,
    # so the excision stops early and the document is still malformed. Accepted: the
    # buffer on this firmware is constant and contains no comma, and the failure is a
    # clean parse error rather than corrupted data. Rebuilding the whole document
    # survived this case; a single excision cannot.
    case("junk imitating a field (known limitation)",
         b'\x00,"fake_key": 999,\x01\xff', should_parse=False)

    # Well-behaved firmware takes the fast path and is never touched.
    case("no junk at all", b'ok', keeps_server_response=True)

    print()
    if _failures:
        print(f"{_failures} check(s) failed.\n")
        return 1
    print("All checks passed.\n")
    return 0


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "-":
        sys.exit(inspect(sys.stdin.buffer.read()))
    sys.exit(run_suite())
