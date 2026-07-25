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
$EDITOR docker-compose.yml     # set Fan__Host
docker compose up -d
```

Open <http://localhost:8080>.

### Configuration

All settings are environment variables under the `Fan__` prefix.

| Variable | Default | Notes |
| --- | --- | --- |
| `Fan__Host` | *(required)* | IP or hostname of the controller |
| `Fan__PollIntervalSeconds` | `20` | Each poll occupies one of the fan's two sockets |
| `Fan__TimeoutSeconds` | `6` | Per-request timeout |
| `Fan__MaxSpeed` | `5` | 3.5e tops out at 5; raises itself if a higher speed is observed |
| `Fan__StepDelayMilliseconds` | `350` | Pacing between the steps of a speed change |
| `Fan__FailuresBeforeOffline` | `3` | Consecutive failures before the UI is told it's stale |
| `Cooldown__Path` | `/data/cooldown.json` | Where cooldown rules are saved |
| `Kestrel__Certificates__Default__Path` | — | PEM certificate chain, for HTTPS |
| `Kestrel__Certificates__Default__KeyPath` | — | PEM private key |

## Deployment

### TLS

The fan can never have a real certificate. This container can.

Kestrel reads a PEM certificate and key directly, so a wildcard from your ACME
pipeline needs no conversion — `*.home.hurey.com` already covers
`fan.home.hurey.com`, since a wildcard matches one label.

Two things reliably go wrong here, and both are addressed by
`deploy/renew-hook.sh`:

The file extension is irrelevant. acme.sh writes PEM whatever it names the file,
and Kestrel parses contents rather than trusting the name — `fullchain.cer` and
`fullchain.pem` are the same bytes. Check with `head -1 fullchain.cer`; if it
says `-----BEGIN CERTIFICATE-----`, you're done thinking about it.

**Permissions.** The chiseled image runs as UID 64198, which cannot read a typical
ACME output directory. Copy the pair somewhere that user owns rather than
loosening permissions on your PKI.

**Renewal.** Kestrel reads the certificate once at startup and does not watch the
file, so a renewal without a restart silently serves the old one until it expires.

With acme.sh, declare both once and renewals take care of themselves:

```bash
acme.sh --install-cert -d '*.home.hurey.com' --ecc \
  --fullchain-file /etc/ssl/airscape/fullchain.pem \
  --key-file       /etc/ssl/airscape/privkey.pem \
  --reloadcmd      "chown 64198:64198 /etc/ssl/airscape/*.pem \
                    && chmod 400 /etc/ssl/airscape/privkey.pem \
                    && systemctl reload airscape-whf"
```

Drop `--ecc` if your cert is RSA. Note that acme.sh stores wildcards under a
directory with a literal asterisk, and appends `_ecc` for elliptic-curve certs —
`~/.acme.sh/*.home.hurey.com_ecc/` — which is a common source of "file not found".

For certbot or anything else, `deploy/renew-hook.sh <acme-output-dir>` does the
same job: it accepts either naming convention, verifies the file really is PEM,
installs with the right owner and mode, and restarts the container.

Then point DNS at the host — an A record for `fan.home.hurey.com` — and
<https://fan.home.hurey.com:8443> works with no browser warning. Map the port to
443 in `docker-compose.yml` if you'd rather drop it from the URL.

HTTP stays open on 8080 on purpose. Forcing a redirect breaks simple `curl`
integrations on a trusted LAN and buys nothing.

### Persistence

Cooldown rules are saved to `/data/cooldown.json`, backed by a bind mount at
`./data` so you can read and back up the file directly. Create it with the right
owner before first start:

```bash
mkdir -p data && sudo chown 64198:64198 data
```

Without a writable mount the rules still work, but are lost on restart and the
log says so once.

### Starting on boot

`restart: unless-stopped` already brings the container back when Docker starts,
so enabling Docker is enough:

```bash
sudo systemctl enable docker
```

For explicit control — ordering against the network, a real `systemctl status`,
and one place to restart after a renewal — install the unit:

```bash
sudo cp -r . /opt/airscape-whf
sudo cp deploy/airscape-whf.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now airscape-whf
```

The root filesystem is read-only, capabilities are dropped, and privilege
escalation is disabled. `/tmp` is a tmpfs because the runtime wants scratch space
even with a read-only root.

## Cooldown

You run the fan to flush the house with cool night air. As the night goes on the
outside air keeps dropping, and at some point you are no longer cooling the house
so much as refrigerating it. Cooldown watches outside air temperature and eases
the fan off as it falls.

Rules are a ladder — below this temperature, run no faster than that:

    Below 62°  ->  Speed 2
    Below 56°  ->  Off

One rung is the simple case ("stop below 58"); more rungs give you a gradual
wind-down. Edit them in the UI; they're saved to `/data/cooldown.json`.

**One invariant governs the whole feature: it can only slow the fan down or stop
it.** It never speeds the fan up and it never starts it. That is what makes it
safe to leave switched on — a rule that could start a fan at 3am because the
outside temperature blipped is a rule nobody would trust. If you want the fan
running again, you turn it on.

**Once a step fires it latches**, and won't fire again until the temperature
climbs back past its threshold plus a release margin (2°F by default). Without
that, sitting at a threshold while the reading jitters by a degree would have the
governor fighting you for control of the speed. With it, if you override the
speed upward after a step has fired, your setting sticks. Steps re-arm on their
own as it warms up, so the ladder is ready again by the next evening.

The governor **never acts on data it doesn't trust**: no reading, a stale
snapshot, or an unreachable fan all mean it does nothing. It reads the same
broadcast the browsers do, so it costs the controller no extra requests.

Rules that could never fire are rejected rather than silently ignored — a colder
step with a *higher* speed than a warmer one can never trigger, so the API says
so instead of accepting it.

The state machine is exercised in `tools/cooldown_check.py`, including a
simulated night from 75°F down to 48°F and back.

## API

| Method | Path | |
| --- | --- | --- |
| `GET` | `/api/status` | Current snapshot |
| `GET` | `/api/stream` | SSE, pushes on every state change |
| `POST` | `/api/speed/{0..max}` | Absolute speed |
| `POST` | `/api/nudge/{±n}` | Relative speed |
| `POST` | `/api/timer/{hours}` | Add timer hours |
| `POST` | `/api/off` | Off |
| `GET` | `/api/cooldown` | Current rules, plus which step fires next |
| `PUT` | `/api/cooldown` | Replace the rules |
| `GET` | `/healthz` | Liveness |

Commands return `202 Accepted`. They are queued, not synchronous — see below.

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

While the doors are moving the controller reports `fanspd` as 0 regardless of the
speed it is heading for. A stepper that believes it will issue speed-up commands
for the ~30 seconds the doors take and overshoot badly, so `FanCommandQueue`
trusts its own arithmetic while `doorinprocess` is set and the controller's report
only once the doors have settled.

## Developing

`tools/mockfan.py` stands in for the controller, and reproduces its awkward
parts on purpose — the two-socket cap with silent drops, relative-only commands,
30-second damper doors, the malformed `server_response` buffer, and optionally
the string-typed JSON.

```bash
python3 tools/mockfan.py --port 8081
Fan__Host=localhost:8081 dotnet run --project src/AirscapeWhf
```

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
malformed field, `--latency` to simulate a slow board.

## Status

The front end is tested — `tools/uitest.js` drives it under jsdom across eight
scenarios including sensor-less fans, stale state, larger models, and the
cooldown editor. The JSON repair is tested against the verbatim hardware capture
and adversarial variants via `tools/salvage_check.py`, and the cooldown state
machine via `tools/cooldown_check.py`.

**The C# has not been compiled**; it was written without a .NET SDK to hand. The
repair *algorithm* is verified, because the Python port mirrors it line for
line, but the C# transcription of it is not. Expect to fix a few things on first
`dotnet build`.

Targets `net8.0`. Bumping the TFM and the two image tags in the `Dockerfile` is
the whole upgrade.

## Not done

- Single fan per container. Multiple fans would mean keying the store and client
  by host; the structure allows it, nothing implements it.
- No history. The snapshot is instantaneous. Temperature and runtime trends
  would want a time-series store rather than this cache.
- Cooldown triggers on outside air only. Triggering on inside temperature, or on
  the difference between the two, would be a reasonable extension; the rule model
  would need a sensor field.
- No scheduling. "Run from 10pm" or "only on weeknights" belongs in Home
  Assistant against this API, not here.
- No auth. Assumes a trusted LAN.
