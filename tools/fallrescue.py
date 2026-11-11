#!/usr/bin/env python3
"""Fixes.FallRescue in the game: the nearest loose item put 30 m under the ground, falling (bridge `fallrescue test`),
with the rescue off (it keeps falling) and on (back on the surface within a second or two). Desktop only; the player's
saves are backed up and put back (ProtectedSession).

  tools/fallrescue.py SAVEDIR NAME"""
import sys, time, shutil, pathlib
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import game

src, name = pathlib.Path(sys.argv[1]), sys.argv[2]
files = [f for f in src.iterdir() if f.name.startswith((f"TLDsaveData_{name}.", f"TLDsaveProp_{name}."))]
ok = True
with game.ProtectedSession():
    copied = []
    try:
        for f in files:
            d = game.SAVES / f.name.replace(f"_{name}.", "_fallrescue."); shutil.copy2(f, d); copied.append(d)
        game.launch()
        b = game.loadsave("fallrescue"); time.sleep(40)
        for on in (False, True):
            b(f"set TLDRevamp.Fixes.FallRescue.Enabled {on}")
            before = b("fallrescue")["rescued"]
            b("devspawn 60"); time.sleep(3)   # a can by the player: the nearest loose item (a shop's sausage snapped back)
            print(f"rescue {on}: dropped", b("fallrescue test"), flush=True)
            time.sleep(3)
            st = b("fallrescue")
            print(f"  after 3 s: {st}", flush=True)
            under = st.get("testUnder")
            if on and (st["rescued"] < before + 1 or under is None or under > 0): ok = False; print("  FAIL: not put back", flush=True)
            if not on and (under is None or under < 5): ok = False; print("  INVALID: with it off it should still be under the ground", flush=True)
        print("PASS" if ok else "FAIL", flush=True)
    finally:
        game.quit_game()
        for p in copied: p.unlink(missing_ok=True)
