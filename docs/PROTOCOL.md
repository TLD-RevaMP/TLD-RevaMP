# Wire protocol

`Protocol.Version` in `src/TLDRevamp/Net/Protocol.cs` is the number two players compare when one joins. This file is the
one place that says what each number changed; other documents link here and don't repeat the current number.

## When the number changes

The number changes once between public releases, not with every wire change:

- A wire change on dev adds a line under the top number below.
- If the top number is marked as released, raise it first, once, and start a new section.
- Two dev builds can't join each other anyway: the join check compares the source commit, and a build with local
  changes needs the identical DLL (`Net/BuildCheck.cs`). So the number protects nobody between dev builds.

The public release v0.69.1 carries 28. The numbers above that were raised on dev, one per change, before this rule.

## History

## 61

- Hello ends with the joiner's TLD Loader mods (id, name, version, DLL sha256 each); the host refuses other ones (`Net/LoaderModCheck`, config Mods.LoaderModsMustMatch)
- CarTune (113): a car's tuning (final drive, gear ratios, wheel positions, suspension travel, which wheels drive and steer) from its owner, kept by the server and sent with the car to later players (`Entities.CarTune`)
- Hello ends with the joiner's game data hash and its list of other mods' patches on the world generators (`WorldCheck`); the host refuses a different game, other data files or other generator patches
- a glued member's place in the census rows, its pose on request
- a Record can end with the ids of what came back from a Restore (`PlayerSave.RestoreConfirmed`)
- a Refetch can end with a byte: the asker still has the part's car and needs the part itself (`RefetchParts`)
- a breakable's number in BreakHit and BreakFx counts the unbroken ones (`BreaksKept`)
- a BreakFx ends with a byte: keep this break for whoever gets the object later, and this is such a replay
- the world clock (97) ends with the host's world total (f64) and a sleeping player's cloud curve (f32, -1: nobody); a SleepSync (38) ends with the sleeper's curve (`Fixes.CloudClock`)

## 60

- the census ask can end with the copy's position (six bytes, PlaceDigest), the owner sends a state again when the copy is elsewhere

## 59

- an Edit can end with the tanks' changes, a pour on a copy adds to what the owner has

## 58

- CensusRepair (63), a copy names what still differs from its owner and the owner sends it again; the census covers every object near a player

## 57

- a part unbolted from a member carried away from its group (a tyre on a wheel off a wreck) is named by the carrier's slot: PartOff and PartDetached can end with the carrier net and slot

## 56

- the engine block of State can carry the owner's engine temperature (flag 8, i16 tenths of a degree after rpm): car copies show the gauge, temp lamp and coolant steam

## 55

- voice to everyone (the codec byte's top bit, Voice.ToEveryone) and the session's voice setting appended to the settings snapshot (VoiceEveryone)

## 54

- the ragdoll block has a mode (Frame / Key / Delta): only the limbs that moved since a Key

## 53

- claim reasons ItemNear and ItemContact (a moving item takes the still loose item it is about to hit, Entities.ItemPush)

## 52

- the terrain byte in Welcome can be OldTerrain (2), the 2023 world

## 51

- the host's terrain mode (Crossroads / MainRoadOnly) rides in Welcome as the LAST byte, so the protocol 50 settings snapshot keeps its place

## 50

- validated host-owned SessionSettings (111) in Welcome and on change: TPA, join placement and session-only developer policy

## 49

- State flag 128 appends a held item's right-hand-target-relative root pose after frame/lock fields (3 x i16 millimetres, +/-2 m per axis, smallest-three rotation; 13 bytes); car signals carry the owner's steer (copies turn the cabin steering wheel)

## 48

- live building usables use 93 net=0 plus POI address and quantized float values; reliable 52 appends sequence and session stamp

## 47

- mark requests and broadcasts (92) append up to 16 marked net ids

## 46

- live object comparison request/query/answer/row/roster (105-109), numeric snapshots and recent event codes; join manifest/end marker moves to 110

## 45

- Ready optionally carries join position and requests the initial-object manifest/end marker (105)

## 44

- Claim drops the pickup epoch/clock extension and State drops held-release flag 128

## 43

- world-pose physlocks carry their mount address for takeover

## 42

- Claim producer byte and explicit reliable held release in State flag 128

## 41

- pickup Claim carries epoch and owner pose-clock fence; host-authenticated renamer index appended to relayed mark renames (104)

## 40

- stable UTC session date in Welcome, 500-character mark notes, authenticated mark origin in message 92, reliable note renames (104: sid, momentSt, origin, note)

## 39

- RabbitState (90) is a reliable change snapshot with owner epoch and centimetre global target (was relative floats); CompactState (98) also carries live upright ground animals, unchanged format; explicit physlock state, parent frame and container reference

## 38

- owner-routed trailer hitches and acknowledged frozen transfers (99-103), riding poses carry vehicle epoch, frame and owner-clock sample (Pose/OtherPose), attach pose carries its exact moving frame and point offset; live usables use registered save order

## 37

- compact flyer poses (98)

## 36

- source commit/dirty/DLL identity in Hello

## 35

- stamped world anchors (97), SleepSync and radio session timestamps/rates

## 34

- a non-owner's hand on a car's ignition key (Entities.IgnitionKey, 96), a car's blood washed off in an edit (KindBlood)

## 33

- reciprocal opt-in mark/report sharing (60/61, 94/95)

## 32

- live item usables (93), session-clock fences in Edit/Resync

## 31

- an attach carries the attached object's pose in its point's parent frame (Entities.AttachPose)

## 30

- telemetry session id and initial host clock in Welcome; synchronized /mark (TelemetryMarks, message 92), ReportAsk moment and ReportChunk mark flag

## 29

- the teleport request's go message carries the height (Tpa.GoMsg)

## 28

- mod API 2 (state records with interest, the far store, session-wide settings: Net/ModObjects.cs)

## 27

- rabbits (Entities.Rabbits: their state and sounds, bites on remote players, a bite's breaks summed)

## 26

- other mods' objects and events (Net/ModObjects.cs), the mod list in Hello (24: the hit check, 0.66.9; 25: per-player saves, 0.67.0)

## 23

- players standing on a shared car ride in its frame (Entities.Footing)

## 22

- held items in a seated player's car travel in its frame (Entities.Carried)

## 21

- joiners placed next to the host (SpotReq/JoinSpot)

## 20

- the sender's clock in player states (bodies interpolated, RemotePlayers)

## 19

- the old owner's last state goes to the new one (Entities.HandoverState)

## 18

- only the owner deletes a shared object; a lost copy is fetched again (Entities.Refetch); hum and pee in poses (PlayerActs); held flag in states, cargo claims (Entities.Cargo)

## 17

- password in Hello, reject codes, kick, chat, voice (Net/Session.cs, Net/Voice.cs)

## 16

- players' footsteps, burps, farts (Entities.PlayerSound)

## 15

- the host's sandstorms (Entities.StormSync)

## 14

- limbs of bodies in states (Entities.Ragdoll)

## 13

- the host's physics-lock setting (Entities.PhysLockSync)

## 12

- car signals in states (Entities.CarSignals)

## 11

- radio stations follow the host (Entities.RadioSync)

## 10

- pushing other players' objects (Entities.PushIn)

## 9

- wheel travel as a fraction of the suspension

## 8

- compact car states (Entities.StateCodec), batched relays
