#!/usr/bin/env python3
"""A long session, hosting alone (or not, --solo): does the frame get slower the farther the world has been travelled?
Volkan, 2026-11-11: ~100 fps in the desert at the start of a save, ~70 a few hundred km later, only when hosting.

A seeded new world; at a spot along the main road (A) the frame is measured (fps, Unity's frame phases, the mod's and
the game's heavy steps, what can pile up: meshes, objects, buildings placed, the managed heap, shared objects); then the
road is travelled in --step km hops for --km; measured there (far); then back at A and measured again (A again). A
against A again is the same view of the same place: what differs is what the session gathered on the way.
Desktop only; the player's saves are backed up and put back (ProtectedSession).

  tools/longhost.py [--km 300] [--step 3] [--solo] [--secs 20] [--seed 20261111]"""
import sys, time, math, json, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game
from bench import Bridge
from roadkit import tp

def arg(n, d, c=str): return c(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
KM, STEP, SECS, SEED = arg("--km", 300.0, float), arg("--step", 3.0, float), arg("--secs", 20, int), arg("--seed", 20261111, int)
SOLO = "--solo" in sys.argv
TIMERS = ("TLDRevamp.Net.Mp.Tick TLDRevamp.Net.Entities.FixedTick TLDRevamp.Net.Entities.LateSample poiGenScript.Upd "
          "mapSettingScript.Update itemPlaceRemoveScript.RemoveStuff itemPlaceRemoveScript.PlaceStuff terrainGenScript.Update roadGenScript.Update")


def look(b, yaw):
    """the same view each time: along the road, level"""
    for path, v in (("mainscript.s.player.rotY", yaw), ("mainscript.s.player.FxRot", 0), ("mainscript.s.player.FyRot", 0)):
        try: b(f"set {path} {v}")
        except RuntimeError: pass


def measure(b, label):
    time.sleep(8)   # what the hop placed settles
    b(f"timers on {TIMERS}"); b("loopprof start")
    wins, last, t0 = [], None, time.time()
    while time.time() - t0 < SECS + 5.5:
        r = b("timers show 12")
        if last and r.get("frames", 0) < last.get("frames", 0):
            wins.append(last)
            if time.time() - t0 >= SECS: break
        last = r; time.sleep(0.4)
    lp = b("loopprof stop 6"); b("timers off")
    frames = sum(w["frames"] for w in wins) or 1
    fps = frames / max(1, 5 * len(wins))
    steps = {}
    for w in wins:
        for t in w.get("top", []):
            a = steps.setdefault(t["m"], [0.0, 0.0]); a[0] += t["msPerFrame"] * w["frames"]; a[1] = max(a[1], t["maxMs"])
    sc = b("scenecount")
    row = {"label": label, "fps": round(fps, 1), "frameMs": round(1000 / fps, 2) if fps else None,
           "steps": {m: [round(a[0] / frames, 3), round(a[1], 1)] for m, a in sorted(steps.items(), key=lambda kv: -kv[1][0])[:9]},
           "phases": lp.get("top") or lp.get("phases") or lp,
           "counts": {k: sc.get(k) for k in ("meshes", "transforms", "pois", "poiChunks", "heapMB", "shared", "rigidbodies", "renderers", "renderersVisible", "particleSystemsPlaying")}}
    print("  " + json.dumps(row), flush=True)
    return row


def hop(b, rp, f, metres):
    """along the road from road point rp, heading f: the road's point there (a hop off a bend is tried shorter)"""
    for m in (metres, metres / 2, metres / 4, metres / 8):
        tp(b, rp["gx"] + f[0] * m, rp["gz"] + f[1] * m, timeout=60); time.sleep(5)
        try: return b("roadpoint")
        except RuntimeError: pass
    tp(b, rp["gx"], rp["gz"], timeout=60); time.sleep(3)
    return b("roadpoint")


rows = []
with game.ProtectedSession():
    try:
        game.launch(); game.newgame(SEED); b = Bridge()
        b("set TLDRevamp.TestSafety.BlockAutoSave True"); b("set kaposztaleves.s.settings.god True")
        b("bg on"); b("set UnityEngine.QualitySettings.vSyncCount 0")
        if not SOLO: print("  hosting alone:", str(b("mp host 27070"))[:80], flush=True)
        rp = b("roadpoint")
        a = hop(b, rp, (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"]))), 3000)
        tp(b, a["gx"], a["gz"], timeout=60); look(b, a["yaw"])
        rows.append(measure(b, "A start"))
        last, heading, t0 = a, (0.0, 0.0), time.time()
        stops = int(KM / STEP)
        for i in range(stops):
            try: rp = b("roadpoint"); last = rp
            except RuntimeError: rp = last
            f = (math.sin(math.radians(rp["yaw"])), math.cos(math.radians(rp["yaw"])))
            if i > 0 and f[0] * heading[0] + f[1] * heading[1] < 0: f = (-f[0], -f[1])
            heading = f
            hop(b, rp, f, STEP * 1000)
            if (i + 1) % 25 == 0: print(f"  {(i + 1) * STEP:.0f} km, {(time.time() - t0) / 60:.1f} min, shared {b('scenecount').get('shared')}", flush=True)
        far = b("roadpoint"); tp(b, far["gx"], far["gz"], timeout=60); look(b, far["yaw"])
        rows.append(measure(b, f"far ({KM:.0f} km)"))
        tp(b, a["gx"], a["gz"], timeout=90); time.sleep(20); look(b, a["yaw"])
        rows.append(measure(b, "A again"))
        if not SOLO: b("mp stop")
    finally:
        game.quit_game()
print("\nLONGHOST " + ("solo" if SOLO else "hosting"))
s, e = rows[0], rows[-1] if len(rows) > 1 else rows[0]
print(f"  A start {s['fps']} fps → A again {e['fps']} fps ({(e['fps'] - s['fps']) / max(s['fps'], 1) * 100:+.0f}%)")
for k in s["counts"]:
    print(f"  {k}: {s['counts'][k]} → {e['counts'][k]}")
