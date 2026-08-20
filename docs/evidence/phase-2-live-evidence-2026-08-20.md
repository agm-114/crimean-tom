# Phase 2 live evidence — 2026-08-20

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

## Explicit limitations and next gate work

The first materializer intentionally reports damage, fuel, magazine, sensor,
formation, and hosted-aircraft state application as unsupported. A failure
after the external ownership tag is attached suspends the tagged object rather
than deleting it or emitting an ordinary destruction outcome. Cleanup logic may
act only on both the exact external tag and the bridge-owned registry entry.

This slice does not complete P2. Checkpoint capture, fenced update, duplicate
update, stale revision/epoch rejection, and native suspension/enable are now
proven. Native suspension is currently the supported reversible lifecycle; it
does not yet claim memory-releasing removal.

The next slice must either prove safe owned quiet removal or formally narrow
the architecture to retained suspension, then rematerialize from a checkpoint.
It must also exercise duplicate removal/rematerialization, unsupported catalog
rejection, injected partial failure, scene reload, companion reconnect,
resource relief, per-field tolerances, and scheduler-visible synchronous cost
before the P2 exit gate can pass.
