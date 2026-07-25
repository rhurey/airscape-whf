#!/usr/bin/env python3
"""
Reference implementation and test suite for the cooldown governor.

The fan is run to flush the house with cool night air. As the night goes on the
outside air keeps dropping, and at some point you are no longer cooling the house
so much as refrigerating it. The governor watches outside air temperature and eases
the fan off as it falls.

The design rests on one invariant, and everything else follows from it:

    THE GOVERNOR CAN ONLY EVER SLOW THE FAN DOWN OR STOP IT.
    It never speeds the fan up and never starts it.

That is what makes the thing safe to leave switched on. A rule that could start a
fan at 3am because the outside temperature blipped is a rule nobody would trust.

The second design point is the latch. Once a step has fired it will not fire again
until the outside temperature climbs back past its threshold plus a release margin.
Without that, standing at a threshold while the reading jitters by a degree would
have the governor fighting the user for control of the speed. With it, a manual
override sticks.

Mirrors src/AirscapeWhf/Automation/CooldownGovernor.cs, so the state machine can be
exercised without a .NET SDK.

    python3 tools/cooldown_check.py
"""

import sys

STALE_MULTIPLIER = 3


class Governor:
    def __init__(self, steps, release_margin=2, enabled=True):
        # steps: list of (below_f, speed). speed 0 means off.
        self.steps = list(steps)
        self.release_margin = release_margin
        self.enabled = enabled
        self.latched = set()

    def evaluate(self, *, outside_f, speed, online=True, age_seconds=0,
                 stale_after=60):
        """
        Returns the speed to command, or None to do nothing.
        Mutates latch state, so call it once per observation.
        """
        if not self.enabled or not self.steps:
            return None

        # Never act on a reading we don't trust. A stale snapshot is the fan's
        # last known state, not its current one.
        if not online or age_seconds > stale_after:
            return None

        if outside_f is None:
            return None

        # Release any step the temperature has climbed back above. This happens
        # even while the fan is off, so the rules are armed again by morning.
        for below_f, _ in self.steps:
            if outside_f >= below_f + self.release_margin:
                self.latched.discard(below_f)

        # The invariant: never start the fan.
        if speed <= 0:
            return None

        firing = None
        for below_f, target in self.steps:
            if outside_f >= below_f:
                continue                     # threshold not reached
            if below_f in self.latched:
                continue                     # already acted on this step
            if target >= speed:
                continue                     # would not slow the fan; never raise
            if firing is None or target < firing[1]:
                firing = (below_f, target)   # take the most restrictive

        if firing is None:
            return None

        self.latched.add(firing[0])
        return firing[1]

    def forecast(self, outside_f, speed):
        """The next step that will fire, for the UI. Returns (below_f, speed) or None."""
        if not self.enabled or outside_f is None or speed <= 0:
            return None
        pending = [
            (b, t) for b, t in self.steps
            if outside_f >= b and b not in self.latched and t < speed
        ]
        return max(pending, key=lambda s: s[0]) if pending else None


# --- tests -----------------------------------------------------------------

_failures = 0


def check(name, ok, detail=""):
    global _failures
    if not ok:
        _failures += 1
    print(f"  {'PASS' if ok else 'FAIL'}  {name}{'' if ok else '  <- ' + detail}")


def scenario(title):
    print(f"\n[{title}]")


# ---------------------------------------------------------------------------
scenario("a night: 75 down to 48, then back up")
g = Governor([(62, 2), (56, 0)])
speed = 5
log = []
for oa in [75, 70, 66, 63, 62, 61, 58, 57, 56, 54, 50, 48,
           52, 58, 60, 64, 68, 72]:
    got = g.evaluate(outside_f=oa, speed=speed)
    if got is not None:
        log.append((oa, speed, got))
        speed = got
check("eased to 2 when it dropped below 62", (61, 5, 2) in log, f"log={log}")
check("stopped when it dropped below 56", (54, 2, 0) in log, f"log={log}")
check("acted exactly twice all night", len(log) == 2, f"log={log}")
check("did not restart as it warmed back up", speed == 0)
print(f"        actions: {log}")

# ---------------------------------------------------------------------------
scenario("jitter at a threshold does not cause a fight")
g = Governor([(60, 2)])
speed, actions = 5, 0
for oa in [61, 59, 61, 59, 60, 59, 61, 58, 60, 59]:
    got = g.evaluate(outside_f=oa, speed=speed)
    if got is not None:
        actions += 1
        speed = got
check("fired once, not once per dip", actions == 1, f"fired {actions} times")
check("settled at the step's speed", speed == 2)

# ---------------------------------------------------------------------------
scenario("a manual override sticks")
g = Governor([(60, 2)])
speed = g.evaluate(outside_f=58, speed=5) or 5
check("governor eased it to 2", speed == 2)
speed = 4                                    # user overrides upward
for oa in [58, 57, 58, 59]:
    got = g.evaluate(outside_f=oa, speed=speed)
    check("governor did not undo the override", got is None, f"tried to set {got}")
    break
check("still at the user's speed", speed == 4)

# ---------------------------------------------------------------------------
scenario("rules re-arm after it warms up")
g = Governor([(60, 2)], release_margin=2)
speed = g.evaluate(outside_f=58, speed=5) or 5
check("fired on the way down", speed == 2)
check("still latched at 61 (inside the margin)",
      g.evaluate(outside_f=61, speed=5) is None)
g.evaluate(outside_f=63, speed=0)            # 60 + 2 -> released
check("released once past the margin", 60 not in g.latched)
check("fires again on the next descent", g.evaluate(outside_f=58, speed=5) == 2)

# ---------------------------------------------------------------------------
scenario("the invariant: never starts or speeds up the fan")
g = Governor([(60, 3)])
check("does nothing while the fan is off", g.evaluate(outside_f=40, speed=0) is None)
g2 = Governor([(60, 3)])
check("never raises a slower fan to the step speed",
      g2.evaluate(outside_f=50, speed=1) is None)
g3 = Governor([(60, 0)])
check("does stop a running fan", g3.evaluate(outside_f=50, speed=1) == 0)

# ---------------------------------------------------------------------------
scenario("bad or missing data is never acted on")
g = Governor([(60, 0)])
check("no sensor reading", g.evaluate(outside_f=None, speed=5) is None)
check("fan unreachable", g.evaluate(outside_f=40, speed=5, online=False) is None)
check("reading too old", g.evaluate(outside_f=40, speed=5, age_seconds=300) is None)
check("acts on a fresh reading", g.evaluate(outside_f=40, speed=5) == 0)

# ---------------------------------------------------------------------------
scenario("disabled, and empty rule sets")
check("disabled does nothing",
      Governor([(60, 0)], enabled=False).evaluate(outside_f=40, speed=5) is None)
check("no steps does nothing", Governor([]).evaluate(outside_f=40, speed=5) is None)

# ---------------------------------------------------------------------------
scenario("a plunge past several steps at once")
g = Governor([(65, 4), (60, 2), (55, 0)])
got = g.evaluate(outside_f=50, speed=5)
check("jumps straight to the most restrictive step", got == 0, f"got {got}")
check("only that step is latched", g.latched == {55}, f"latched={g.latched}")
check("no follow-up action at the same temperature",
      g.evaluate(outside_f=50, speed=0) is None)

# ---------------------------------------------------------------------------
scenario("forecast shown in the UI")
g = Governor([(62, 2), (56, 0)])
check("names the next step to fire", g.forecast(70, 5) == (62, 2), f"{g.forecast(70, 5)}")
g.evaluate(outside_f=61, speed=5)
check("advances after the first fires", g.forecast(60, 2) == (56, 0), f"{g.forecast(60, 2)}")
check("nothing to forecast when off", g.forecast(70, 0) is None)

print()
if _failures:
    print(f"{_failures} check(s) failed.\n")
    sys.exit(1)
print("All checks passed.\n")
