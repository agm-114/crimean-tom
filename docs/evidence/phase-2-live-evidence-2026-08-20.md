# Phase 2 live evidence, 2026-08-20

Pinned game build: `0.8.2-23444+s358`.

## Owned mutation admission

The first mutation family is disabled on every game-process start. Enabling it
requires the authenticated loopback control token, the current named-pipe
session ID, the current runtime fingerprint, the exact confirmation
`enable-owned-mutations`, and a bounded 1–600 second duration. The arming state
belongs only to that pipe session, expires automatically, and is cleared on
disconnect. Runtime capabilities report the mode, expiry, and exact enabled
mutation command. Arming does not make the runtime authority-eligible; all
unrelated determinism blockers remain active.

Every surface materialization carries a session ID, assignment ID, authority
epoch, expected entity revision, and idempotency key. The companion rejects an
invalid session or structurally incomplete request before forwarding it. The
plugin repeats validation on Unity's main thread, requires a fully loaded and
paused tactical mission, and restricts the first family to the exact catalog
tuple `usn_ff_knox_84` / `Variant13` / `Default` in the player task force.
External entity IDs must use the bounded `unit:crimean-tom:` namespace.

## Live Knox materialization

Red Sea Breakout was loaded and its briefing advanced entirely through typed UI
automation. At simulation second `3.950890064239502`, the bridge paused the
mission. An armed dry run validated a Knox at latitude
`12.9396320322755`, longitude `43.1309606993161`, heading
`144.830795288086`, telegraph zero, in `taskforce:Taskforce1`, without changing
the world.

The live request then used Sea Power's supported
`ObjectsManager.createShip` path synchronously. It completed in 155,468 µs and
read back:

- external tag `unit:crimean-tom:phase2-knox-1`;
- canonical stable ID
  `campaign-unit:unit:crimean-tom:phase2-knox-1`;
- game unique ID 355;
- exact unit, variant, loadout, task-force, position, telegraph, and zero speed;
- alive and enabled state, seven observed ammunition types, and no hosted-air
  rows.

The full world reader found exactly one matching owned unit. Observed objects
increased 39 to 40, units increased 18 to 19, campaign identities increased
zero to one, and stable identity remained `ready`. The resulting world canonical
SHA-256 was
`0f4a1533611fdcafd8239916a75c56a0a9ac0d14d4f1cf73ef4719ac3658769a`.

Repeating the exact create with the same idempotency key returned the original
game ID, revision 1, and duration with `idempotentReplay=true`; it did not create
a second object. Reusing that key with a different heading failed closed with
`idempotency-conflict`. While armed, capabilities exposed only
`materialize-owned-surface-unit`; `authorityEligible` remained false because
the random-source determinism inventory is not controlled.

## Portable checkpoint and fenced lifecycle

The second live slice loaded Red Sea Breakout through the typed UI seam and
paused it at simulation second `97.1970443725586`. A newly armed session
materialized `unit:crimean-tom:phase2-lifecycle-knox-1` as game unique ID 413
in `taskforce:Taskforce1`. The synchronous constructor completed in 113,453 µs.

At revision 1 the bridge captured the supported portable state as checkpoint
`checkpoint:unit:crimean-tom:phase2-lifecycle-knox-1:1:7f734124d76e`, with
SHA-256
`7f734124d76ec50143e5fda84dbccad675e1e4aff7a975b7d7d7aee731c427a6`.
The checkpoint contains the catalog tuple, ownership/task-force identity,
position, heading, speed, telegraph, and enabled/destroyed state. Damage, fuel,
magazine, sensor, formation, and hosted-aircraft application remain explicit
unsupported fields.

A fenced update moved the unit to latitude `12.940323947793704`, longitude
`43.1439973020687`, heading 172, and telegraph 1. Readback returned 5.4 knots,
revision 2, the same stable identity, and the same game object. The main-thread
operation took 89 µs. Repeating the exact revision-1 command and idempotency
key returned the original revision-2 result with `idempotentReplay=true`.
A different command at stale revision 1 failed with HTTP 422
`stale-revision`; a command at authority epoch 2 failed with HTTP 422
`stale-authority-epoch`.

Native `ObjectBase.Disable` then suspended the unit at revision 3 in 1,603 µs.
The live world changed from 84 observed objects, 19 units, and one campaign
identity to 83, 18, and zero respectively, with no remaining match for the
owned tag. The bridge retained the owned record and read the object back as
disabled. An exact duplicate suspend was idempotent.

Native `ObjectBase.Enable` restored the same game unique ID 413 at revision 4
in 1,137 µs. The world returned to 84 observed objects, 19 units, exactly one
matching campaign identity, and the exact pre-suspend canonical world hash
`b11ffa67f1aea7ad0caadb1eb7695e79ca781fe3570405756c7519ff6297ae39`.
An exact duplicate enable was idempotent. A revision-4 checkpoint produced
SHA-256
`0506898eea62adef8c8f97bcc3a0f1a84aced1d5f848209e3ac15730c27593e3`,
capturing the updated kinematics and enabled state.

The complete path ran while paused on Unity's main thread. `Player.log` showed
the bridge startup marker and no runtime exception or error entry during the
lifecycle exercise.

## Checkpoint-backed rematerialization

The next live run captured revision-1 checkpoint
`checkpoint:unit:crimean-tom:phase2-restore-knox-1:1:005e41ac4ff6`, SHA-256
`005e41ac4ff65591fecf521b62a4be7b240f50c1560b4883c454730c696bc65a`.
The unit was then moved away from the checkpoint, advanced to revision 2, and
suspended at revision 3. Restoring the checkpoint re-enabled retained game ID
355 at revision 4 in 1,091 µs and restored latitude 12.935, longitude 43.135,
heading 147, speed zero, and telegraph zero. The complete world canonical hash
before the update and after restore was exactly
`cd1664e704279ee2020a441a4557ac7daaac8e5a6b8c2bec7638009a85fc265b`.
An exact duplicate restore returned `idempotentReplay=true`.

The bridge then suspended the unit and exited the tactical session through the
typed UI seam. After loading the same scenario again through typed navigation,
advancing its briefing, and pausing, the old Unity reference was absent. The
same checkpoint reconstructed a new supported vessel in 102,216 µs with
`reconstructedAfterSceneLoss=true`, revision 6, exactly one matching external
identity, and every supported field restored. Native game ID 355 happened to
be allocated again, but is not part of the cross-scene identity contract.

Finally, the companion was intentionally stopped and restarted while Sea Power
remained loaded. The pipe session changed from
`9740287d832044b0a1fa9af531c31a6c` to
`aa69718c05a64b7e95652bd92dbe1957`. An immediate mutation failed with HTTP 422
`mutation-disabled`. After explicit arming for the new session, suspend advanced
revision 6 to 7 and checkpoint restore advanced it to 8 while retaining game ID
355 and all supported state. The only exception-shaped log entry was the
expected `EndOfStreamException` recording the deliberate companion disconnect;
there was no Unity mutation failure.

The implemented comparisons and tolerances are published in
[Phase 2 surface field tolerances](phase-2-surface-field-tolerances.md). A
restore with the prior idempotency key but a different valid checkpoint hash
failed with `idempotency-conflict`. An unsupported Ticonderoga catalog tuple was
rejected by the companion, and a correctly scoped but unowned entity was
rejected by the plugin without touching a scenario object.

The final deployed build enforces those comparisons before returning success.
A fractional live pass materialized latitude `12.935123456` and heading
`147.125`, updated to latitude `12.945123456` and heading `211.25`, suspended,
and restored the original fractional position/heading at revision 4 without a
tolerance assertion failure.

## Quiet-removal disposition

Inspection of the pinned native methods closes the memory-releasing removal
spike negatively. `destroyObject(score: false)` still sets the destroyed flag,
emits destruction semantics, detaches relationships, and cascades into child
objects. `OnReturnedToPool()` only clears an ECS reference and is not an owner
for task-force, manager, systems, reactive state, or GameObject teardown. A
plugin-composed hybrid would therefore risk both invented losses and leaked or
orphaned state.

P2 is explicitly narrowed to Sea Power's supported retained suspension. It
removes the unit from active object/task-force collections and ECS updates but
retains the constructed object and its memory. Scheduler capacity must count
that retained memory; suspension is CPU/update relief, not memory reclamation.
Cross-scene reconstruction is allowed only after Unity itself has disposed the
old scene object.

## Transactional fault injection and resource accounting

The final P2 run exercised every declared Unity-mutation boundary using the
exact confirmation `inject-owned-mutation-failure`. Three materialization
faults retained one tagged, suspended recovery object apiece while the active
world stayed at its original 18 scenario units. The resource snapshot reported
three owned records, zero active objects, three suspended-retained objects,
three recovery-required objects, and three retained constructed objects.

Three update faults and two suspension faults each returned structured HTTP 422
`mutation-injected-failure` results with operation, step, and recovery state.
Every rollback retained revision 1, the original supported fields, exactly one
active owned object, and canonical world SHA-256
`89494f3674f47bbf431a063baea63f5137d6f39167489f13b4c50776da135ee1`.

After a real update and suspension, restore faults were injected after state
application, enable, and readback. All three restored the source suspension at
revision 3 with `recoveryRequired=false`. A normal retry restored the checkpoint
at revision 4 and returned the same canonical hash.

The scheduler-facing resource reader explicitly counts active,
suspended-retained, detached, recovery-required, and retained-constructed
objects. It reports `memoryReclamationSupported=false` and policy
`active-and-suspended-retained-objects-consume-materialized-capacity`.

A focused cleanup run proved the only registry-only release path. A failed
materialization remained suspended and invisible to the 18-unit scenario. Typed
tactical exit let Unity destroy the scene object, changing it to
`detached-after-scene-loss`. Release then required the exact owned assignment,
epoch, revision, recovery flag, absence of checkpoint, and absence of any live
Unity object. It removed the record without a Unity mutation; an exact duplicate
replayed, a foreign record failed closed, and all owned resource counts returned
to zero.

The detailed requirements mapping is in
[Phase 2 exit-gate matrix](phase-2-exit-gate-matrix.md).

## Explicit limitations and gate disposition

The first materializer intentionally reports damage, fuel, magazine, sensor,
formation, and hosted-aircraft state application as unsupported. A failure
after the external ownership tag is attached suspends the tagged object rather
than deleting it or emitting an ordinary destruction outcome. Cleanup logic may
act only on both the exact external tag and the bridge-owned registry entry.

The architecture is now formally narrowed to retained suspension and
checkpoint-backed rematerialization. Duplicate suspension/rematerialization,
unsupported catalog rejection, scene reload, companion reconnect, per-field
tolerances, and synchronous costs have live evidence.

Exit gate P2 is complete for the narrow allow-listed Knox contract. Unsupported
fields remain explicit promotion blockers, and every additional entity family
still requires its own later gate.
