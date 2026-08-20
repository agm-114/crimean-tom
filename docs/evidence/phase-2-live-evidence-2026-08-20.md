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

## Explicit limitations and next gate work

The first materializer intentionally reports damage, fuel, magazine, sensor,
formation, and hosted-aircraft state application as unsupported. A failure
after the external ownership tag is attached suspends the tagged object rather
than deleting it or emitting an ordinary destruction outcome. Cleanup logic may
act only on both the exact external tag and the bridge-owned registry entry.

This slice does not complete P2. The next lifecycle slice must add portable
checkpoint capture, fenced update, native suspension/enable measurements,
owned removal semantics, and rematerialization. It must then exercise duplicate
update/delete, stale revision and epoch, injected partial failure, scene reload,
and companion reconnect before the P2 exit gate can pass.
