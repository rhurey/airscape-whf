# airscape-whf

A replacement web UI for AirScape whole house fans with 2nd-generation controls,
in a container.

The fan's own web server works, but unreliably: pages take a minute, or render,
or time out. The cause isn't the fan. The Microchip TCP/IP stack it runs is
compiled with a fixed, very small number of concurrent HTTP connections —
typically two — and it has no listen backlog, so excess connections are dropped
rather than refused. A browser opening its normal six parallel connections to
fetch a page therefore stalls on SYN retransmit timers.

It's worse than a straight concurrency limit, because the controller answers
every request with `Connection: close`. No connection is ever reusable, so every
stylesheet, script, image, and favicon on the page costs a full connect and
teardown against a two-socket pool, and each socket stays unavailable while it
drains. That is the whole mystery: a device that appears to work intermittently
for no visible reason.

This service is the fix: **one process holds the only connection to the fan,
ever.** It polls status on an interval, caches the result, and serves a modern
UI over server-sent events. Adding a browser tab costs this container a channel
and costs the fan nothing.

---

## What it looks like

A single page, dark, built for checking at 9pm on a phone. The centrepiece is
the air path — outside, house, attic — with each reading coloured by where it
sits between the coolest and warmest thing the fan can see, and flow that drifts
faster at higher speed. The headline tells you whether running the fan is
actually worth it right now, which is the question you opened the page to answer
and the one the vendor UI makes you work out yourself from a table.

Four of the vendor page's rows aren't device state at all. CFM/watt, cooling
BTUh, and EER are arithmetic on `cfm`, `power`, `house_temp`, and `oa_temp`:

    cooling BTUh = 1.08 x CFM x (house - outside)
    EER          = BTUh / watts

They're computed here so there's one source of truth.

## Running it

Give the fan a DHCP reservation, then:

```bash
git clone <this> && cd airscape-whf
mkdir -p config data
cp config.example/appsettings.json config/   # set Fan:Host to the controller's IP
openssl rand -hex 32 > config/mcp-token      # optional: enables /mcp
sudo chown 1654:1654 data                    # the container's user
docker compose up -d
```

Open <http://localhost:8080>.

### Configuration

The container needs exactly two directories mapped in, and nothing about your
house lives in the image or the compose file:

| Mount | Mode | Contents |
| --- | --- | --- |
| `/config` | read-only | `appsettings.json` (required: `Fan:Host`); `certs/fullchain.pem` + `certs/privkey.pem` (optional, enables HTTPS); `mcp-token` (optional, enables `/mcp`) |
| `/data` | read-write | `cooldown.json`, written by the app |

`config.example/appsettings.json` lists every setting with its default.
Environment variables override the file using `__` for nesting, so
`Fan__Host=192.168.1.50` works for a quick run without a config file. Starting
without a `Fan:Host` exits straight away with a message saying where to set it.

| Setting | Default | Notes |
| --- | --- | --- |
| `Fan:Host` | *(required)* | IP or hostname of the controller |
| `Fan:PollIntervalSeconds` | `20` | Each poll occupies one of the fan's two sockets |
| `Fan:TimeoutSeconds` | `6` | Per-request timeout |
| `Fan:MaxSpeed` | `5` | 3.5e tops out at 5; raises itself if a higher speed is observed |
| `Fan:StepDelayMilliseconds` | `350` | Pacing between speed steps once the doors are settled |
| `Fan:DoorPollMilliseconds` | `2000` | How often to re-read status while the damper doors move (min 500) |
| `Fan:DoorTimeoutSeconds` | `90` | Give up on a command if the doors never settle |
| `Fan:FailuresBeforeOffline` | `3` | Consecutive failures before the UI is told it's stale |
| `Cooldown:Path` | `/data/cooldown.json` | Where cooldown rules are saved |
| `Http:Port` | `8080` | Always on |
| `Https:Port` | `8443` | On only when the PEM pair exists |
| `Https:CertificatePath`, `Https:KeyPath` | `/config/certs/fullchain.pem`, `privkey.pem` | Override the PEM locations |
| `Mcp:Enabled` | `true` | Serve `/mcp` (still needs a token) |
| `Mcp:ReadOnly` | `false` | Only the status and rule-reading tools |
| `Mcp:Token` | — | Overrides `/config/mcp-token`; prefer the file |
| `AIRSCAPE_CONFIG_DIR` (env) | `/config` | Where the config directory is |

## Deployment

### TLS

The fan can never have a real certificate. This container can.

Put a PEM chain and key at `config/certs/fullchain.pem` and
`config/certs/privkey.pem` and restart. The startup log says whether HTTPS is on,
and if not, which file it looked for. Without them the container serves HTTP only
rather than refusing to start.

A wildcard from your ACME pipeline needs no conversion. `*.home.example.com`
covers `fan.home.example.com`, since a wildcard matches one label.

Two things reliably go wrong here, and `deploy/renew-hook.sh` handles both.

The file extension is irrelevant. acme.sh writes PEM whatever it names the file,
and Kestrel parses contents rather than trusting the name: `fullchain.cer` and
`fullchain.pem` are the same bytes. Check with `head -1 fullchain.cer`; if it
says `-----BEGIN CERTIFICATE-----`, you're done thinking about it.

**Permissions.** The chiseled image runs as UID 1654, which cannot read a typical
ACME output directory. Copy the pair somewhere that user owns rather than
loosening permissions on your PKI.

**Renewal.** Kestrel reads the certificate once at startup and does not watch the
file, so a renewal without a restart silently serves the old one until it expires.

`deploy/renew-hook.sh <acme-output-dir> [install-dir]` does the job for certbot,
acme.sh, or anything else. It accepts either naming convention, checks the files
really are PEM, installs them into `config/certs/` with owner 1654 and mode 0400 on
the key, and restarts the service. With acme.sh:

```bash
acme.sh --install-cert -d '*.home.example.com' --ecc \
  --reloadcmd "/opt/airscape-whf/deploy/renew-hook.sh '/root/.acme.sh/*.home.example.com_ecc'"
```

acme.sh stores wildcards under a directory with a literal asterisk, and appends
`_ecc` for elliptic-curve certs. That is a common source of "file not found".

Then point DNS at the host, with an A record for `fan.home.example.com`. The
compose file maps 8443 to 443, so <https://fan.home.example.com> works with no
browser warning.

HTTP stays open on 8080 on purpose. Forcing a redirect breaks simple `curl`
integrations on a trusted LAN and buys nothing.

### Persistence

Cooldown rules are saved to `/data/cooldown.json`, backed by a bind mount at
`./data` so you can read and back up the file directly. Create it with the right
owner before first start:

```bash
mkdir -p data && sudo chown 1654:1654 data
```

Without a writable mount the rules still work, but are lost on restart and the
log says so once.

### Health

The image defines a `HEALTHCHECK` that runs the app in `--healthcheck` mode, which
does one local GET of `/healthz`. The chiseled image has no shell or curl, so the
app checks itself. It is liveness only: the service is doing its job even when the
fan is unreachable, so fan state deliberately does not fail it.

### Starting on boot

`restart: unless-stopped` already brings the container back when Docker starts,
so enabling Docker is enough:

```bash
sudo systemctl enable docker
```

For explicit control — ordering against the network, a real `systemctl status`,
and one place to restart after a renewal — install the unit:

```bash
sudo cp -r . /opt/airscape-whf   # including your config/ and data/
sudo cp deploy/airscape-whf.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now airscape-whf
```

The root filesystem is read-only, capabilities are dropped, and privilege
escalation is disabled. `/tmp` is a tmpfs because the runtime wants scratch space
even with a read-only root.

## Cooldown

You run the fan to flush the house with cool night air. As the night goes on the
house keeps cooling, and at some point you are no longer cooling it so much as
refrigerating it. Cooldown watches the **inside (house) temperature** and eases the
fan off as it falls. The outside reading plays no part in it.

Rules are a ladder — when the house is below this temperature, run no faster than
that:

    Below 68°  ->  Speed 2
    Below 65°  ->  Off

One rung is the simple case ("stop when the house reaches 66"); more rungs give you
a gradual wind-down. Edit them in the UI; they're saved to `/data/cooldown.json`.

**One invariant governs the whole feature: it can only slow the fan down or stop
it.** It never speeds the fan up and it never starts it. That is what makes it
safe to leave switched on — a rule that could start a fan at 3am because a
temperature reading blipped is a rule nobody would trust. If you want the fan
running again, you turn it on.

**Once a step fires it latches**, and won't fire again until the house climbs back
past its threshold plus a release margin (2°F by default). Without that, sitting at
a threshold while the reading jitters by a degree would have the governor fighting
you for control of the speed. With it, if you override the speed upward after a
step has fired, your setting sticks. Steps re-arm on their own as the house warms
up, so the ladder is ready again by the next evening.

One consequence of triggering on the house temperature: if you start the fan when
the house is *already* below a step that has not fired yet, the step fires straight
away and slows or stops the fan. Start it again and it sticks, because the step is
now latched. Lower the threshold if that bites.

The governor **never acts on data it doesn't trust**: no reading, a stale
snapshot, or an unreachable fan all mean it does nothing. It reads the same
broadcast the browsers do, so it costs the controller no extra requests.

Rules that could never fire are rejected rather than silently ignored — a colder
step with a *higher* speed than a warmer one can never trigger, so the API says
so instead of accepting it.

The state machine is exercised in `tools/cooldown_check.py`, including a
simulated night as the house cools and warms again.

## API

| Method | Path | |
| --- | --- | --- |
| `GET` | `/api/status` | Current snapshot |
| `GET` | `/api/stream` | SSE, pushes on every state change |
| `POST` | `/api/speed/{0..max}` | Absolute speed |
| `POST` | `/api/nudge/{±n}` | Relative speed |
| `POST` | `/api/timer/{hours}` | Add timer hours (starts an idle fan at speed 1) |
| `POST` | `/api/off` | Off |
| `GET` | `/api/cooldown` | Current rules, plus which step fires next |
| `PUT` | `/api/cooldown` | Replace the rules |
| `GET` | `/healthz` | Liveness |
| `GET` | `/metrics` | Prometheus scrape target (see below) |
| `POST` | `/mcp` | MCP server, bearer token required (see below) |

Commands return `202 Accepted`. They are queued, not synchronous (see below). While
one is in flight the snapshot carries `targetSpeed`, the speed it is heading for,
which is the only way to tell what the fan is about to do while the damper doors
make the controller report 0. It is absent once the fan has arrived.

The REST API, `/metrics` and the UI have no auth and assume a trusted LAN. Only
`/mcp` is gated.

## MCP

The same process serves a [Model Context Protocol](https://modelcontextprotocol.io)
endpoint at `/mcp` (Streamable HTTP, stateless), so an agent can check the fan and
drive it. The tools call the same state and command queue as the REST API, so they
cost the controller nothing extra.

| Tool | Does |
| --- | --- |
| `get_fan_status` | Speed, target, doors, timer, temperatures, airflow, power, verdict |
| `get_cooldown_rules` | The rules and which step fires next |
| `set_fan_speed` | Absolute speed, 0 to max. Returns once queued, before the fan arrives |
| `turn_fan_off` | Off |
| `add_timer_hours` | 1–12 hours. Starts the fan if it's off |
| `set_cooldown_rules` | Replace the rules. Invalid ladders are rejected with the reason |

`Mcp:ReadOnly=true` serves only the two `get_` tools.

**A token is required.** Without one, `/mcp` is not served at all and the startup
log says how to create one:

```bash
openssl rand -hex 32 > config/mcp-token   # at least 32 characters
docker compose restart
```

Every request needs `Authorization: Bearer <token>`; anything else gets 401. The
token is read once at startup, so rotating it means replacing the file and
restarting. Prefer the HTTPS port when certificates are configured: a bearer
token sent over plain HTTP can be read by anyone on the LAN.

Claude Code:

```bash
claude mcp add --transport http airscape https://fan.home.example.com/mcp \
  --header "Authorization: Bearer $(cat config/mcp-token)"
```

Clients that only speak stdio can bridge with
`npx mcp-remote https://fan.home.example.com/mcp --header "Authorization: Bearer <token>"`.

Auth is an ordinary ASP.NET Core authentication scheme plus a policy named `Mcp`
(`src/AirscapeWhf/Mcp/McpSetup.cs`). Moving to OAuth2 with Entra ID later means
registering JwtBearer and pointing that policy at it. The endpoint and tools stay
as they are.

## Prometheus

`GET /metrics` serves the cached snapshot in the Prometheus text format (0.0.4).
Like the MCP tools, it reads the same state as `/api/status`, so scraping it costs
the controller nothing. It needs no token, and works whether MCP is enabled or not.

| Metric | Labels | Value |
| --- | --- | --- |
| `airscape_up` | | 1 if the reading is fresh, otherwise 0. Always present |
| `airscape_last_reading_age_seconds` | | Seconds since the controller last answered. Present once it has answered at all |
| `airscape_temperature_fahrenheit` | `sensor="outside"`, `"inside"`, `"attic"` | Whole °F, as the controller reports it |
| `airscape_fan_speed` | | Reported speed. Reads 0 while the damper doors move |
| `airscape_fan_power_watts` | | Fan power draw |
| `airscape_fan_airflow_cfm` | | Airflow |
| `airscape_info` | `model`, `software_version` | Always 1 |

A reading is fresh when the fan is online and it is no older than
`max(60, 3 × PollIntervalSeconds)` seconds (the same rule the cooldown governor
uses before acting). **When it is not fresh, only `airscape_up 0` and the age are
exported.** The service keeps the last good reading after the fan goes quiet;
exporting it would draw a flat line at the last temperature, which is wrong data
that looks right. Dropping the series lets Prometheus mark them stale, so the
graph shows a gap instead. A sensor the controller doesn't report is left out the
same way.

Temperatures stay in Fahrenheit, the controller's native unit, so `promtool check
metrics` warns that it is not a base unit. That warning is expected.

```yaml
- job_name: airscape
  scrape_interval: 30s
  static_configs:
    - targets: ['fan.home.example.com:8080']
```

Thirty seconds is enough: the service refreshes every 20 s and the values are whole
numbers. Alert on `airscape_up == 0 or absent(airscape_up)` to notice the fan
dropping off the network.

## How it works

**`FanClient`** is the only thing that opens a socket. Requests are serialised
through a semaphore, the handler is capped at `MaxConnectionsPerServer = 1`, and
every request sets `Connection: close` so nothing is left parked in the device
between polls. Belt and suspenders, deliberately: the connection cap alone won't
stop a command and a poll from interleaving in surprising ways.

**`FanPoller`** reads status on an interval and backs off exponentially (capped
at 8x) when the fan stops answering.

**`FanStateStore`** holds the last good reading and fans it out to subscribers
over bounded channels. A single dropped poll does not blank the UI — it keeps
serving the last reading with a visible age, because on a two-socket device an
occasional miss is normal and hiding that fact is the whole point.

**`FanCommandQueue`** translates absolute intent into the relative commands the
controller actually has. The API is step-based — up, down, add an hour, off —
so "go to speed 5" from speed 1 is four sequential round trips. Intents are
coalesced (only the newest destination survives) and an in-flight sequence is
abandoned when a newer one arrives, so dragging across the range doesn't queue a
dozen redundant commands. There's a hard iteration cap, because the wall switch
and the handheld remote can move the speed out from under us.

Commands return their own status document, so issuing one publishes fresh state
without spending an extra round trip.

### What the controller actually puts on the wire

Captured from a 3.5eWHF on 2.15.1:

```
HTTP/1.1 200 OK
Connection: close
Content-Type: text/html
Cache-Control: no-cache
```

Three things follow from that. There is **no `Content-Length`** and no chunked
encoding, so the body is delimited by the connection close and clients must read
to EOF. The JSON is **mislabelled `text/html`**, so the framework cannot be
trusted to pick a decoder — `FanClient` reads bytes and decodes UTF-8 explicitly
with a replacement fallback, because the body contains sequences that aren't
valid UTF-8 and a strict decoder would throw before the salvage ever ran. And
`Connection: close` confirms there's no keep-alive to exploit.

### The controller returns invalid JSON

Firmware 2.15.1 emits a `server_response` field containing a 20-byte binary
buffer:

```
5d 0c 85 b4 aa 24 35 01 c9 9c c6 f4 44 ea f0 09 bc 08 5e 86
```

Four raw control bytes (FF, SOH, TAB, BS) and nine that aren't valid UTF-8. None
of that is legal inside a JSON string, so **the document the fan returns
genuinely cannot be parsed**. No serializer option fixes this; the input is
malformed.

The buffer is *constant* — byte-identical across repeated reads — so it's
something the firmware stores rather than uninitialised memory. It happens to
contain neither a quote nor a comma, which is the easy case for the scanner
below, but the scanner doesn't rely on that.

Since the firmware is frozen, the buffer is constant, and the field order is
fixed, there is exactly one defect to handle and it does not move. `JsonRepair`
therefore doesn't parse or rebuild anything — it deletes the bad field and hands
the remaining bytes to a normal parser.

The one subtlety is where to stop deleting. Skipping to the field's closing quote
doesn't work, because binary junk can contain a quote byte and nothing
distinguishes a real terminator from an accidental one. So the cut is anchored on
where the *next* field begins: a short identifier in quotes, followed by a colon,
preceded by a comma. That makes the junk's length and content irrelevant — it
doesn't matter what is in there, only that a real field follows it. Deleting
`[bad field, next field)` also takes the bad field's trailing comma and leaves
the previous field's, which is exactly what keeps the document well-formed.

If no field follows, the read fails rather than guessing: removing the *preceding*
comma instead would be the correct repair, and a wrong guess there corrupts the
document. A failed read is already handled — the poller serves last-known state
with a visible age.

Well-formed documents take a fast path and are never touched.

**Known limitation.** Junk that imitates a complete field — a comma, a quoted
identifier, and a colon — creates a false anchor, so the excision stops early and
the document stays malformed. This is accepted: the buffer on this firmware is
constant and contains neither a quote nor a comma, and the failure mode is a clean
parse error rather than corrupted data. An earlier version rebuilt the entire
document from scratch and survived this case, at roughly four times the code. That
wasn't worth it for a defect that cannot change.

`tools/salvage_check.py` mirrors the algorithm in Python and covers the verbatim
hardware capture plus adversarial junk (stray quotes, commas, braces, pure control
bytes, and the known-limitation case). It also inspects a live capture, which is
the quickest way to see what your particular controller emits:

```bash
curl -s http://<fan>/status.json.cgi | python3 tools/salvage_check.py -
```

### Firmware variance

Field names and types differ across Gen 2 revisions. 2.15.1 uses `inside`,
`attic`, `oa`, and `dns`; older documentation describes `house_temp`,
`attic_temp`, `oa_temp`, and `DNS1`. The observed names are primary and the
documented ones are aliases, so both work. Some builds also emit every number as
a JSON string. `FanRawStatus` parses numbers leniently, captures unrecognised
keys, and falls back to aliases rather than reporting null — add to
`FanRawStatus.Resolve()` if your controller uses something not covered.

Two fields are passed through verbatim for diagnostics: `dip_switches` and
`remote_switch`. AirScape doesn't publish what the bit positions mean, so they
are surfaced rather than interpreted.

Status is served at `status.json.cgi` on most firmware and `status.json.html`
on some. The client tries both once and remembers which answered.

### The two endpoints disagree with each other

**Only `status.json.cgi` returns JSON.** `fanspd.cgi` — the command endpoint —
answers with a stream of label-and-tag pairs that looks like XML but isn't:

```
inside: <house_temp>74</house_temp>
```

The labels sit outside the tags, the spacing is inconsistent, and
`server_response` carries the same binary buffer, so an XML parser fails on it
too. `TagStreamParser` therefore doesn't parse the document — it looks up the
tags it wants by name and never reads the binary one.

Worse, **the tag names are the older documented ones**. The same controller that
calls it `inside` in JSON calls it `house_temp` here. `oa` becomes `oa_temp`,
`attic` becomes `attic_temp`, and the dip switches become `DIPS`.

Commands still publish fresh state without an extra round trip, which matters on
a two-socket device — the response just needs a different reader than the poll
does.

### Damper doors lie about speed

Starting from off, the damper doors take about 30 seconds to open. While they
move, the controller reports `fanspd` as 0 whatever speed it is heading for, and
does not honour speed-up or speed-down commands. An earlier version stepped on a
fixed 350 ms delay, so from off every step after the first was sent into the void
and the fan stopped at speed 1.

`FanCommandQueue` now only steps on a *settled* reading. It starts from a fresh
status read (the wall switch or remote may have moved the speed), and whenever the
doors are moving it re-reads status every `DoorPollMilliseconds` until they stop.
Then it steps the remainder from the speed the controller actually reports. Off is
the exception: it is sent immediately, even mid door travel, and sent once more if
the fan is still running when things settle. If the doors never settle within
`DoorTimeoutSeconds` the command is abandoned and logged.

The door polls share `FanClient`'s single-flight gate with the regular poller, so
the controller still never sees more than one connection from us.

This has only been exercised against the mock and the unit tests so far. The first
real run is next cooling season.

## Developing

Needs the .NET 10 SDK.

```bash
dotnet test                                   # unit + endpoint tests
python3 tools/mockfan.py --port 8081 --door-seconds 8
Fan__Host=localhost:8081 Http__Port=5099 AIRSCAPE_CONFIG_DIR=./config \
  Cooldown__Path=./data/cooldown.json dotnet run --project src/AirscapeWhf
```

`tools/mockfan.py` stands in for the controller, and reproduces its awkward
parts on purpose: the two-socket cap with silent drops, relative-only commands,
damper doors that report speed 0 and ignore speed commands while they move, the
malformed `server_response` buffer, and optionally the string-typed JSON.

To see the failure mode this project exists to fix, fire six parallel requests
straight at the mock and watch four get dropped:

```bash
for i in $(seq 1 6); do
  curl -s -m 5 -o /dev/null -w "%{http_code} %{time_total}s\n" \
    http://localhost:8081/status.json.cgi &
done
```

Then do the same through the service and watch all six succeed.

Useful flags: `--sockets 1` to be really unkind, `--stringify` to emit the
string-typed JSON some firmware produces, `--clean-json` to suppress the
malformed field, `--latency` to simulate a slow board, `--door-seconds` to shorten
the door wait, and `--door-commands accept` to model firmware that queues speed
commands during door travel instead of ignoring them.

## Status

`dotnet test` covers the door-aware stepper against a scripted fake controller,
the cooldown forecast, the `/metrics` rendering and staleness rules, and the MCP
endpoint end to end (auth, tool list, read-only mode, validation errors).
`docker build --target test .` runs the same suite, and the default image build
runs it too.

The front end is tested by `tools/uitest.js`, which drives it under jsdom across
sensor-less fans, stale state, door travel, larger models, and the cooldown editor
(`cd tools && npm install && node uitest.js`). The JSON repair is tested against
the verbatim hardware capture and adversarial variants via
`tools/salvage_check.py`, and the cooldown state machine via
`tools/cooldown_check.py`.

## Not done

- Single fan per container. Multiple fans would mean keying the store and client
  by host; the structure allows it, nothing implements it.
- No history in the service. The snapshot is instantaneous; trends come from
  scraping `/metrics` into Prometheus, not from this cache.
- No scheduling. "Run from 10pm" or "only on weeknights" belongs in Home
  Assistant against this API, not here.
- No auth on the REST API or UI; they assume a trusted LAN. `/mcp` uses a static
  bearer token; OAuth2 with Entra ID is the planned next step.
