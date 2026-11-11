# Messages

Every message the mod sends, and what happens when one has no effect. Whether it arrives isn't the question here:
refused sends queue (`SendOutbox`), so a reliable message gets there. The question is what happens when it arrives and
is refused or ignored, or is dropped because the player hasn't finished loading.

Read from the code at commit ce60aee, not from a test run, except that the heals of BreakFx, Refetch, Restore and
WorldBreak were checked in the game with `tools/itemchaos.py` (`aud-break-late`, `aud-part-lost`, `aud-restore`, `aud-world-break`). `tools/unit/MessageTableTest.cs` compares this table with the
code: every routed message has a row, and each id and direction are what the dispatchers do. A new message needs a row.

**Kind**

- **state:** one message changes lasting state on the receiver, and nothing repeating replaces it.
- **stream:** an owner's or host's repeating or change-driven messages; a later one, or a kept record, replaces a lost one.
- **ask:** a request. The sender changes nothing until the answer, or losing it leaves machines the same.
- **effect:** sound, a visual, or a one-off push.
- **session:** join, chat, voice, reports, panels, mod download; not object state.

**Heal** is the code that brings machines back together, for state and stream rows. **gap** means nothing does: the
machines can stay different until the session ends. The test keeps the list of gaps, so adding one has to be a decision
and fixing one takes it off the list.

**Direction** is what the dispatchers in `Mp.cs` and `Entities` accept: `c>s` on the server, `s>c` on a client, `both`.
`transport` is handled below them.

The census covers only copies within 200 m of their player. It doesn't cover colours, food, ammo, plates, worn items,
breakables, trailers or mod objects.

## Table

| id | name | dir | kind | heal | note |
|---|---|---|---|---|---|
| 1 | Hello | c>s | session | - | Refused with a Reject that names the reason. |
| 2 | Welcome | s>c | session | - | Bad session fields return silently; only a bad settings snapshot names a reason. |
| 3 | Reject | s>c | session | - | |
| 4 | Ready | c>s | session | - | Opens the gate: entity and mod messages before it are dropped, except lease traffic and the mod clock. |
| 5 | Chat | both | session | - | Flood limit: 5 lines in 5 s, the rest dropped. |
| 6 | Voice | both | session | - | Unreliable; needs the speaker's position on the server. |
| 7 | Roster | s>c | session | - | Pings for the player list, every 2 s. |
| 8 | SpotReq | c>s | session | - | Answered with JoinSpot, then the player's saved pocket. |
| 9 | JoinSpot | s>c | session | - | No answer in 6 s: the joiner stays at the start, unless the join screen is up. |
| 10 | State | c>s | stream | `Relay` next state replaces it | Player position, unreliable, 20 Hz. An implausible one is dropped. |
| 11 | PlayerJoined | s>c | session | - | Sent directly at Ready. |
| 12 | PlayerLeft | s>c | session | - | |
| 13 | OtherState | s>c | stream | `Relay` next state replaces it | Rate falls with distance. |
| 14 | Pose | c>s | stream | `WriteLocalPose` next pose replaces it | Relayed only within pose range. |
| 15 | OtherPose | s>c | stream | `WriteLocalPose` next pose replaces it | Relayed only within pose range. |
| 16 | Outfit | c>s | stream | `OutfitHash` resends on change; `Outfit` kept for newcomers | Refused only if unreadable. |
| 17 | OtherOutfit | s>c | stream | `Looks` keeps the last one per player | Refused only if unreadable. |
| 18 | Batch | transport | session | - | One tick's unreliable messages in a bundle; a bad one is dropped, the rest apply. |
| 20 | Share | c>s | state | gap | Over quota or before Ready: item stays on the sharer only, and a pending entry blocks sharing it again. |
| 21 | Assigned | s>c | state | gap | Item destroyed before this arrives: no removal goes out, others keep a copy nobody moves. |
| 22 | Add | s>c | state | gap | Copy refused (undecodable, root did not spawn): missing for that player; only a teleport asks again. |
| 23 | State | both | stream | `SendSettledState` on entering range; `CheckCopyPlace` compares where it rests | Refused if not owner or old epoch. Place check only for still objects within 200 m. |
| 24 | Claim | c>s | state | `RefuseClaim` Owner reply undoes provisional ownership | While provisional, the claimer's states and records are dropped at the server. |
| 25 | Owner | s>c | state | `WriteAdd` carries owner and epoch for copies made later | An older epoch is ignored. |
| 26 | PartOff | c>s | state | `ServerCensusRepair` with `RepairOff` resends within 200 m | Refused: sender told in chat, part stays on for others until a copy is near. |
| 27 | PartDetached | s>c | state | `SendObject` replays parts; `ServerCensusRepair` sends missing ones | Refused as part absent: the census repeats it but cannot rebuild a member lost here. |
| 28 | LeaseReq | c>s | state | gap | Asked before Ready: the refusal's reply is dropped, that building never spawns or swaps contents here. |
| 29 | LeaseGrant | s>c | state | `PendingGrants` retried until found; `ServerLeaseExpiry` frees it after 60 s | |
| 30 | LeaseTaken | s>c | state | gap | Building object absent on arrival: its built-in items stay on here, beside the holder's shared ones. |
| 31 | Resync | both | stream | `ServerCatchUpTick` when near again; `ForgetResyncHash` after an edit | A provisional claimer's record is dropped with its hash kept: next change or census. |
| 32 | Edit | both | state | `EditBackLater` and `EditBackAfterHold` make the owner resend | No record back if malformed, clock over 10 s off, or 256 already waiting. |
| 33 | LeaseDone | c>s | state | `ServerLeaseExpiry` frees an unconfirmed lease after 60 s | Refused only when the lease is gone or another's. |
| 34 | LeaseFreed | s>c | state | `OnLeaseFreed` clears flags; `LeaseRetryS` later the game asks again | Never reaches a player before Ready: see LeaseReq. |
| 35 | RemoveItem | both | state | `ServerRemoveItem` drops the record, so no later Add | No refusal: any ready player may remove, receivers always apply. |
| 36 | ContactImpulse | both | effect | - | Unreliable to the owner. |
| 37 | DetachReq | both | ask | - | Also carries a stale owner's detach; refused by reach or rate, the census then settles it. |
| 38 | SleepSync | both | stream | `ServerTimeTick` next clock replaces it | With the world clock on, the server answers with WorldClock. Ends with the sleeper's cloud curve (-1 on waking). |
| 39 | ShotgunIn | both | ask | - | Passenger input, repeats at 20 Hz. |
| 40 | PlayerDamage | both | ask | - | Refused by the hit check: victim's health unchanged, nothing differs. |
| 41 | AttachSync | both | state | `ServerCensusRepair` within 200 m; `ReplayAttaches` for later players | Trailers have a fixed census row. Unshared parent: nothing sent. |
| 42 | DetachSync | both | state | `ServerCensusRepair` within 200 m | Trailers have a fixed census row. The server's trailer release now reads back on every machine. |
| 43 | CrashClaim | c>s | state | `ServerCrashClaim` refusal replies Owner; `CrashHoldTick` ends the hold | |
| 44 | CrashRelease | c>s | state | `ServerCrashTick` returns the lease when it runs out | Ignored from a player who holds no lease. |
| 45 | ShotFx | both | effect | - | |
| 46 | ExplodeReq | both | ask | - | Refused by the hit check: nothing happens anywhere. |
| 47 | ExplosionFx | both | effect | - | Not kept: players loading or arriving later see nothing. |
| 48 | BreakHit | both | ask | - | Refused by the hit check: nothing happens anywhere. |
| 49 | BreakFx | both | state | `SendBreaksTo` for later copies; `BreakWaitTick` for a copy stored far | Kept for the session, made again when the game places the object anew; a creature whose Die throws is tried 5 times. A breakable that is an item of its own goes as MemberGone. |
| 50 | AiState | both | stream | `SendCreatureSnapshots` on entering range; 1 s keepalive | Unreliable, within 1000 m. A dead creature's flag goes once. |
| 51 | AiSound | both | effect | - | |
| 52 | PoiUsable | both | state | `ServerCatchUpTick` for others; `PoiTellBack` for a refused sender | Refused, nothing known and building not loaded on the host: the sender's door stays moved. |
| 53 | PushIn | both | effect | - | |
| 54 | RadioSync | s>c | stream | `RadioTick` on change and every 5 s | |
| 55 | PhysLockSync | s>c | stream | `PhysLockTick` on change and every 5 s | |
| 56 | StormSync | s>c | stream | `StormTick` every 2 s | |
| 57 | PlayerSound | both | effect | - | |
| 58 | Refetch | c>s | ask | - | Lost twice in 30 s: asked again after 30 s. A lost part comes from the host's copy; not loaded on the host, or lost on the host itself: stays gone. |
| 59 | HandoverState | both | stream | `ResyncTick` new owner's first pass sends everything | Late or refused: the old owner's last changes are lost, machines still agree. |
| 60 | ReportAsk | s>c | session | - | Ignored if not sharing, or within 30 s of the last. |
| 61 | ReportChunk | c>s | session | - | Only from players who share. |
| 62 | AttachReq | both | state | `ApplyAttachReq` answers; `ServerCensusRepair` within 200 m | Owner with it stored far answers nothing until a hand-off. |
| 63 | CensusRepair | both | ask | - | Refused on a stale epoch; the next census asks again. Flag 8 is the record-again request for edits. |
| 65 | CensusAsk | both | ask | - | Copies within 200 m. Refused silently on a stale epoch or by an owner with it stored far. |
| 66 | CensusReply | both | ask | - | Also carries an owner's place findings and glued members' poses. |
| 67 | Record | c>s | stream | `SendRecord` periodic | Rows for objects the sender doesn't own are skipped. |
| 68 | Restore | s>c | state | `ConfirmHanded` keeps what the record doesn't name; `TakeHandedBack` when they leave | An item that can't spawn stays with the host and is sent again at each join. |
| 69 | GoMsg | s>c | session | - | Invalid destination: logged, no move. |
| 70 | ModShare | c>s | state | gap | Over budget or before Ready: object stays on the sharer only, a pending entry blocks a resend. |
| 71 | ModAssigned | s>c | state | `ModRemove` follows if the object is already gone | |
| 72 | ModAdd | s>c | state | `ServerOnJoin` for later players; `ServerTick` adds ranged objects again | No view range: a copy the mod failed to make stays missing for that player. |
| 73 | ModRecord | both | stream | `OwnerTick` next change; `WriteAdd` for later copies | Budget drop: copies wait for the next change, the dirty flag is already cleared. |
| 74 | ModEdit | both | ask | - | The sender changes nothing until the owner's record. |
| 75 | ModOwner | s>c | state | `WriteAdd` carries the owner; `SetOwner` on the next change | An older epoch is ignored. |
| 76 | ModRemove | both | state | gap | Refused when ownership just moved or over budget: gone on the sender, others keep it. |
| 77 | ModPose | both | stream | `OwnerTick` next pose or keepalive | |
| 78 | ModEvent | both | state | gap | Over budget or before Ready: lost, nothing resends. In-tree user: TV keys, typed text can differ. |
| 79 | ModClaim | c>s | ask | - | Refused silently unless the type allows takeover; the sender changes nothing. |
| 80 | ModClock | both | ask | - | Repeats; allowed before Ready. |
| 81 | ModOffer | s>c | session | - | |
| 82 | ModFetch | c>s | session | - | Only before Ready, only what was offered. |
| 83 | ModChunk | s>c | session | - | Paced by the send queue. |
| 84 | PoiCensus | c>s | session | - | Log only: names a building that differs, repairs nothing. |
| 85 | Worn | both | state | `SendWornTo` for later copies; `WornNotMineAnymore` takes it off a refused head | Not in the census. |
| 86 | MemberGone | both | state | `SendGoneMembers` for later copies | Refused forward (owner has it stored far): member stays deleted on the deleter only. |
| 87 | ModState | both | stream | `OwnerTick` next change; `SendState` by sequence per player | Budget drop: the owner's last state is already marked sent, copies wait for the next change. |
| 88 | ModStore | s>c | stream | `ServerTick` adds it again on coming near | |
| 89 | ModSetting | s>c | stream | `WriteSettings` on change and at join | |
| 90 | RabbitState | both | stream | `SendRabbitSnapshot` via `StateInterestCatchUp` on entering range | Within 1000 m. |
| 91 | RabbitFx | both | effect | - | Eyes and ears can only be lost: a shooter's copy keeps the loss if the owner never heard it. |
| 92 | Message | both | session | - | A mark; one per player every 10 s. |
| 93 | LiveUsable | both | stream | `ForgetResyncHash` then a final record; `SendPoiFinal` for buildings | Unreliable while moving; refusals settle at the final record. |
| 94 | ShareState | c>s | session | - | |
| 95 | SharedChunk | s>c | session | - | |
| 96 | IgnitionKey | both | ask | - | A held key repeats; a hold ends on its own. |
| 97 | WorldClock | s>c | stream | `ServerTimeTick` every 2 s | Ends with the host's world total and a sleeper's cloud curve (`Fixes.CloudClock`). |
| 98 | CompactState | both | stream | `SendSettledState` on entering range; `CheckCopyPlace` compares where it rests | Refused if not owner or old epoch. Place check only for still objects within 200 m. |
| 99 | HitchRequest | c>s | ask | - | Refused: nothing changed locally, the result goes to everyone's log. |
| 100 | HitchCommand | s>c | ask | - | Not answered in 5 s: the server releases the hitch. |
| 101 | HitchFreeze | s>c | ask | - | Not answered in 5 s: the server grants the trailer with its last pose. |
| 102 | HitchFrozen | both | ask | - | Refused: the same 5 s timeout. |
| 103 | HitchResult | both | ask | - | Log only on clients. Refused at the server: the 5 s timeout releases the hitch. |
| 104 | MarkRename | both | session | - | Queued until sent. |
| 105 | Ask | c>s | session | - | Read-only object panel, as 106 to 109. |
| 106 | Query | s>c | session | - | |
| 107 | Answer | c>s | session | - | |
| 108 | Row | s>c | session | - | |
| 109 | Roster | s>c | session | - | |
| 110 | JoinLoading | s>c | session | - | Manifest, then an end marker for the join screen. |
| 111 | SessionSettings | s>c | session | - | Refused unless it carries this host's token. Sent again at Ready. |
| 112 | WorldBreak | both | state | `ServerWorldBreaksTo` for later players; `WorldBreakTick` when near | Kept for the session, 4096 at most. A piece that never appears here is looked for less and less often. |
| 113 | CarTune | both | stream | `SendTuneTo` for later players; `CarTuneTick` applies it again when the car is placed anew; the owner sends a tuned car again every `CarTuneResendS` | Only from the car's owner; the server keeps the latest per car. Unreadable or out of range: refused, not passed on. |

## Not settled by the code

- **LeaseReq (28), LeaseFreed (34):** the code settles that a request made before Ready gets no reply and is never asked again. Whether the game's building spawn runs before Ready in real play rests on one code comment; nothing else confirms it.
- **Share (20):** the quota refusal is settled. The spawn hook checks only that a session id exists, so a spawn between Welcome and Ready would be dropped too. Whether the game calls that spawn in that window is not visible here, as the game's code is outside this repository.
- **BreakFx (49):** which breakables outlive their break without being an item of their own isn't listed. A rabbit does; a bus's toilet is an item of its own.
- **WorldBreak (112):** a piece stayed down on both machines after both went 3 km away and back. Whether it stays down across a restart of the host is not established.
- **ModEvent (78):** lasting or not is up to each mod. The only in-tree user sends TV keys, which end up as page text on the other machines. Marked as state and gap on that basis; a mod using it only for effects would have no lasting difference.
- **ModShare (70):** dropping before Ready is settled. Whether a mod has objects to share in that window depends on the mod.
- **PartOff (26), carried part form:** a refused part named by its carrier's slot was not traced through the census. The group's owner may not have the group loaded, and then it answers nothing, so a heal is not established for that form.
- **AttachSync (41), trailer refused on a copy:** no census row and no resend. The trailer's pose still follows the owner's states, so the visible effect is limited to its coupling. How much that shows was not determined.
- **Resync (31), provisional claimer:** how often a pass falls inside the claim's round trip, and whether the handover record usually changes the hash anyway, was not determined.
