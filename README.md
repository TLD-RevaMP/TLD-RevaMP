# TLD RevaMP

TLD RevaMP makes **The Long Drive** run better and lets you play it **with your friends, in the same world**.

It is a mod for the PC (Steam) version of the game. It has two halves:

- **It makes the game faster.** Mirrors, shadows, grass and tiny distant parts cost the game a lot of frames. RevaMP
  trims that work back in ways that look normal while you play.
- **It adds multiplayer.** Not a separate lobby game with abstract cars: the other players are *there*. They drive
  past you, sit in your passenger seat, shoot at the same rabbits, open the same door, and their car falls apart in
  the same way yours does.

> **This is a work in progress.** Expect bugs. Back up your saves before you play. See
> [What to expect from a work in progress](#what-to-expect-from-a-work-in-progress) below.

## Before you start: what you need

| | |
| --- | --- |
| **The game** | You need your own copy of **The Long Drive** on Steam. RevaMP is only the mod — it never contains game files, so it cannot work without the game installed. |
| **The beta branch** | RevaMP only works on the game's **beta** branch. In Steam: right-click *The Long Drive* → **Properties** → **Betas** → choose **beta**. Let Steam finish updating. |
| **Your own saves** | Your saves are yours. RevaMP never touches them during install, update or uninstall. Still, back them up before playing a test build. |
| **No other mod loader** | There must be **no other mod loader** in the game folder. If an old TLDLoader or ModLoader is installed there, remove it first: two loaders fight over the same plugins and the game will not start. |

You also need the **same RevaMP version** as everyone else in a session. If two of you run different versions, the
game will say so and refuse the join. That is on purpose: two different builds would each see a slightly different
world.

## Installing it (the easy way)

The **Releases** page has everything you need:
<https://github.com/TLD-RevaMP/TLD-RevaMP/releases>

### Windows

1. Download **`install.ps1`** from the release page.
2. Right-click it → **Run with PowerShell**.
3. It asks what you want to do: **install or update**, **repair**, or **uninstall**. Pick *Install or update*.
4. It shows you exactly what it will download (with a checksum for each file) and asks once before it changes
   anything. Say yes.
5. If the game is somewhere unusual, run the installer from a PowerShell window and give it the folder with
   `-GameDir` (`powershell -ExecutionPolicy Bypass -File .\install.ps1 -GameDir "<game folder>"`), or install from
   zips you already downloaded with `-From <folder>`. If it says the game is running, close the game and run it
   again. If it says the SHA-256 does not match, the file you downloaded is not the one this installer was written
   for: download it again.

### Linux and Steam Deck

1. Download **`install.sh`** from the release page.
2. Open a terminal and run `bash install.sh`. On the Steam Deck, use Desktop Mode. If the game is somewhere
   unusual, run `bash install.sh --game-dir "<game folder>"`; to install from zips you already downloaded, add
   `--from <folder>`.
3. Same menu as Windows: **install or update**, **repair**, or **uninstall**.

### What the installer does, in one paragraph

It finds your game through Steam's own list of libraries, downloads the mod (and the BepInEx loader it needs) and
checks each download against a checksum written into the script, so a wrong or corrupted file stops the install
before anything changes. Then it copies the files into your game folder, saves a backup of anything it replaces, and
on Linux also tells Proton to load the right `winhttp.dll` for this game only. It never touches your saves, your
settings or other games, and it needs no administrator or root rights.

### If you would rather do it by hand

Download the **`TLD-RevaMP-<version>.zip`** from the same page and unpack it into your game folder (Steam → right-click
*The Long Drive* → **Manage** → **Browse local files** shows you where that is). `winhttp.dll` has to end up next to
`TheLongDrive.exe`. On Linux and the Steam Deck you also need this launch option, unless the installer set it for
you:

```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

Steam → *The Long Drive* → **Properties** → **General** → **Launch options**. The zip also contains `INSTALL.txt`
with the same steps.

### Is it installed?

Start the game. Its main menu has a **Mods** button between Settings and Exit: that is your proof it loaded.
**Page Up** shows a small overlay in the top-left corner with the RevaMP version and the frame rate.

### Updating later

Download the new release and run its installer again, choosing *Install or update*. Or close the game and copy the
new zip's files over the old ones. Your saves and settings stay where they are. **Tell everyone in your session to
update too** — you cannot join each other across versions.

### Removing it

Run the installer and choose *Uninstall*. It removes exactly the files it added and puts back the ones it replaced.
Your RevaMP settings, logs and reports stay behind on purpose.

## Your first multiplayer session

Multiplayer uses Steam's own networking, so there is **no port forwarding** to set up and no server to rent. If you
are friends on Steam, you can play.

### Hosting

1. Start or load your world, then press **F11** (or use the game's own **Multiplayer** button in the pause menu or
   the main menu).
2. Press **Host**. A password is optional — if you set one, your friends have to type it.
3. Your friends now see your game in their multiplayer menu.

### Joining

1. Press **F11** (or the game's **Multiplayer** button).
2. Pick the friend who is hosting and press **Join**. If they set a password, type it.
3. Your game loads **their** world. Your own saves are not touched. The host's machine owns the world and the save.

While the join loads, the screen is covered by a muted loading panel that shows each stage and how many objects have
arrived. It waits until the ground under you and the objects around you are really there, so you do not fall through
the world or land next to a car that is not there yet. **Escape** cancels the join. If it has not moved for 60
seconds, you get a **Continue anyway** button as well.

### While you play

| | |
| --- | --- |
| **Text chat** | Press **Enter**. |
| **Voice** | Hold the game's **Voice chat** key (**K** if you never set one). Only players within about 60 m hear you. Hold **O** (Settings → Revamp → *Talk to everyone key*) to talk to every player, wherever they are. A host can make every voice reach everyone: Session settings → *Voice chat: Everyone hears everyone*. The microphone is the one Steam has chosen. |
| **The player list** | Shows who is in the game and their ping. The host can **mute** and **kick**. |
| **Teleport** | Type `/tpa <name>` in the chat to ask another player to teleport to you; they answer `/y` or `/n`. The host can turn this off. |
| **Hardcore** | Settings → Revamp → *Hardcore* turns it on in single player and for the games you host; while hosting, Session settings → *Hardcore* does the same. In multiplayer the host's setting counts. Then nobody can open the F1 developer menu (the host neither), faucets and water towers are empty, a part a crash knocks off falls, can't be put back on and is gone a few seconds later, and cleaning tools clean nothing. Faucets get their water back when Hardcore goes off. |
| **Performance overlay** | **Page Up** shows and hides the overlay with the frame graph. It starts hidden. |
| **Bug report** | **Ctrl+Insert**, or `/bug` and a short note in the chat: see *Reporting a problem* below. |

### What you can see of each other

Players (walking, sitting, driving, riding, their footsteps), cars (driving, crashing, losing parts, fuel and oil),
loose items (picking up, throwing, dropping), doors and gates, animals (hunting, attacks, bodies), guns, bullet
holes, explosions, sandstorms, tumbleweeds and the radio. You can stand on a moving bus, or on someone else's car.

You do **not** have to be near each other. Every machine simulates the cars, items and animals near *its own* player,
so you can drive off in opposite directions and still both be in the same world.

## The world's shape

A new game can be generated in the shape the game ships with (**Crossroads**: a main road with side roads crossing
it), with only the main road (**Main road only**: one unbroken road, no crossroads), or in the **Old terrain (2023)**:
the hills, the one road and the roadside buildings of the public 2023 build, made by the 2023 build's own code. That
needs the 2023 game files once, from Steam's console (`steam -console` or `steam://open/console`):
`download_depot 1017180 1017181 7731485613881054580`; RevaMP checks and keeps them, and never ships them.
Pick it in the new-game screen's **Terrain** dropdown; it is not in Settings. A save keeps the shape it was made with,
and when you join somebody else's world you play on the host's shape, so everybody drives the same roads. See
[docs/TERRAIN-MODES.md](docs/TERRAIN-MODES.md).

## When something goes wrong

These are the ones people hit most often.

**"The host plays TLD RevaMP X, you Y. Both need the same version."**
You are on different versions. Update to the newest release on both machines. Download the newest one from the
Releases page, run the installer, and pick *Install or update*.

**"… different builds of the same version. Both need the same build."**
Same version number, different code — usually one of you has a test build and the other has a normal release. Get the
same release on both machines.

**"This game has a password." / "Wrong password."**
Type the host's password. If you did not know there was one, ask the host.

**"The game is full (16 players)."**
The session is full. Ask one of the players to leave, or start another game.

**The join hangs on the loading screen.**
It is waiting for the world. Give it a moment. After 60 seconds without progress you can press **Continue anyway**.
If the ground never arrives, ask the host to save and reload, or leave and rejoin.

**The game starts but RevaMP does not load (no Mods button on the main menu).**
Most often the game is on the wrong branch, or another mod loader is installed. Check that Steam is on the **beta**
branch, and remove any old TLDLoader or ModLoader install from the game folder. On Linux, check that the Proton
override or the launch option is set.

**Everything looks wrong — cars in the wrong place, doors on the wrong car, an item where it should not be.**
That is a bug. Press **Ctrl+Insert** right away (see below) and report it.

**You want to undo everything.**
Run the installer again and choose *Uninstall*, or *Repair* if you think a file got corrupted or half-copied.

## Reporting a problem

**Do not open a public issue for a crash you cannot reproduce without a file.** The right route is:

1. **Press Ctrl+Insert** (either Ctrl) in the game right after the problem. Write what went wrong in the box that
   opens, if you like, and press **Enter**. Or type `/bug` and a short note in the chat (it is never sent as chat).
   That saves `TLDRevamp-bug-<date>-<time>.zip` (and a smaller `TLDRevamp-report-<date>-<time>.zip` next to it) in
   `BepInEx\tldrevamp-feedback\` in your game folder. The bug zip holds the
   recent game state, performance and network numbers, your numeric settings, your hardware models, and snapshots of
   the car you sit in, the item you look at and the parts near you. When you host or play alone, it also holds your
   world save. Screenshots, raw logs, player names and file paths are left out, and your note is cleaned before it
   is saved. To include what you did before the bug in a multiplayer game, turn on *Record actions for bug reports*
   (Settings → Revamp → Multiplayer, off by default) before the session starts.
2. **Describe the bug** in a GitHub issue: what you did, what you expected, what you saw instead, whether you
   hosted or joined, and your RevaMP version. That is all we need to start.
3. **Keep the report zips on your own computer.** Do not attach them to the issue, and do not upload them anywhere else.
   GitHub issues are public. If we need the file, we will ask you for it privately.

Report bugs here: <https://github.com/TLD-RevaMP/TLD-RevaMP/issues>

**Scroll Lock** opens *marks*: press it, then **Enter** to save a note about what you were looking at (with a bit of
game state attached), **Escape** to cancel. You can browse the session's marks afterwards and
rename your own notes in the panel. In a release, marks and reports stay on your computer: they are never shared
with the other players. Dev builds can share them (**Settings → Revamp → Debugging**, off by default).

**Cinematic camera:** a separate camera for filming, in free (fly) and orbit modes, with a bounded speed, field of
view and orbit distance. It is **unbound by default** — assign `CinematicCamera` in `BepInEx/config/tldrevamp.core.cfg`
(or Settings → Revamp → Keys) to open it. It never touches the game's own camera, never moves your character
and never touches the network: what the other players see stays exactly the same. While it is on, W A S D fly the
camera instead of walking you, Q and E change the height, the mouse or the arrow keys look, Shift hurries and Ctrl
creeps. In orbit mode the camera circles a point in front of you: the mouse turns it around the point, W and S move
it nearer or further, and Q and E raise or lower the point. It can also play a shot list (a text file of moves, cuts
and speeds). See [the cinematic camera](docs/CINEMATIC-CAMERA.md) for the controls, the bounds, the shot-list format
and the tests. A small status line (mode, field of view, controls) stays visible even when the performance overlay is
hidden. The camera is put away by itself when you die, when the world is loaded or replaced, or when you are in VR.

**Replays:** record what happens around you and watch it again with the filming camera, like a replay mod.
**Ctrl+F9** starts and stops recording (or Settings → Revamp → Filmmaking): every player's body and every car, part and
item within about 250 m, 20 times a second, in single player and multiplayer, with nothing sent to the other players.
**Ctrl+F10** plays the latest recording. The world and your character stay where they are; the recorded things are
drawn in their place until you leave. Space pauses, the arrow keys jump 5 s and change the speed (0.1× to 4×), 1 to 4
pick the free, orbit, eyes and follow views, Tab picks the player to watch, and Alt + click scrubs the timeline.
Recordings are saved in `BepInEx/tldrevamp-replays` and hold numbers only. Headlights and lamps are recorded, and animals
and bodies within about 120 m; sounds and smoke are not yet. See [replays](docs/REPLAY.md).

**Only in dev builds.** The release for players leaves out the developer tools below: its Settings → Revamp has no
Debugging section, the background checks are off, and it has no debug report (**Page Down**), item health
(**Home**), debug layout (**Right Shift**) or item traffic (plain **Insert**). Dev builds have them;
[docs/DEVELOPERS.md](docs/DEVELOPERS.md) says how to build one.

**Debug tools:** Settings → Revamp → Debugging has a set of diagnostic overlays and tools (ownership labels, ghost
boxes, link lines, live comparison between players, network-condition presets and a repro recorder). They are all off
by default and are for people chasing a specific bug. It starts with **Debug view**: Off (default), Focus (one target
card), Problems (Focus plus faulty-object tags), Everything (all nearby diagnostics). Bind `DebugView` in
`tldrevamp.core.cfg` to cycle modes; `net debugview off|focus|problems|everything|state` selects one from the bridge
and `net compare all|differences` changes comparison detail. What each one does, and what its limits are, is in
[docs/DEVELOPERS.md](docs/DEVELOPERS.md#reports-marks-and-the-debug-tools) and
[docs/TELEMETRY.md](docs/TELEMETRY.md).

**Debugging V2:** set Settings → Revamp → Debugging → **Version** to V2 for one **Debug overlay** switch instead: the
performance graph, the network packets panel, the focus card, owner ghosts and links, and one tab directly above each
nearby shared object with everything about it (owner, simulated or copy, sync error, its network use, its colliders and
fitted parts that can't be reached). **Right Shift** (`DebugLayout` in `tldrevamp.core.cfg`) edits the layout: every panel
and every line of it on or off (the FPS line, the key hints, a panel's title…), drag the framed panels (item health too) onto
a grid (Options: 5, 10, 20 or 40 px, or off), each panel's border
(off, plain or coloured) and its border and title colours (a palette or RGB), and **custom panels** made of fields you pick
(fps, lows, p99, ping, network, speed, position, the aimed object…). Legacy keeps its own switches untouched; going back
restores them.

**Item health:** aim at a shared object (or sit in a car, or hold an item) in a multiplayer game and press **Home**
(`ItemHealth`): every check of it with ✓, ! or ✗ — loaded here, owner, position, held/stored/attached/locked and its slots
against every other player's view (asked through the host), its body and colliders, fitted parts you can't reach,
refusals and corrections — why each problem is one, and the recent events that led there. Home again closes it.

**Found on their own:** in a multiplayer game RevaMP also runs these checks by itself, so a bug doesn't need someone to
notice it first. The shared objects near you get item health's checks; every loaded object gets checked for what can never be
right (a position or speed that isn't a number or is far beyond anything the game does, an object under the ground with
nothing below it, a part and its slot that disagree, an owner who has left). Loose items you can see but can't pick up,
and fitted parts you can't open or take off, are caught too, and so is a copy the census keeps failing to repair. A
problem that lasts is written to the session log and saved as an automatic mark (both machines' state at that moment,
in your next bug report), and the Debugging V2 tab over the object says `health: …` in red until it clears.

If you are not sure whether something is a bug or just a work in progress, ask on the issue tracker before writing a
long report. There is no shame in asking.

## What to expect from a work in progress

This is an **unfinished mod**, and it is being developed in the open.

- **Bugs are normal.** Some things will be wrong: a car slightly out of place for a moment, a door that does not
  line up, an item that lands oddly after you drop it. Press Ctrl+Insert and tell us.
- **Things can change between versions.** We are still changing how objects are shared, how the game is patched and
  what the settings look like. Your settings may need resetting.
- **Everyone must be on the same version.** We cannot support a session where half the players are on an older
  build, because the two builds genuinely disagree about the world.
- **Some parts are still being built.** The full list of what is shared today, and what is not, is in
  [docs/DEVELOPERS.md](docs/DEVELOPERS.md#what-is-shared).
- **Performance work is measured, not guessed.** Every performance option has a default that was checked in game so
  that it looks normal while you play. If something looks wrong, a setting is probably the reason — **Settings →
  Revamp** has a **Reset** button that puts every option back to its recommended default.

We would rather you have a rough but working version than a perfect one that never ships.

## Add-ons

RevaMP has a few optional mods of its own. They are not in the RevaMP zip: you install them from the mod list in
the game. Their source and releases are in [TLD-Mods](https://github.com/TLD-RevaMP/TLD-Mods) (World Map has a
repository of its own):

| Add-on | What it does |
| --- | --- |
| **Head Unit** | A digital head unit that fits some cars, with a radio and a YouTube queue everyone in the car shares. |
| **Car Trailer** | A trailer for carrying a car, shared between players like the game's own trailer. |
| **Trailer Hitches** | Receiver hitches for cars that cannot take the game's stock trailer drawbar. |
| **World Map** | A carryable paper map with explored routes, visited places and live player markers. |
| **TV Browser** | A web browser on the in-game TV, used with the keyboard and mouse; everyone in a session sees the same page. |
| **Road End** | Picked on the new-game screen: the main road ends after 5000 km of it, at the game's roadside grave, with a note. The land and side roads go on. |
| **BusGut**, **MountAnything**, **TerrainChoice** | By YujiroX2: strip the buses out for cargo, mount held items anywhere, and pick the terrain for a new game. |

The **Mods** button on the game's main menu, between Settings and Exit (also in the pause menu), opens the mod list.
It has two tabs, **All mods** and **Installed**, a search box and a sort (*Most downloaded*, *Recently updated*,
*Name*). Each row shows a mod's author, state, description, tags, downloads, last update, size and version, and its
buttons: **Install** (or **Update**), **Disable** and **Enable** to turn an installed mod off and on without
uninstalling it, **Remove** to take it away again (it asks first), and **Cancel** while it downloads. Changes take
effect at the next start, so restart the game. A mod you installed by hand shows **By hand**: you update and remove it
yourself. When the main menu first shows, RevaMP checks the catalog for newer versions of your mods and, if it finds
any, asks *Update now?* (*Check for mod updates at start* in Settings → Revamp turns that off).

The live catalog (`packaging/mods.json` on dev) is the mod list. New catalog releases become available without a
RevaMP update. Only GitHub release assets are accepted, and each downloaded DLL must match the catalog's SHA-256.
Invalid entries show why they cannot be installed. Mods installed by hand load as usual.

When joining with missing or different mods, a screen lists what you need and what to remove. Install individual
mods or **Install all** from the catalog, then restart the game and join again. Only the identical DLL hash the
host uses is offered. **Copy list** copies names, versions and catalog source links. The host never sends files.

Listing copy for posting these on Nexus Mods, plus the upload checklist, is in [docs/NEXUS.md](docs/NEXUS.md).

## Where things live in the game folder

After the install, the mod's files are in your game folder. You rarely need them, but when a problem is reported
these are the folders that matter:

| Folder | What it is |
| --- | --- |
| `BepInEx/plugins/TLDRevamp/` | the mod itself |
| `BepInEx/patchers/TLDRevamp/` | the preloader, the piece that activates the mod |
| `BepInEx/TLDLoaderMods/` | your TLD Loader mods, if you use any; RevaMP runs them |
| `BepInEx/tldrevamp-feedback/` | the bug reports the mod saves when you press **Ctrl+Insert** (or type `/bug`) |
| `BepInEx/disabled-mods/` | the mods RevaMP switched off for you (never deleted, only moved aside) |
| `BepInEx/plugins/TLDRevampMods/` | the mods RevaMP manages for you |
| `BepInEx/licenses/` | the licence texts of the bundled third-party code |

## Other things you may want

- **The mod's own settings**: **Settings → Revamp**, a tab in the game's own settings menu. Everything is explained
  where you change it, and there is a **Reset** button.
- **The technical reference**: what the installer does, how the multiplayer works, how to build and release the mod:
  [docs/DEVELOPERS.md](docs/DEVELOPERS.md). Building from source is two commands:

  ```sh
  tools/libs.sh          # copies the game's and BepInEx's reference assemblies into lib/ (never committed)
  tools/package.sh       # builds and writes dist/TLD-RevaMP-<version>.zip
  ```
- **Modding**: the multiplayer API other mods are built on: [docs/MODDING-MULTIPLAYER.md](docs/MODDING-MULTIPLAYER.md).
- **Contributing**: how to help: [docs/CONTRIBUTING.md](docs/CONTRIBUTING.md).

## Credits and licence

RevaMP bundles BepInEx 5 (MIT) with its HarmonyX, MonoMod and Mono.Cecil (MIT) and UnityDoorstop (LGPL-2.1)
unmodified, each under its own licence (the texts ship in the release zip as `BepInEx\licenses`). The FSR 3.1 native AA
option is AMD FidelityFX FSR 3.1 (MIT) through Nico de Poel's FSR3Unity (MIT), built into `TLDRevamp.dll`; its shaders
are rebuilt with `tools/fsr3bundle`. The Long Drive is by
Gab and Genesz. RevaMP is an unofficial fan project, not made, endorsed or supported by them; it contains no game
files or game code, and every player needs their own copy of the game. Thanks also to Killenger for his
TLDAutopilot mod, used in testing.

The trailer-creep fix, trailer brakes and the optional driving assists follow ideas from *Long Haul Overhaul* by
QuantumRage & Wisher; they are written anew here and no code from it is included.

**This Mod was created using AI.**

Copyright (c) 2026 Normal99. TLD RevaMP is free software under the **GNU General Public License, version 3**
(GPL-3.0): see [COPYING](COPYING). If you share RevaMP — changed or not — you share its source under the same
licence. That includes other mods: a mod that uses RevaMP's code, such as the multiplayer mod API in
`TLDRevamp.Multiplayer.dll`, must be released under GPL-3.0 or a GPL-3.0-compatible licence (see
[docs/MODDING-MULTIPLAYER.md](docs/MODDING-MULTIPLAYER.md)). The Long Drive itself, BepInEx, HarmonyX and
UnityDoorstop keep their own licences; none of their code is in this repository.
