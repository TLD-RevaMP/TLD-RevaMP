#!/usr/bin/env python3
"""A save loaded: what its cars, trailers and trailer grips do in the first seconds (Fixes.LoadWatch). Volkan's save with
a car trailer hitched to a pickup blew apart at every load (2026-11-11: the pickup jolted at 109-176 m/s, the trailer
hit a parked car at 199 m/s). Desktop only; the player's saves are backed up and put back (ProtectedSession).

  tools/loadfreeze.py SAVEDIR NAME | --newgame SEED  [--ab] [--top 10] [--trace] [--set PATH=VALUE ...]

SAVEDIR holds the save's files (TLDsaveData_NAME.tlds and its .leases/.players, TLDsaveProp_NAME.tldsp); they are
copied into the test copy's saves as `loadfreeze` and removed after. --ab loads it twice: the fixes off (before),
then on (each of SWITCHES). PASS: no vehicle faster than MAX_MS in the watch."""
import sys, time, shutil, pathlib, argparse
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game

MAX_MS = 5.0
ap = argparse.ArgumentParser()
ap.add_argument("savedir", nargs="?"); ap.add_argument("name", nargs="?"); ap.add_argument("--newgame", type=int, help="a new world with this seed instead of a save")
ap.add_argument("--ab", action="store_true"); ap.add_argument("--top", type=int, default=10); ap.add_argument("--trace", action="store_true")
ap.add_argument("--frozen", type=float, default=0, help="also list the bodies still frozen near the player (stillfrozen) until this many seconds after the load")
ap.add_argument("--set", action="append", default=[], help="PATH=VALUE through the bridge before each load (A/B of other switches)")
a = ap.parse_args()
files = []
if a.newgame is None:
    if not a.name: sys.exit("SAVEDIR NAME, or --newgame SEED")
    src = pathlib.Path(a.savedir)
    files = [f for f in src.iterdir() if f.name.startswith((f"TLDsaveData_{a.name}.", f"TLDsaveProp_{a.name}."))]
    if not any(f.name == f"TLDsaveData_{a.name}.tlds" for f in files): sys.exit(f"no TLDsaveData_{a.name}.tlds in {src}")


SWITCHES = ["TLDRevamp.Fixes.LoadFreeze.Enabled", "TLDRevamp.Fixes.UnfreezeOnGround.HitchPairs"]   # all off for the "before" load


def watch(label, enabled):
    b = game.Bridge()
    for s in SWITCHES:
        try: b(f"set {s} {enabled}")
        except RuntimeError: print(f"  (this build has no {s})", flush=True)
    for kv in a.set:
        k, v = kv.split("=", 1); print(f"  set {k} {v}:", b(f"set {k} {v}"), flush=True)
    if a.newgame is None: b = game.loadsave("loadfreeze")
    else: game.newgame(a.newgame); b = game.Bridge()
    print("  player after the load:", b("gpos"), flush=True)
    while True:
        r = b("loadwatch")
        if r.get("t", -1) >= 25: break
        time.sleep(1)
    # the test player stands still: in RedPickup an NPC kills it within ~15 s and the world goes (the watch is kept)
    try: print("  player at the end:", b("gpos"), flush=True)
    except RuntimeError: print("  player at the end: gone (died: the world went back to the menu)", flush=True)
    bodies = sorted(r["bodies"], key=lambda x: -x["top"])
    worst = bodies[0]["top"] if bodies else 0.0
    print(f"\n== {label}: {len(bodies)} vehicles and grips, worst {worst:.1f} m/s", flush=True)
    for x in bodies[:a.top]:
        print(f"   {x['name']:<28} save {x['save']:<6} top {x['top']:7.2f} m/s at {x['topAt']:5.1f} s  let go "
              f"{'never' if x['freeAt'] < 0 else format(x['freeAt'], '.1f') + ' s':>7}  moved {x['moved']:6.2f} m (height {x.get('dy', 0):+.2f})"
              + f"  [{x.get('item')}]" + (f"  hitch gap {x['gap0']:.2f}->{x['gapMax']:.2f} m, ball {x['ball0']:.2f}->{x['ballMax']:.2f} m on {x['hitched']}" if x['gap0'] >= 0 else ""), flush=True)
    if a.trace: print(b("loadtrace")["trace"], flush=True)
    t0 = time.time() - r.get("t", 0)
    for at in (45, 90, 150, 240):
        if at > a.frozen: break
        while time.time() - t0 < at: time.sleep(1)
        try: fr = b("stillfrozen 200")["bodies"]
        except RuntimeError as ex: print(f"  still frozen at {at} s: no answer ({ex})", flush=True); break
        # a living creature's limbs are kinematic by design (the rabbits' 14 bones each)
        up = [x for x in fr if x["above"] > 0.3 and not x["locked"] and not x["held"] and not any(c in x["root"] for c in ("Nyul", "Ragdol"))]
        print(f"  still frozen at {at} s: {len(fr)} kinematic bodies within 200 m, {len(up)} of them more than 0.3 m above what is under them:", flush=True)
        for x in sorted(up, key=lambda x: -x["above"])[:25]:
            print(f"     {x['root']:<28} {x['body']:<24} {x['above']:7.2f} m up  {x['dist']:6.1f} m away  item {x['item']}  ground hold {x['groundHold']}  parent {x['parent']}", flush=True)
    for c in ("loadfreeze", "unfreezeground"):
        try: print(f"  {c}:", b(c), flush=True)
        except RuntimeError: pass
    return worst


with game.ProtectedSession():
    copied = []
    try:
        for f in files:
            d = game.SAVES / f.name.replace(f"_{a.name}.", "_loadfreeze."); shutil.copy2(f, d); copied.append(d)
        game.launch()
        results = []
        if a.ab: results.append(("off (before)", watch("fixes off (before)", False)))
        results.append(("on", watch("fixes on", True)))
        on = results[-1][1]
        print(f"\n{'PASS' if on <= MAX_MS else 'FAIL'}: worst vehicle {on:.1f} m/s with the fixes on (limit {MAX_MS})", flush=True)
    finally:
        game.quit_game()
        for p in copied: p.unlink(missing_ok=True)
