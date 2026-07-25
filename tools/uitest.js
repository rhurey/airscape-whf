const fs = require("fs");
const { JSDOM } = require("jsdom");

const path = require("path");

const html = fs.readFileSync(
  path.join(__dirname, "..", "src", "AirscapeWhf", "wwwroot", "index.html"), "utf8");

let failures = 0;
const check = (name, actual, expected) => {
  const ok = String(actual) === String(expected);
  if (!ok) failures++;
  console.log(`  ${ok ? "PASS" : "FAIL"}  ${name}` +
    (ok ? ` = ${actual}` : `\n          expected: ${expected}\n          actual:   ${actual}`));
};

function boot(cooldownReply = { enabled: false, steps: [], releaseMarginF: 2, maxSpeed: 5, next: null }) {
  const posts = [];
  const bodies = [];
  let onmessage = null;

  // Stubs must exist before the page script runs, so install them in beforeParse.
  const dom = new JSDOM(html, {
    runScripts: "dangerously",
    pretendToBeVisual: true,
    beforeParse(w) {
      w.EventSource = class {
        set onmessage(fn) { onmessage = fn; }
        get onmessage() { return onmessage; }
        set onerror(_) {}
        close() {}
      };
      w.fetch = (path, opts) => {
        const method = opts?.method || "GET";
        posts.push(`${method} ${path}`);
        if (opts?.body) bodies.push(JSON.parse(opts.body));
        if (path === "/api/cooldown" && method === "GET") {
          return Promise.resolve({ ok: true, json: () => Promise.resolve(cooldownReply) });
        }
        return Promise.resolve({ ok: true, json: () => Promise.resolve({}) });
      };
    },
  });

  return {
    dom, w: dom.window, posts, bodies,
    push: (snap) => onmessage({ data: JSON.stringify(snap) }),
    $: (id) => dom.window.document.getElementById(id),
  };
}

// The page fetches /api/cooldown on load; let that promise settle before asserting.
const flush = () => new Promise((r) => setTimeout(r, 0));

const base = {
  online: true, observedAt: new Date().toISOString(), ageSeconds: 3,
  speed: 5, maxSpeed: 5, running: true, doorMoving: false, interlocked: false,
  timerMinutes: 0, cfm: 2500, powerWatts: 180,
  houseTempF: 69, atticTempF: 65, outsideTempF: 59,
  model: "3.5eWHF", softwareVersion: "2.15.1",
  ipAddress: "192.168.36.193", macAddress: "60:CB:FB:00:13:7D",
  dipSwitches: "11100", remoteSwitch: "1111",
  hasSensors: true, deltaF: 10, cfmPerWatt: 13.9, coolingBtuh: 27000, eer: 150,
  verdict: "Good", headline: "Pulling in air 10\u00B0 cooler than the house",
};

// --------------------------------------------------------------------
async function main() {
console.log("\n[1] running at full speed, matching the screenshot");
{
  const t = boot();
  t.push(base);
  check("headline", t.$("headline").textContent, base.headline);
  check("outside temp", t.$("t-out").textContent, "59");
  check("house temp", t.$("t-house").textContent, "69");
  check("attic temp", t.$("t-attic").textContent, "65");
  check("speed buttons rendered", t.$("steps").children.length, 5);
  check("speed 5 active", t.$("steps").children[4].className.includes("is-on"), true);
  check("speed 1 inactive", t.$("steps").children[0].className.includes("is-on"), false);
  check("airpath animating", t.$("airpath").className.includes("is-running"), true);
  check("flow rate at max", t.$("airpath").style.getPropertyValue("--flow-duration"), "0.35s");
  check("metric cells", t.$("metrics").children.length, 5);
  check("EER shown", t.$("metrics").children[4].textContent.includes("150"), true);
  check("cooling comma-formatted", t.$("metrics").textContent.includes("27,000"), true);
  check("timer readout", t.$("timer-readout").textContent, "no timer");
  check("beacon live", t.$("beacon").className.includes("is-live"), true);
  check("no notice", t.$("notice").className.includes("is-shown"), false);
  check("colophon has fw", t.$("colophon").textContent.includes("fw 2.15.1"), true);
  check("colophon has dip switches", t.$("colophon").textContent.includes("dip 11100"), true);
  check("colophon has remote switch", t.$("colophon").textContent.includes("rem 1111"), true);
  check("outside is coolest -> cyan", t.$("t-out").style.color, "rgb(100, 210, 232)");
  check("attic mid-range -> blended", t.$("t-attic").style.color, "rgb(180, 181, 128)");
}

// --------------------------------------------------------------------
console.log("\n[2] off, and outside is warmer than inside");
{
  const t = boot();
  t.push({ ...base, speed: 0, running: false, cfm: 0, powerWatts: 0,
    outsideTempF: 78, houseTempF: 72, atticTempF: 95, deltaF: -6,
    cfmPerWatt: null, coolingBtuh: null, eer: null,
    verdict: "Counterproductive", headline: "Outside is warmer than inside. Leave it off." });
  check("headline flagged bad", t.$("headline").className.includes("is-bad"), true);
  check("airpath still", t.$("airpath").className.includes("is-running"), false);
  check("no speed active", [...t.$("steps").children].some(b => b.className.includes("is-on")), false);
  check("turn-off disabled", t.$("turn-off").disabled, true);
  check("derived metrics dropped", t.$("metrics").children.length, 2);
  check("attic hottest -> amber", t.$("t-attic").style.color, "rgb(233, 161, 59)");
}

// --------------------------------------------------------------------
console.log("\n[3] controller unreachable, serving stale state");
{
  const t = boot();
  t.push({ ...base, online: false, ageSeconds: 240,
    lastError: "Controller did not answer within 6s." });
  check("beacon down", t.$("beacon").className.includes("is-down"), true);
  check("freshness in minutes", t.$("freshness").textContent, "4m ago");
  check("notice shown", t.$("notice").className.includes("is-shown"), true);
  check("notice explains staleness", t.$("notice").textContent.includes("4m ago"), true);
  check("notice names the cause", t.$("notice").textContent.includes("did not answer"), true);
}

// --------------------------------------------------------------------
console.log("\n[4] transient states: doors moving, interlock, timer");
{
  const t = boot();
  t.push({ ...base, doorMoving: true, timerMinutes: 95 });
  check("door notice", t.$("notice").textContent.includes("30 seconds"), true);
  check("timer formatted", t.$("timer-readout").textContent, "1h 35m left");

  t.push({ ...base, doorMoving: false, interlocked: true, timerMinutes: 45 });
  check("interlock notice", t.$("notice").textContent.includes("interlock"), true);
  check("sub-hour timer", t.$("timer-readout").textContent, "45m left");
}

// --------------------------------------------------------------------
console.log("\n[5] no temperature sensor package");
{
  const t = boot();
  t.push({ online: true, observedAt: new Date().toISOString(), ageSeconds: 1,
    speed: 2, maxSpeed: 5, running: true, doorMoving: false, interlocked: false,
    timerMinutes: 0, cfm: 1000, powerWatts: 48, hasSensors: false,
    cfmPerWatt: 20.8, verdict: "Unknown", headline: "Running at speed 2" });
  check("temps show placeholder", t.$("t-out").textContent, "--");
  check("placeholder styled void", t.$("t-out").className.includes("is-void"), true);
  check("no crash, speed 2 active", t.$("steps").children[1].className.includes("is-on"), true);
  check("only real metrics", t.$("metrics").children.length, 3);
}

// --------------------------------------------------------------------
console.log("\n[6] interaction: tapping a speed posts and shows pending");
{
  const t = boot();
  t.push({ ...base, speed: 1 });
  t.$("steps").children[3].dispatchEvent(new t.w.MouseEvent("click", { bubbles: true }));
  const sent = () => t.posts.filter((x) => x.startsWith("POST"));
  check("posted absolute speed", sent()[0], "POST /api/speed/4");
  check("target shows pending", t.$("steps").children[3].className.includes("is-pending"), true);
  check("old speed not shown active", t.$("steps").children[0].className.includes("is-on"), false);

  t.push({ ...base, speed: 4 });
  check("pending clears on confirm", t.$("steps").children[3].className.includes("is-pending"), false);
  check("confirmed speed active", t.$("steps").children[3].className.includes("is-on"), true);

  t.$("add-hour").dispatchEvent(new t.w.MouseEvent("click", { bubbles: true }));
  check("timer posts", sent()[1], "POST /api/timer/1");
  t.$("turn-off").dispatchEvent(new t.w.MouseEvent("click", { bubbles: true }));
  check("off posts", sent()[2], "POST /api/off");
}

// --------------------------------------------------------------------
console.log("\n[7] firmware that omits the undocumented diagnostic fields");
{
  const t = boot();
  const { dipSwitches, remoteSwitch, ...without } = base;
  t.push(without);
  check("no dip label", t.$("colophon").textContent.includes("dip"), false);
  check("no rem label", t.$("colophon").textContent.includes("rem"), false);
  check("fw still shown", t.$("colophon").textContent.includes("fw 2.15.1"), true);
}

console.log("\n[8] cooldown: disabled by default, hidden until switched on");
{
  const t = boot();
  t.push(base);
  check("switch reads off", t.$("cooldown-toggle").getAttribute("aria-checked"), "false");
  check("rules hidden", t.$("cooldown-body").className.includes("is-hidden"), true);
  check("no forecast text", t.$("forecast").textContent, "");
}

console.log("\n[9] cooldown: switching on seeds a usable rule");
{
  const t = boot();
  t.push(base);
  t.$("cooldown-toggle").dispatchEvent(new t.w.MouseEvent("click", { bubbles: true }));
  check("switch reads on", t.$("cooldown-toggle").getAttribute("aria-checked"), "true");
  check("rules revealed", t.$("cooldown-body").className.includes("is-hidden"), false);
  check("one row seeded", t.$("rules").children.length, 1);
  const row = t.$("rules").children[0];
  check("seeded temperature", row.querySelector(".rule-temp").value, "60");
  check("seeded action is off", row.querySelector(".rule-speed").value, "0");
  check("speed choices match the fan", row.querySelector(".rule-speed").children.length, 6);
  check("first choice is Off", row.querySelector(".rule-speed").children[0].textContent, "Off");
}

console.log("\n[10] cooldown: adding and removing steps");
{
  const t = boot();
  t.push(base);
  t.$("cooldown-toggle").dispatchEvent(new t.w.MouseEvent("click", { bubbles: true }));
  t.$("rule-add").dispatchEvent(new t.w.MouseEvent("click", { bubbles: true }));
  check("two rows", t.$("rules").children.length, 2);
  check("new step is colder than the last", t.$("rules").children[1].querySelector(".rule-temp").value, "56");
  t.$("rules").children[0].querySelector(".rule-drop")
    .dispatchEvent(new t.w.MouseEvent("click", { bubbles: true }));
  check("back to one row", t.$("rules").children.length, 1);
  check("the remaining row is the one kept", t.$("rules").children[0].querySelector(".rule-temp").value, "56");
}

await flush();
console.log("\n[11] cooldown: forecast reads from the server");
{
  const t = boot({ enabled: true, steps: [{ belowF: 62, speed: 2 }, { belowF: 56, speed: 0 }],
                   releaseMarginF: 2, maxSpeed: 5, next: { belowF: 62, speed: 2 } });
  await flush();
  t.push(base);
  check("names the next step", t.$("forecast").textContent.includes("eases to 2 below 62"), true);
  check("quotes the current temperature", t.$("forecast").textContent.includes("59"), true);
}

await flush();
console.log("\n[12] cooldown: honest when nothing will fire");
{
  const t = boot({ enabled: true, steps: [{ belowF: 40, speed: 0 }],
                   releaseMarginF: 2, maxSpeed: 5, next: null });
  await flush();
  t.push({ ...base, speed: 0, running: false });
  check("says rules need a running fan",
        t.$("forecast").textContent.includes("once the fan is running"), true);
  check("marked idle", t.$("forecast").className.includes("is-idle"), true);
}

await flush();
console.log("\n[13] cooldown: no sensor means nothing can fire");
{
  const t = boot({ enabled: true, steps: [{ belowF: 60, speed: 0 }],
                   releaseMarginF: 2, maxSpeed: 5, next: null });
  const { outsideTempF, ...noSensor } = base;
  await flush();
  t.push(noSensor);
  check("says so plainly", t.$("forecast").textContent.includes("No outside reading"), true);
}

console.log("\n[14] larger model reports a higher max speed");
{
  const t = boot();
  t.push({ ...base, speed: 7, maxSpeed: 10, model: "4.4eWHF" });
  check("ten buttons", t.$("steps").children.length, 10);
  check("speed 7 active", t.$("steps").children[6].className.includes("is-on"), true);
  check("ident uses model", t.$("ident").textContent, "4.4eWHF whole house fan");
}

}

main().then(() => {
console.log(failures === 0
  ? "\nAll checks passed.\n"
  : `\n${failures} check(s) failed.\n`);
  process.exit(failures === 0 ? 0 : 1);
});
