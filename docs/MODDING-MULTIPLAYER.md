# Making other mods work in multiplayer (outline)

> **Status:** every step of section 9 is built. `TLDRevamp.Multiplayer.dll` (API 4) has registration, records,
> edits, ownership hooks, events, a shared clock, pose sync, state records sent by distance, the far store,
> session-wide settings and replay channels; the session side is `src/TLDRevamp/Net/ModObjects.cs`. Mods must match at join (section 3a)
> and a joiner installs missing mods from the catalog; the host never sends files. The Head Unit and Car Trailer example mods ([TLD-Mods](https://github.com/TLD-RevaMP/TLD-Mods)) run in two-machine
> tests, and `tools/modapi2.py` (with the test mod `src/Mods/ApiProbe`) tests API 2. The Loader-mod scanner
> (section 7) writes a report and a code skeleton.

## 0. Using it

A mod references `TLDRevamp.Multiplayer.dll` (shipped next to `TLDRevamp.dll`). It has no other dependency than
Unity's core module, so a mod built against it loads without TLD Revamp; every call then works on this machine only.

**Licence.** TLD Revamp, `TLDRevamp.Multiplayer.dll` included, is GPL-3.0 ([COPYING](../COPYING)). A mod that
references it or uses TLD Revamp's code must be GPL-3.0 compatible: release it under GPL-3.0 (or a licence that
can be combined with it) and give its source to the people you give the mod to. Closed-source mods can't use this
API. The same goes for the skeleton the Loader-mod scanner writes (section 7) once it is filled in and shared.

```csharp
using TLDRevamp.Multiplayer;

class BeaconType : SharedType
{
    public override string Name => "beacon";
    public override GameObject Create(RecordReader r, Vector3 pos, Quaternion rot)
    {
        var go = MakeBeacon(pos, rot);   // the mod's own prefab
        Read(go, r);
        return go;
    }
    public override void Write(GameObject go, RecordWriter w) { w.Color(go.GetComponent<Light>().color); }
    public override void Read(GameObject go, RecordReader r)  { var c = r.Color(); if (r.IsValid) go.GetComponent<Light>().color = c; }
    // the owner decides on changes asked by others; returning true sends the new record to everyone
    public override bool OnEdit(SharedObject o, RecordReader edit, int from)
    {
        var c = edit.Color(); if (!edit.IsValid) return false;
        o.GameObject.GetComponent<Light>().color = c; return true;
    }
}

// once, at startup
var mod = MpApi.RegisterMod("beacons", "Beacons", "1.0.0");
var beacon = MpApi.RegisterType(mod, new BeaconType());
// when the player places one
var shared = MpApi.Share(beacon, go);
// any player recolours it (applied by the owner)
shared.RequestEdit(w => w.Color(Color.red));
```

What the session does for a shared object: a network id, one owner (the creator; the host when they leave; another
player through `RequestOwnership` if the type allows it), copies on every other machine built by `Create`, joiners
get it from the host's latest record, removal everywhere. `SyncPose` types get their position sent by the owner and
played back smoothly on copies (which are kinematic). Objects parented to something else (a part in a car) are not
moved by the session: they ride their parent. `MpApi.WorldObjectId` / `FindWorldObject` let a record point at one
of the game's shared objects (a car). `MpApi.SimulatedHere` (API 5) says whether this machine runs an object's game logic: true outside a
session and for objects owned here, false for a copy of another player's car, so logic that changes saved state (a part
wearing) runs only where it counts. `MpApi.SessionTime` is the host's clock on every machine, for things that must
happen at the same moment everywhere (a video's position). `MpApi.SendEvent` / `OnEvent` carry short messages that
are not object state.

Limits, checked by the host before anything is passed on: records up to `MaxRecordBytes` (8 KB by default), edits
and events up to 1 KB, 60 messages a second per player (bursts of 120), 1,000 objects per player, finite positions,
records and poses only from the current owner. Bridge: `mp mods` (counters, objects). Reflection paths name the real member
owner; `switches` shows the paths for runtime switches.

Two things about this game that every BepInEx mod meets (found with the head unit example mod):
- The game destroys BepInEx's manager object when its scene loads, so a plugin's own `Update`, `OnGUI` and the like
  never run. Put per-frame work on a `GameObject` of the mod's own, made in `Awake` with `DontDestroyOnLoad`.
  TLD Revamp and the example mods do this.
- No camera is tagged MainCamera (`Camera.main` is null). The player's view is `mainscript.s.player.Cam`.

## Ragdolls

Game-style ragdolls with `tosaveitemscript.ragdollPos` need nothing. RevaMP uses the game's limb list.
For a custom ragdoll with its own list, call `MpApi.RegisterRagdoll(root, limbs, isLimp)` (API 3) when it is
created on each machine, before or after sharing it. `root` is the object RevaMP syncs, with the item's
`tosaveitemscript`. This registers the limbs; the object must still be shared through the game's save system.

Pass 1-63 limb transforms, with no nulls or duplicates. Their order must match on every machine: use the same
prefab and mod version. RevaMP sends only the limbs that moved. It checks a hash of the limb names so a
mismatched hierarchy is ignored rather than moving the wrong limbs. Limbs are sent while limp: `isLimp` returns
whether they move on their own. Leave it null for always limp.

The API stores a copy of the array. Call again to replace it, or `MpApi.UnregisterRagdoll(root)` to remove it.
Destroyed roots are removed lazily. Registration works without a session or TLD Revamp installed.

## Replay channels

A mod's own state can be in TLD Revamp's [replays](REPLAY.md) (API 4). Register a channel once, in `Awake`:

```csharp
var ch = MpApi.RegisterReplayChannel(mod, "screen");   // "tvbrowser/screen" in the file
ch.RecordingStarted += () => { /* record every object's current state once */ };
ch.Shown += k => { /* k.Copy: the object's copy; k.Data: the latest key at the playhead; k.CopyOf(liveRenderer) */ };
ch.PlaybackEnded += () => { /* free what Shown made */ };
// while ch.Recording: ch.Record(gameObjectOfAnItem, bytes) whenever the state changes
```

A key is about a game item (one with `tosaveitemscript`, or a child of one) that the recording samples, at most
256 KB. Record only on change: the latest key at the playhead is what `Shown` gets, once each time it changes and
once when playback starts. `Shown` gets the copy (a GameObject the replay draws), the live object when it is in this
world, and `CopyOf(renderer)`, the copy's renderer for one of the live object's: set a material of the mod's own on
it (the copies share the game's materials). Nothing is sent to other players; replays are local. Example:
[`mods/TvBrowser/ReplayTv.cs`](https://github.com/TLD-RevaMP/TLD-Mods/blob/main/mods/TvBrowser/ReplayTv.cs) in TLD-Mods.
To still load with an older RevaMP, keep `RegisterReplayChannel` in a method of its own (`MissingMethodException`
there means no channels) and keep the API 4 types (`ReplayChannel`, `ReplayShown`) out of the mod's fields: with an
older RevaMP, a class with such a field fails to load before any catch in it can run. TV Browser keeps one in a field,
so it needs RevaMP 0.69.2.

## 1. Why this is needed

TLD Revamp shares the game's own things: cars, items, parts, building contents, doors and switches, the radio,
weather and time. It finds them through the game's save system: anything the game can save (a save script on the
object, with its save records) can be shared, because a save record already describes everything needed to build
the same object on another machine.

A mod's objects are usually invisible to that:

- objects a mod creates from its own assets have no game save script, so the session never sees them;
- objects that do use the game's save script but carry extra state in the mod's own components lose that state
  on the other machines (it isn't in any game save record);
- a mod that reacts to player input (a button, a key, a menu) runs only on the machine of the player who pressed it.

A mod that only changes a car's tuning (final drive, gear ratios, wheel positions, suspension travel, which wheels drive and
steer) needs none of this: the session shares those values for every car itself and keeps them with the world save
(`Entities.CarTune`, `CarTuneSave`). A wheel's radius, mass and grip follow its tyre part, which the records carry.

The plan: a small public API so a mod can say "this object is shared", "this is its state" and "this happened",
and the session does the rest (ownership, joiners, players far away, saving).

## 2. Principles

1. **No changes to our multiplayer code per mod.** A mod registers; it never patches or calls our internals.
2. **Same rules as the game's objects.** A registered object gets what a car or an item gets: one owner at a
   time who simulates it, copies everywhere else, hand-off when the owner leaves, a place in the far store when
   no player is near, a record for players who join later.
3. **State travels as records.** A mod describes its state as a record (a plain serialisable class), the same way
   the game describes a car part or a tank. Records are versioned and size-capped.
4. **Optional.** A mod that doesn't use the API keeps working as it does today (local only).
5. **Safe by default.** Every record and event from another player is checked (size, version, rate, value ranges)
   before the mod sees it, as our own messages are today. A broken mod can't end a session.

## 3. Registering an object type as shared

- A mod registers a **type key** (its mod id plus a type name) and a **factory**: given a record, build the
  object here (from the mod's own prefab or assets).
- When the mod creates an object of that type in a session, it hands the object to the session, which gives it a
  network id and an owner (the creating player) and sends its first record to everyone near.
- Removal goes through the session too, so the object disappears everywhere (and from the far store), not only on
  one screen.
- Objects that come from the game's own save system need only the extra-state part (section 4): the session
  already shares them.

### 3a. Mods must match (built)

Every player's mods that use this API must be the same as the host's: the same mod ids, versions and DLL files
(SHA-256 of each mod's DLL, computed when it registers). The joiner's list rides in Hello.

Disabling a managed mod in the browser takes effect at the next start. Until then its already loaded code and
Hello registration remain active. After restart the DLL is outside `BepInEx/plugins`, never loads or registers,
and is absent from Hello. The ordinary missing-mod check still applies. If a required mod is installed but
disabled, the required-mods screen offers **Enable** for it; the player restarts the game and joins again.
The required-mods screen recognizes the multiplayer registration id even when it differs from the plugin GUID;
the browser records that association when queuing a change. No wire fields were added (protocol 45).

- **The host has a mod the joiner lacks, or another version or file of it:** the host sends `ModsNeeded`
  (`Net/ModsNeeded.cs`), then refuses the join with a readable reason. The mods screen fetches the same live catalog
  as the browser and offers only a release whose DLL SHA-256 equals the host's hash. The host never sends files.
  Install downloads from the catalog, verifies the bytes, saves them under `BepInEx/plugins/TLDRevampMods/` and sets
  an older managed copy aside (`.replaced`). Install all handles every installable row in turn. Restart the game,
  then join again: BepInEx loads plugins once. Disabled required mods offer Enable. Hand-installed copies must be
  replaced by hand. Mods absent from the catalog name their author as the download route. Copy list copies each
  name and version with its catalog source link, followed by problem lines.
- **The joiner has a mod the host doesn't, or a type version differs:** the screen includes the remove or update
  line, and the host logs `[anomaly] mods differ`.

The catalog is the list of installable releases. The DLL hash proves that the downloaded file matches that list
and the host's file. Hand-installed mods still load normally. A modified game can lie about its own hashes: this
is a consistency check, not anti-cheat.

Open questions:
- Whether a type can declare that it rides a parent (a part bolted to a car, cargo in a container), so it moves
  with the parent instead of sending its own states.

## 4. State as save records

Three kinds of state, matching what the session already does for cars and items:

| Kind | When it is sent | Example |
|---|---|---|
| **Full record** | on creation, to joiners, when a player arrives near it, after a hand-off | everything needed to build it |
| **State record** | at a fixed rate while it changes, by its owner | position, speed, a gauge value |
| **Edit record** | when a player changes something, from that player | a switch flipped, a dial turned |

- The mod provides: write record, read record, and (for state records) write and read state.
- The session provides: rates, interest (only players near get state records), the far store, and replay for
  joiners.

Built (API 2):
- **State records:** a type with `HasState` writes its state in `WriteState`. The owner calls it up to `StateHz`
  times a second (5 by default, at most 20) and sends it only when the bytes changed (`MaxStateBytes`, 256 by
  default). The host passes it to players within `StateRange` metres (500 by default; 0 = everyone) and keeps the
  latest, so a player who comes within range gets it then. Copies get it in `ReadState`.
- **The far store:** with `ViewRange` above 0, a player's machine has the object only while the player is within that
  many metres. At 1.25 times the range the copy goes (`OnStored`, which destroys it by default); the host keeps the
  record and the latest state. Back within range, the copy is built again from them (`Create`, then `OnPlaced`). An
  owner beyond the range hands the object to the nearest player within it, or to the host when nobody is near. If
  nobody is near at all, the host's copy is stored too, and the first player who comes near owns it. `ViewRange` 0
  (the default) keeps API 1's behaviour: everyone has every object.
- Records must be deterministic to read: the same record gives the same object on every machine. Random values
  (loot, wear) are chosen once by the owner and sent, never rolled again on each machine.

## 5. Events and ownership hooks

Callbacks a mod can implement, all optional:

- **Owner changed** (became owner / lost ownership): start or stop simulating; copies should not run physics or
  game logic that changes state.
- **Edit requested** (another player used the object): the owner decides and applies; the result travels as an
  edit record. Gives the mod a single place to validate input.
- **Replayed edits** (API 6, `SharedType.ReplayEdits`): an accepted edit normally sends the whole record to
  everyone. A type whose record is large and whose edits are small and frequent (a long queue beside a search bar
  being typed into) can turn this on: the owner sends the edit itself, and every copy runs the same `OnEdit` on its
  own copy. `OnEdit` must then change only what `Write` writes, the same way on every machine from the record, the
  edit, `fromPlayer` and `MpApi.SessionTime` (it holds the owner's time of the edit while `OnEdit` runs): no random
  values, no owner-only work (removing, events, sounds, other objects), no settings of this machine. Each copy checks
  its record against a hash of the owner's and asks for the whole record when it differs, so a mistake costs
  traffic, not a wrong copy; one that keeps differing is named once in the log. The whole record still goes every 64
  edits (or 32 KB of them) and after a change the owner made itself (`RecordChanged`). A player who becomes the owner
  first takes the host's record and the edits since it; edits that reach it meanwhile wait and follow, and its record
  then goes to everyone once. (An OnEdit that breaks the rule above can still lose its last edit when ownership moves
  at that moment: every machine ends the same, on the new owner's record.) On a TLD Revamp
  before API 6 the override is never read and the type sends whole records as before (`MpApi.ApiVersion` is a
  constant compiled into the mod: the API it was built against, not the one installed).
- **Placed / stored**: the object came into view on this machine or went into the far store.
- **Player joined / left**: for mods with session-wide state.

And one way to send a **mod event** (a short message to everyone or to the owner) for things that aren't object
state: a sound, an effect, a chat-like notice. Events are rate-limited and size-capped.

## 6. Session-wide settings

A mod with settings that must match for everyone (rules, difficulty, spawn rates) registers them so the host's
values are sent at join and applied for the session, the way the host's physics-lock setting is. Local-only settings
(graphics, keys) stay local.

Built (API 2): `MpApi.RegisterSetting(mod, name, get, apply)`. Values are strings of at most 1024 bytes, in a format
the mod chooses. The host checks its values about once a second and sends any that changed. A joiner applies the
host's value with `apply`, shows it as the host's while `SessionSetting.FromHost` is true, and gets its own value
back when the session ends.

## 7. A converter for existing TLD Loader mods

TLD Revamp already runs TLD Loader mods under BepInEx on the beta (the mod's lifecycle calls including DbLoad, its
logger, its config and assets folders, LoadAssets, and the public game's singleton names mainscript.M, itemdatabase.d
and savedatascript.d, which the preloader adds to the game's classes: TLDRevamp.Preloader.LoaderAliases, with
mainscript.seed/load/napszak/loaded/multi/world/mainWorld as instance fields Unity never saves, set from the beta's
world seed, its load flag (kept for the whole world: the game clears its own on the first frame), napszakvaltakozas.s,
whether the world is up with the mods' OnLoad done (false through OnLoad and that frame's Update), whether a RevaMP
session runs, a transform at the beta's world offset (world.InverseTransformPoint(p) is p's world position) and a
visszarako there whose coord is that offset (global = unity position - mainWorld.coord); and where a
game field is the same integer with the other sign on the beta, as idInSave (an int there, a uint here), ModHost points
the mod's own reference at the beta's type before loading it: LoaderRetarget, config Mods.RetargetLoaderMods.
That switch also enables public-name rewrites: fpscontroller.Tb -> BodyRot, radioscript.v -> Bvolume.tunedLerp,
carscript.lampak -> lightUsable.currentTurnState, carscript.KmhObj -> debugCanvas; fpscontroller.M -> mainscript.s;
menuhandler.DFMS and mainscript.DFMS
-> DataFromMenuScript.s; menuhandler.Settings and mainscript.SettingObj -> settingsscript.s; mainscript.GSettings
-> gamemodesettingsscript.s.S. Reads, writes and field addresses reach the beta's storage directly. Method adapters
also route mainscript.godmode/Showkmh and ChGodMode/ChKmh to kaposztaleves.s.settings.god/kmh, and
FoodCh/WaterCh/HpCh/PissCh/ShitCh to kaposztaleves.s.PressedFoodCh/PressedWaterCh/PressedHpCh/PressedPissCh/PressedShitCh.
The other method adapters cover RightHandRelease() (sync=true), UpdValues(bool) (the unused argument is dropped),
PressedEscape() (true,false),
both SwitchState overloads (null sender; the no-argument call advances the light state), and Damage/DamageInstant
(zero direction, preserving amount and show). What the public game's save and attach code was, TLDRevamp.LoaderCompat
answers from the beta's (config Mods.LoaderCompat): the spawner many workshop mods share (the first id free in
savedatascript.toSaveStuff and the records' nearStuff/farStuff, then tosaveitemscript.FStart(int)), removeFromMemory
(kept where the mod put it), pickupable.attach/cols/cols2, attachablescript.targetTosave/C/Attach(Transform,RaycastHit) and a store into
partslotscript.part (the slot's list then holds that part alone), tankcapscript.TCap/TOpen/TClosed,
GOpen/GClosed and UpdCap(bool) (kept for the mod to read back; the beta's cap turns by its usable, left as it is),
carscript.driverSeat (the seat marked driverSeat0) and ISwitches (the car's usables), pickupable.slots (the item's
tosaveitemscript.partslotscripts) and pickupable.Drop(bool) (Drop());
GetComponent of another type the beta dropped is none. The public game's parts are the beta's attachablescript: the
type partscript is renamed in the mod (its fields, locals and GetComponent<partscript> too), partscript.tosaveitem/p
-> attachablescript.tosave/tosave.P, tosaveitemscript.part and pickupable.part -> the item's attachablescript,
partslotscript.part -> part(); arrays the beta keeps as lists (tosaveitemscript.partslotscripts, partconditions,
tanks, usables, tankcapscripts, physlocks -> physLocks, colors, door_rots) read as a copy and store as a new list.
Of the workshop's 202 mods (2026-11-23), 41 named partscript. Buildings are the beta's points of interest:
buildingscript -> poiScript, itemdatabase.buildings -> pois, itemsSpawned -> the place has started (a mod
puts its items into buildings then); doublecoord -> UnityEngine.Vector3d. The world generator's
scattered prefabs (TerrainGenerator.TG.manager.settings.objGeneration.objTypes[i].prefab, and
TerrainGenerationSettings.staticReference): each step is the current map (menuhandler.s.currentMainMap), objTypes
its objGenScripts' prefabs (the same prefab objects), ObjClass -> objGenScript/prefabClass; the desert, mountain and
road generators have no aliases. Attach targets (hitches, plates, trunk mounts) are slots on the beta:
attachTargetscript -> partslotscript, its target/attachs -> T/parts, attachablescript.target -> slot and
tosaveitemscript.attachTargets -> partslotscripts. TerrainGenerator.roads -> the map's roadGens. Any other field the
beta dropped, of a type it has (attachablescript.lockRotation in a mod's item setup), is kept per
object for the mod alone (LoaderCompat.Kept/Keep) so the method naming it runs; the DLL's log line lists each one. A
game method's return of the other integer sign is retargeted as fields are (savedatascript.GetNewKey), and
attachablescript.Refresh() does nothing; usablescript.Refresh(slide, x, y) passes the usable's own turn state to the
beta's, layerScript.FStart() passes the collider's item, and GameObject.Find("Main") gets the object mainscript is on
(the beta's has another name). A mod's own Harmony.PatchAll() is all or none: one patch that fails on the beta
undoes the rest and the mod goes on (a mod stopped its OnLoad half patched). save_prefab, the public game's far
item record, is LoaderCompat.SaveRecord: the records a mod keeps of its own in savedata.farStuff/nearStuff (mods'
settings and progress, a string in rendszam.S under the hash of the mod's ID) are saved with the game as plate
strings (itemData.rendszam) under ids from 2^31 up; the game's own far items aren't records on the beta. A mod's own
field of another game type the beta dropped is an object, so its class loads (a field of the menu's
temporaryTurnOffGeneration took the class down, and with a list of such classes the whole mod). Every other dropped game
type is LoaderCompat.Gone, a component nothing in the world has: only code using its own members stops (a dbLoad
stopped at a search for the public game's dig holes, and everything after it). The road buildings' generator
(roadBuildingGeneration) is LoaderCompat.RoadBuildings: equal to null in Unity's sense, with no parent (the beta's
points of interest are roots of their own) and the current map's placed points of interest as placedBuildings.
inputscript.forward read as a bool is the beta's float above zero, partslotscript.Craft(part, bool) the beta's
Craft(part), and Type.GetMethod(name) that the beta's overloads make ambiguous (TextMeshPro's SetText) gets the one
with the fewest parameters, a string first, optional ones hardly counting; MethodBase.Invoke fills in the optional
arguments a call leaves out (a slider's text). Harmony's PatchAll called by reflection (a mod carrying its
own Harmony) patches class by class and leaves out a class whose target the beta dropped (mainscript.PauseRBs), where
PatchAll stopped there with the rest unpatched. mountStuff.Lock(item, bool) is the beta's Store (saved items in
their mounts); attachablescript.Load(pos) attaches to targetTosave at that place on it and pointLocalPos is where the
attach point sits on its target (swapping an item for another); savedatascript.properties is the save
data's (the distance driven). A SaveRecord's meter (a save helper keeps its numbers
in one) is saved in its plate string after the plate text, and a new plate string is empty, not null, as the public
game's was (a mod read null as its saved XML). A destroyed tumbleweed spawner leaves the map's
list before RestartMap numbers them and before each Update (a mod that destroys them: the beta's map threw on the
destroyed one and the world never loaded). The main menu
(mainmenuscript.mainmenu) is menuhandler.s, its SettingsScreenObj/SaveScreenObj the beta's settings and save screens
(active while open) and its Canvas the menu's. A DLL's module initializer runs before its types are checked, as it would
before the mod's first code: Costura-built mods
load the libraries they carry inside there. A rewritten mod loads from bytes and has no Assembly.Location: its Location
calls get the DLL it came from (mods find their assets next to it). The beta's item database has empty slots the public
game's hadn't: during the mods' DbLoad and OnLoad each holds a placeholder, emptied again afterwards (issue 44;
a vehicle list built in OnLoad). The public game ran DbLoad in the world: the beta has
an item database in the menu too, and ModHost leaves that one alone (it goes with the menu scene; what a mod made there
lived in the menu and read the missing player every frame). The world's database gets it, maybe before the
player is up: a mod whose DbLoad throws then (one that uses the player) runs it again once the world is
up, before its OnLoad. OnMenuLoad comes before the first DbLoad, as under TLD Loader, which calls it when it
makes the mod (a DbLoad can use what OnMenuLoad made). The mods' OnGUI and Config() run in an IMGUI pass of
their own with the layout pass on (RevaMP's overlay skips it): a mod's GUILayout threw "Getting control 0's position"
without it. A Loader mod's settings are where TLD Loader's mod list has them: the mods list (Installed) shows every
Loader mod, and Settings (for a mod whose Config() has code, TLD Loader's test) opens TLD Loader's window for it, a
draggable "Mod Config for <Name>" 500 wide and 70 % of the screen tall with a scroll view ConfigScrollHeight tall
(2000 unless the mod says), where its Config() draws as it did there (`ModHost.Settings.cs`). SettingAPI keeps the
values in Settings.json in TLD Loader's format, and takes a player's file from TLD Loader's own folder
(Documents/TheLongDrive/Mods/Config/Mod Settings/<ID>) the first time. typeof() of a renamed type in an attribute is renamed too ([HarmonyPatch(typeof(buildingscript),
"SpawnStuff")]: the type didn't load, and the mod's PatchAll stopped with none of its patches). The beta keeps more of tosaveitemscript's arrays as
lists (randomScales, randoms), and a field the beta dropped along with its type
(interiorLights) is kept for the mod as an object. A modding toolkit's item templates, which have no name, save
nothing when the old item database goes with a scene load (the beta makes a new one each time; it threw there once per
item). A copy of a tank cap that had started (a mod's vehicle clones one) starts as its own: Unity copies the beta's
started flag but not the particle module, and its Update threw every frame. A mod's AccessTools.TypeByName is remembered,
a miss too until another assembly loads (a mod that looks for other, absent mods every frame: each miss searched every
loaded type and logged a warning, and the game slowed to a crawl). A mod that uses another mod's types (one mod built
on another) gets that DLL from the mods folder, loaded once: a rewritten mod loads from bytes, where the runtime finds no
other mod by name, and the DLLs load in name order. tosaveitemscript.Claim(b), the public game's multiplayer claim, does nothing. `mods aliases` lists their mappings even in the menu; each changed DLL
logs which ones it used. Unchanged DLLs still load from their files. The old world generator has no aliases, and a
SaveRecord holds a mod's own data only: savedatascript.SpawnItem(save_prefab) and a record's transform aren't mapped. The existing seed/load/napszak/loaded/multi/world/mainWorld fields are host snapshots; writing those does not write beta
state. TLD Loader's helpers also work: SettingAPI, LoadedMods, isOnMainMenu/GetCurrentScene, Keybind, Logger/UseLogger,
UseHarmony (all of a
mod's patches or none: a patch naming what the beta lacks would throw inside the game's own method), Events,
GameUtil, CopyComponent), and TLD Loader 3.0.3's item registry (for mods that register items through it): ModLoader.Instance (the loader's
component; its dbmodsloaded is false only during DbLoad) and Database, Mod.RegisterItem(prefab, id).Register() with
WithRigidbody/AsPickupable/AsTank/AsExplodable/AsBreakable, Mod.GetItem, and inventory pictures kept in Loader/Cache next
to the mods. The public game knew an item by a category and an id; the beta knows it by its place in
itemdatabase.items alone (a save keeps tosaveitemscript.id, and the item comes back from itemdatabase.Item). A registered
item gets a place from 16384 up, by a hash of its mod's category and its id, the next free one on a collision
(TLDLoader.ItemSlots), and its tosaveitemscript.id is that place: the game saves, loads and spawns it as its own. The mod
reads and writes its own numbers: its tosaveitemscript.id and .category go to LoaderCompat.ItemId/ItemCategory (the beta
has no category; for other items they are as before). The registry is emptied for each new item database and the mods
register again, in the same order, to the same places. An item a mod puts in the table itself (appending to
itemdatabase.items in DbLoad) is moved to a place of the same range after that mod's DbLoad, by the mod's ID, the
item's name and how many of that name the mod added before it, so its place doesn't change when another mod comes or
goes. A saved item whose place has no prefab (its mod isn't loaded) comes back as the game's error item but keeps its
place, so it saves unchanged and is itself again once the mod is back. Not there: WithCondition, AsPart, SpawnAt and SpawnInBox (no
known mod calls them) and 3.0.3's patches of the public game's lookups by category. DbLoad runs
for every mod with LoadInDB once per item database, in the order of the mods' IDs, so every machine changes and adds
items the same way. That holds only when every player has the same TLD Loader mods: a host refuses a joiner whose mods differ by
ID, version or DLL (SHA-256), and says what to install or remove (`Net/LoaderModCheck.cs`, config
Mods.LoaderModsMustMatch; nothing is downloaded; install these mods by hand). Most of those mods were written for single player. A converter would help them into multiplayer
without rewriting them:

1. **Scan** a Loader mod's assembly and report what it does that matters in a session: objects it creates,
   components it adds to game objects, input it reacts to, game state it changes directly.
2. **Classify** each finding: works as is (purely local or cosmetic), works through the game's save system
   already, or needs registration (section 3) or records (section 4).
3. **Generate a starting point**: a registration and record skeleton for the mod's author to fill in, never a
   silent rewrite of someone else's code.
4. **Report**, in plain words, which features will be local only until the author adopts the API.

Out of scope: changing a mod's behaviour without its author, and mods that replace the game's own systems (another
multiplayer, another mod loader).

Built: `src/TLDRevamp/LoaderScan.cs` reads a mod's code (its IL; nothing is run) and writes `<mod>.md` and
`<mod>.MpSkeleton.cs` to `BepInEx/TLDRevampModScan`. To use it, set `[Mods] ScanLoaderMods = true` in
`BepInEx/config/tldrevamp.core.cfg`: every mod in `BepInEx/TLDLoaderMods` is scanned at startup. The report lists:
- its lifecycle overrides, dependencies that could not be resolved, type-load failures and counts of resolved and
  unresolved member references, so a beta API change is visible before the author rewrites a feature;
- objects the mod makes from its own assets;
- components it adds;
- keys and buttons it reacts to;
- world-wide values it changes (time scale, gravity);
- random numbers it rolls;
- game state it writes directly;
- files it writes;
- the game's own things it spawns (shared already);
- what only draws on screen (works as is).

The same scan runs without starting the game (build Revamp first):

```sh
TLD_DIR="/path/to/The Long Drive" dotnet run --project tools/modscan -- MyMod.dll
```

The report and the skeleton are written **next to the scanned DLL** (`MyMod.md` and `MyMod.MpSkeleton.cs` there),
which is where the in-game scanner writes them too. `--out <folder>` sends them somewhere else. The tool never writes
into the current working directory, so running it from the repository root cannot leave build output in the tree.

The report is deliberately a compatibility assessment, not a converter: it never runs or rewrites the mod. A zero
unresolved-member count proves that the assemblies needed to resolve its references were available to the scanner; it
does not prove that the mod's lifecycle or gameplay is correct. Test the mod's own features in single player, then in
the two-machine test after porting its multiplayer behavior.

Before changing game-facing code, compile the same DLL against the scanner's `TLD_DIR` (or the supplied `--lib`
folders). Missing dependencies and changed game members are then errors in the report and in the build, rather than a
runtime surprise after players install it.

Each item says what it means in a session. The skeleton registers the mod, has a `SharedType` for each component
the mod adds, a setting for each world-wide value and an event for each input handler, all to be filled in by the
mod's author.

## 8. Testing

Every registered type would get the same two-machine checks the game's objects have: created on one machine and
seen on the other, edited by the non-owner, owner leaves (hand-off), both players far away and back (far store),
a late joiner sees it, and the session saved and loaded. The two-machine test tools in `tools/` run the game's own
objects through these today ([tools/README.md](../tools/README.md)); to see what the game does with an object, decompile
it yourself as [CONTRIBUTING.md](CONTRIBUTING.md) explains.

## Mod browser catalog

The in-game browser reads `packaging/mods.json` from `dev`, where the browser is developed. Its `id` is the
BepInEx plugin GUID: `tldrevamp.headunit` and `tldrevamp.cartrailer`. Head Unit's multiplayer registration id,
`headunit`, stays unchanged; it is a separate identifier used by the session protocol. Using the plugin GUID in
the catalog lets the browser recognize mods installed by hand, including Car Trailer, which does not register
with the multiplayer API.

The Mods screen is a list like the RevaMP launcher's Mods page, drawn with the game's own menu pieces: tabs **All mods**
and **Installed**, a search (name, author, description, id, tags), a sort (*Most downloaded*, *Recently updated*,
*Name*), and one row per mod with its buttons. A mod may carry optional `"tags"` in the catalog (at most six short
words; the launcher shows them too). Download counts and dates come from GitHub's release lists of the repositories
the catalog's URLs name (`api.github.com/repos/<owner>/<repo>/releases`), summed over a mod's versions by tag/file;
each list is kept for an hour in `BepInEx/cache/tldrevamp-modstats/`, and a list that can't be fetched leaves its
mods without numbers. The required-mods screen shown when joining (below) keeps the tapes.

Managed installs have **Disable / Enable** and **Remove** controls, including local entries when the catalog is
offline. A row's state reads **On**, **Off**, **On at next start** or **Off at next start** (with *· update to v…*
when the catalog has a newer version), and **Removed at next start** after **Remove**: until then **Install** takes
the removal back, and after the restart the mod is **Not installed** and installs again as usual. After **Disable** or
**Enable**, the message under the list gives the full
state, as the bridge's `mods list` does: **Enabled**, **Disabled (takes effect at next start)**, **Disabled** or
**Enabled at next start**. Hand-installed mods show **By hand** (state *On · installed by hand*) and are read-only
because one DLL or folder can serve several plugins or dependencies. RevaMP (`TLDRevamp`, `TLDRevamp.Multiplayer`,
`TLDLoader`, its preloader) and BepInEx core cannot be toggled.

`TLDRevamp.Preloader.dll` is installed under `BepInEx/patchers/TLDRevamp/`. Its initializer replays durable intent
before the chainloader scans plugins. BepInEx 5.4.23's `TypeLoader.FindPluginTypes` uses
`Directory.GetFiles(..., "*.dll", SearchOption.AllDirectories)`; naming a subfolder "disabled" does not disable it.
We move managed DLLs between `BepInEx/plugins/TLDRevampMods/` and `BepInEx/disabled-mods/<id>/` instead. Each
same-volume file rename is atomic, never overwrites a destination, and can be replayed after a partial batch or
crash. The intent file `<id>.state` is atomically replaced and flushed before any moves. It lives outside the DLL
folder, so losing that folder leaves a visible **Disabled (DLL folder or file missing)** entry. Conflicts preserve
both copies and leave the action pending. Linked files/folders and paths outside the managed locations are refused.

Updates and new installs of a disabled mod write to its disabled folder and keep its intent. Old versions remain
as `.replaced`. Remove queues a move to a unique `.removed` backup at next start, from either location; no DLL is
deleted. Preserve `BepInEx/disabled-mods` during RevaMP upgrades. Build references include BepInEx's local
`Mono.Cecil.dll` (`tools/libs.sh` copies it); the preloader is built automatically with the main project and included
by the package, source/test installer, and deployment scripts.

Bridge commands: bare `mods` retains the Loader-mod status command; `mods list` returns browser ids, versions,
`installed`, desired `enabled`, `pending` (null or enabled/disabled/removed), `loaded`, `registered` (the live API
registration used by Hello), and a readable state. `mods enable <id>` / `mods disable <id>` queue changes;
`mods remove <id>` queues removal using the same file path as the screen. Existing `mp mods` is unchanged.
For the list itself: `mods open` / `mods close`; `mods view [search=<text>|-] [tab=all|installed] [sort=0|1|2]
[scroll=<0..1>] [ask=<question>] [press=<id>:<button label>]` sets what a player would and returns the rows as shown
(`+` stands for a space); `mods ui` dumps the menu's layout and `mods sprite <name>` saves a UI sprite's texture.
The external `modtogglecheck.py --selftest` is offline; its live mode works only on one hidden, isolated test copy of
the game and verifies loading, disabling, enabling and removal while disabled across restarts. It must be run
separately.

Head Unit 0.2.0 first shipped with RevaMP 0.68.0; the catalog's `revamp: "0.68.0"` records that compatibility
baseline. The field is checked: a RevaMP older than a release's `revamp` does not offer or install that release; it
offers the newest one that fits, or shows *Needs RevaMP x.y.z*. Beyond that, the live catalog and the downloaded
DLL's SHA-256 decide whether installation is allowed.

Car Trailer 0.1.1 is listed with the hash of its existing release DLL. Its catalog ZIP size and both example mods'
DLL hashes were checked against the v0.69.0 assets. The original assets are reused without rebuilding them.
Car Trailer can be installed from the browser and loads after restarting the game; every player still needs it, since it
does not register for the host's missing-mod check listing.

BusGut, MountAnything and TerrainChoice (by YujiroX2, `anonymoustld.*` GUIDs) have their source and releases in
TLD-Mods (`mods/BusGut`, `mods/MountAnything`, `mods/TerrainChoice`; tags `<mod>-v<version>`, for example
`terrainchoice-v0.5.0`) and are listed with the hashes of their release DLLs: BusGut 0.2.0, MountAnything 0.2.1 and
0.2.2, TerrainChoice 0.4.1, 0.4.2 and 0.5.0. All three register with the multiplayer API (`busgut`, `mountanything`,
`terrainchoice`), so a join with a different set is refused. Their catalog baseline is RevaMP 0.69.1, the version
their self-tests passed on.

TV Browser 0.1.0 (`tldrevamp.tvbrowser`) is a mod-only release (tag `tvbrowser-v0.1.0`), listed with its DLL's hash.
It registers with the multiplayer API (`tvbrowser`), so every player in a session needs it. Its baseline, for 0.1.0
and 0.2.0 alike, is RevaMP 0.69.2: both have a static field of the API 4 type `ReplayChannel` in the plugin class,
and API 4 came after 0.69.1, so with 0.69.1 that class fails to load before its `MissingMethodException` catch can
run.

Road End 0.1.0 (`tldrevamp.roadend`) is a mod-only release (tag `roadend-v0.1.0`), listed with its DLL's hash. It
registers with the multiplayer API (`roadend`) and shares one session setting (`world`: the host's world has an end or
not). It works without RevaMP too.

Head Unit 0.2.1, Car Trailer 0.1.2, Trailer Hitches 0.2.0, TV Browser 0.2.0 and Road End 0.2.0 are mod-only releases
(tags `<mod>-v<version>`, for example `tvbrowser-v0.2.0`), and World Map 0.2.1 is a release of its own repository.
Each is listed with its DLL hash in the live catalog on `dev`. New entries become installable without a RevaMP
release. Older versions stay in the catalog so joiners can install the identical release the host uses.

Head Unit, Car Trailer, Trailer Hitches, TV Browser and Road End moved to [TLD-Mods](https://github.com/TLD-RevaMP/TLD-Mods)
with their source. Their releases there (`headunit-v0.2.0` and `cartrailer-v0.1.1` for the two zips that came with
v0.69.0, the other tags unchanged) hold the same zips byte for byte, so the catalog hashes did not change; the catalog
downloads from TLD-Mods. New versions are released there.

## 9. Order (all built)

1. Registration + full records for objects a mod creates (sections 3 and 4).
2. Edit records and ownership hooks (section 5).
3. State records with rates and interest.
4. Session-wide settings (section 6).
5. The Loader-mod scanner and report (section 7), then the skeleton generator.
