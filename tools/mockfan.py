#!/usr/bin/env python3
"""
A stand-in for an AirScape Gen 2 controller, for developing against without
touching the real fan.

It deliberately reproduces the controller's awkward parts:

  * only two concurrent connections, with excess ones dropped rather than
    refused (so a misbehaving client stalls on SYN retransmit, exactly as it
    does against the real device)
  * relative-only speed commands
  * damper doors that take ~30 seconds to move, during which the reported speed
    is 0 and speed up/down commands are not honoured (--door-commands accept
    models firmware that queues them instead)
  * numbers emitted as JSON strings, which some firmware revisions do
  * a `server_response` field carrying the exact 20-byte binary buffer captured
    from real 2.15.1 hardware, which makes the document invalid JSON
  * no Content-Length, so responses are delimited by the connection close
  * JSON mislabelled as text/html

Run it, then point the service at it:

    python3 tools/mockfan.py --port 8081
    Fan__Host=localhost:8081 dotnet run --project src/AirscapeWhf

Use --sockets 2 (the default) to prove the single-flight client works; raise it
to see the difference, or set --sockets 1 to be really unkind.
"""

import argparse
import json
import random
import socket
import threading
import time
from http.server import BaseHTTPRequestHandler, HTTPServer
from socketserver import ThreadingMixIn
from urllib.parse import urlparse, parse_qs

MAX_SPEED = 5
CFM_BY_SPEED = [0, 500, 1000, 1550, 2050, 2500]
WATTS_BY_SPEED = [0, 21, 48, 88, 130, 180]


class Fan:
    def __init__(self, stringify: bool, clean_json: bool, random_junk: bool = False,
                 door_seconds: float = 30.0, door_commands: str = "ignore"):
        self.lock = threading.Lock()
        self.door_seconds = door_seconds
        self.door_commands = door_commands
        self.speed = 0
        self.timer_minutes = 0
        self.door_until = 0.0
        self.stringify = stringify
        self.clean_json = clean_json
        self.random_junk = random_junk
        self.house = 69
        self.outside = 59
        self.attic = 65
        self._start_drift()

    def _start_drift(self):
        def drift():
            while True:
                time.sleep(20)
                with self.lock:
                    # The house cools toward outside while the fan runs.
                    if self.speed > 0 and self.house > self.outside:
                        if random.random() < 0.35 * self.speed / MAX_SPEED:
                            self.house -= 1
                    self.outside += random.choice([-1, 0, 0, 1])
                    self.attic = max(self.house, self.attic + random.choice([-1, 0, 1]))
                    if self.timer_minutes > 0:
                        self.timer_minutes = max(0, self.timer_minutes - 1)
                        if self.timer_minutes == 0:
                            self.speed = 0
        threading.Thread(target=drift, daemon=True).start()

    def doors_moving(self) -> bool:
        return time.time() < self.door_until

    def command(self, direction: int):
        with self.lock:
            before = self.speed
            if direction in (1, 3) and self.doors_moving() and self.door_commands == "ignore":
                print(f"  fan  IGNORED dir={direction}: damper doors are moving")
                return
            if direction == 1:
                self.speed = min(MAX_SPEED, self.speed + 1)
            elif direction == 2:
                self.timer_minutes = min(720, self.timer_minutes + 60)
                if self.speed == 0:
                    self.speed = 1
            elif direction == 3:
                self.speed = max(0, self.speed - 1)
            elif direction == 4:
                self.speed = 0
                self.timer_minutes = 0
            # Doors move whenever we cross the on/off boundary.
            if (before == 0) != (self.speed == 0):
                self.door_until = time.time() + self.door_seconds
            print(f"  fan  dir={direction}: speed {before} -> {self.speed}"
                  f"{'  (doors moving)' if self.doors_moving() else ''}")

    def status(self) -> dict:
        with self.lock:
            moving = self.doors_moving()
            return {
                # The real controller reports 0 while the doors travel, whatever
                # speed it is heading for.
                "fanspd": 0 if moving else self.speed,
                "doorinprocess": 1 if moving else 0,
                "timeremaining": self.timer_minutes,
                "macaddr": "60:CB:FB:00:13:7D",
                "ipaddr": "192.168.36.193",
                "model": "3.5eWHF",
                "softver": "2.15.1",
                "interlock1": 0,
                "interlock2": 0,
                "cfm": CFM_BY_SPEED[self.speed],
                "power": WATTS_BY_SPEED[self.speed],
                # 2.15.1 spellings, as observed on real hardware.
                "inside": self.house,
                "attic": self.attic,
                "oa": self.outside,
                "dip_switches": "11100",
                "remote_switch": "1111",
                "setpoint": 0,
                "dns": "192.168.36.1",
            }

    # fanspd.cgi answers with this, NOT with JSON. Labels sit outside the tags,
    # the spacing is inconsistent, and the tag names are the older documented ones
    # (house_temp/attic_temp/oa_temp) even though this firmware's JSON uses
    # inside/attic/oa. Reproduced exactly, because assuming both endpoints agreed
    # is what broke the client.
    TAG_LAYOUT = [
        ("fanspd", "fanspd", "fanspd"),
        ("doorinprocess", "doorinprocess", "doorinprocess"),
        ("timeremaining", "timeremaining", "timeremaining"),
        ("macaddr", "macaddr", "macaddr"),
        ("ipaddr", "ipaddr", "ipaddr"),
        ("model", "model", "model"),
        ("softver", "softver", "softver: "),
        ("interlock1", "interlock1", "interlock1: "),
        ("interlock2", "interlock2", "interlock2: "),
        ("cfm", "cfm", "cfm: "),
        ("power", "power", "power: "),
        ("inside", "house_temp", "inside: "),
        (None, "DNS1", ""),
        ("attic", "attic_temp", "attic: "),
        ("oa", "oa_temp", "OA: "),
        (None, "server_response", "server response: "),
        ("dip_switches", "DIPS", "DIP Switches: "),
        ("remote_switch", "switch2", "Remote Switch: "),
        ("setpoint", "Setpoint", "Setpoint:"),
    ]

    def tag_stream_bytes(self) -> bytes:
        doc = self.status()
        lines = []
        for key, tag, label in self.TAG_LAYOUT:
            if tag == "DNS1":
                value = doc["dns"].encode()
            elif tag == "server_response":
                value = b"OK" if self.clean_json else self._junk()
            else:
                value = str(doc[key]).encode()
            lines.append(label.encode() + b"<" + tag.encode() + b">"
                         + value + b"</" + tag.encode() + b">")
        return b"\n".join(lines)

    def status_bytes(self) -> bytes:
        """
        Serialises the document the way the controller does.

        Real 2.15.1 firmware emits a `server_response` field containing a raw binary
        buffer — control bytes, newlines, invalid UTF-8 — none of which is legal inside
        a JSON string. The document it produces genuinely cannot be parsed. That is
        reproduced here by default, because a mock that only emits valid JSON would
        never have caught it.
        """
        doc = self.status()
        if self.stringify:
            doc = {k: (str(v) if isinstance(v, int) else v) for k, v in doc.items()}

        if self.clean_json:
            return json.dumps(doc, indent=4).encode()

        # Build by hand so we can inject bytes json.dumps would refuse to write.
        # Real firmware is inconsistent about the space after the colon; these three
        # fields have none. Reproduced so the parser is exercised against it.
        tight = {"interlock1", "inside", "oa"}

        parts = []
        for key, value in doc.items():
            sep = b'":' if key in tight else b'": '
            parts.append(b'    "' + key.encode() + sep + json.dumps(value).encode())
            # The junk lands in the same position the real firmware puts it.
            if key == "oa":
                parts.append(b'    "server_response": "' + self._junk() + b'"')

        return b"{\n" + b",\n".join(parts) + b"\n    }\n\n\n"

    # Captured verbatim from a real 3.5eWHF on firmware 2.15.1. Identical across
    # repeated reads, so this is a constant the firmware emits rather than
    # uninitialised memory. Contains four raw control bytes (FF, SOH, TAB, BS) and
    # nine bytes that are not valid UTF-8 — but notably no quote and no comma.
    REAL_SERVER_RESPONSE = bytes([
        0x5D, 0x0C, 0x85, 0xB4, 0xAA, 0x24, 0x35, 0x01, 0xC9, 0x9C,
        0xC6, 0xF4, 0x44, 0xEA, 0xF0, 0x09, 0xBC, 0x08, 0x5E, 0x86,
    ])

    def _junk(self) -> bytes:
        if self.random_junk:
            return bytes(random.randrange(256) for _ in range(random.randrange(10, 24)))
        return self.REAL_SERVER_RESPONSE


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    fan: Fan = None  # type: ignore[assignment]
    latency: float = 0.0

    def log_message(self, fmt, *args):
        print(f"  fan  {self.address_string()}  {fmt % args}")

    def do_GET(self):
        if self.latency:
            time.sleep(self.latency)

        parsed = urlparse(self.path)
        path = parsed.path.lstrip("/")

        if path == "status.json.cgi":
            return self._body(self.fan.status_bytes())

        if path == "fanspd.cgi":
            dirs = parse_qs(parsed.query).get("dir", [])
            if not dirs:
                return self._body(self.fan.tag_stream_bytes())
            if dirs[0] not in {"1", "2", "3", "4"}:
                return self._not_found()
            self.fan.command(int(dirs[0]))
            # The command endpoint answers with the tag stream, not JSON.
            return self._body(self.fan.tag_stream_bytes())

        return self._not_found()

    def _body(self, body: bytes):
        # The real controller sends exactly these four headers. There is no
        # Content-Length and no chunked encoding, so the body is delimited by the
        # connection close -- clients must read to EOF. It also mislabels JSON as
        # text/html. Both are reproduced so the client is exercised against them.
        self.send_response_only(200)
        self.send_header("Connection", "close")
        self.send_header("Content-Type", "text/html")
        self.send_header("Cache-Control", "no-cache")
        self.end_headers()
        self.wfile.write(body)
        self.close_connection = True

    def _not_found(self):
        body = (b'404: File not found<br>Use <a href="/mpfsupload">MPFS Upload</a>'
                b' to program web pages')
        self.send_response(404)
        self.send_header("Content-Type", "text/html")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


class CappedServer(ThreadingMixIn, HTTPServer):
    """An HTTP server with the controller's tiny connection budget."""
    daemon_threads = True
    allow_reuse_address = True
    max_sockets = 2

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self._live = 0
        self._live_lock = threading.Lock()
        self.dropped = 0

    def process_request(self, request, client_address):
        with self._live_lock:
            if self._live >= self.max_sockets:
                self.dropped += 1
                print(f"  fan  DROPPING connection from {client_address[0]} "
                      f"({self._live} already open, cap {self.max_sockets}) "
                      f"— total dropped: {self.dropped}")
                # Drop silently, like a stack with no listen backlog. The client
                # will sit on its SYN retransmit timer.
                try:
                    request.close()
                except OSError:
                    pass
                return
            self._live += 1
        super().process_request(request, client_address)

    def shutdown_request(self, request):
        with self._live_lock:
            self._live = max(0, self._live - 1)
        super().shutdown_request(request)


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--port", type=int, default=8081)
    ap.add_argument("--sockets", type=int, default=2,
                    help="concurrent connection cap (default: 2, like the real thing)")
    ap.add_argument("--latency", type=float, default=0.15,
                    help="seconds of delay per request")
    ap.add_argument("--stringify", action="store_true",
                    help="emit every number as a JSON string, as some firmware does")
    ap.add_argument("--random-junk", action="store_true",
                    help="vary the server_response bytes each read instead of using "
                         "the constant captured from real hardware")
    ap.add_argument("--door-seconds", type=float, default=30.0,
                    help="how long the damper doors take to move (default: 30)")
    ap.add_argument("--door-commands", choices=["ignore", "accept"], default="ignore",
                    help="what speed commands do during door travel: 'ignore' (default, "
                         "like the real controller) or 'accept'")
    ap.add_argument("--clean-json", action="store_true",
                    help="omit the malformed server_response field that real 2.15.1 "
                         "firmware emits (default: reproduce it)")
    args = ap.parse_args()

    Handler.fan = Fan(stringify=args.stringify, clean_json=args.clean_json,
                      random_junk=args.random_junk, door_seconds=args.door_seconds,
                      door_commands=args.door_commands)
    Handler.latency = args.latency
    CappedServer.max_sockets = args.sockets

    server = CappedServer(("0.0.0.0", args.port), Handler)
    print(f"mock 3.5eWHF on http://localhost:{args.port}  "
          f"(connection cap: {args.sockets}, latency: {args.latency}s, "
          f"doors: {args.door_seconds:g}s/{args.door_commands}, "
          f"json: {'valid' if args.clean_json else 'malformed, like the real thing'})")
    print(f"  try:  curl http://localhost:{args.port}/status.json.cgi")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nstopped")


if __name__ == "__main__":
    main()
