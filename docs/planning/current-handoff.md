# Current implementation handoff

## Repository state

This handoff records the state at the end of the 20 August 2026 implementation
session. The repository remains in Phase 0. No authoritative campaign-state
mutation is enabled, and no campaign simulation has been implemented yet. The
first allow-listed UI automation command only opens a native force-management
panel; it does not purchase, confirm, save, or resolve anything.

The last committed revision is `ed22f12`. A large earlier body of work is
already staged, while the latest implementation and documentation changes are
unstaged. Preserve that split unless the user explicitly requests staging or a
commit.

Sea Power and the Crimean Tom companion were stopped after live verification.
The deployed bridge remains current and hash-matched. Agents may launch Sea
Power when runtime verification or interactive UI work requires it, minimize
it when not used interactively, and close agent-launched applications afterward.

## Latest completed Phase 0 slice

The hardened read-only bridge currently provides:

- strict length-prefixed frames, bounded queues, priority lanes, deadlines,
  cancellation frames, and session fencing;
- a current-user-only named pipe and authenticated loopback HTTP API;
- explicit `starting`, `waiting-for-game`, `handshaking`, `connected`, and
  `stopping` companion states, plus a monotonically increasing session
  generation and transition timestamp;
- removal of queued Unity commands and queued responses belonging to an ended
  session, preventing stale authorized work from crossing a reconnect;
- graceful broker shutdown that disconnects the game and completes pending
  callers;
- direct and real-host control-token authentication tests;
- main-thread ping, capability, scene inspection, and a live world snapshot,
  plus runtime file identity and fail-closed authority blockers;
- a typed campaign-management read and typed `open-task-force-builder` UI
  action, with explicit preconditions and a verified loaded-panel
  postcondition.

`get-world-snapshot` and `GET /game/world` now expose scenario-scoped task-force
identities and Sea Power's own per-unit `UniqueID` values. Unit records include
task-force ownership, name, class, runtime type, nation, latitude/longitude,
altitude, heading, speed, and destruction state. The command is read-only and
runs through the existing Unity-main-thread dispatcher. `pnpm game:world`
provides the operator-facing command.

The companion now enforces one absolute command lifetime after a pending slot
is acquired. The same deadline token governs write-lock acquisition, the
complete pipe-frame write, and correlated response wait. The shorter transport
write timeout remains a separate linked bound; interrupted writes dispose the
session so a partial frame cannot poison subsequent parsing. Caller
cancellation, command expiry, transport failure, and game disconnection retain
distinct results. `GamePipeBrokerOptions` provides short timeout and one-slot
test seams while retaining the five-second production default.

The real-pipe deadline test negotiates a session, verifies the advertised short
deadline, withholds the response, observes a correlated Control-priority
`cancel-command` with reason `deadline-elapsed`, requires
`game-command-timeout`, then completes a follow-up command through the same
one-slot broker to prove the pending slot was released.

The runtime bridge also runs a one-shot initialization self-test on a managed
worker. It invokes the actual `GameCommandDispatcher` with a valid future-dated
ping envelope and requires a structured `wrong-execution-thread` response. The
top-level capability result exposes only `pending`, `passed`, or sanitized
`failed:*` values and remains outside the stable compatibility fingerprint.
Pending or failed results block authority. The control smoke now requires
`passed`.

Control-token files are now created with a current-user-only Windows ACL in the
same atomic file-creation operation. Existing token files must already be owned
by the current user before the companion will use them. The real-host
integration test starts the actual companion on a temporary loopback port and
requires missing and incorrect tokens to return structured `401` responses,
while the generated token reaches `/health` successfully.

The companion lifecycle script now verifies that a PID belongs to the expected
companion command line before treating it as live or stopping it. Startup clears
stale PID, token, stdout, and stderr artifacts; failed startup stops its child
and removes transient PID/token state. The integration test seeds its own PID as
a recycled unrelated PID plus stale token/log markers, then proves safe startup,
artifact replacement, authenticated health, and clean stop in an isolated run
directory.

The actual broker now has a Windows kernel ACL test that impersonates the
anonymous security token, requires access denial before protocol negotiation,
then connects and handshakes as the current user through the same listener. A
stronger proof using a second named local account remains outstanding because
this medium-integrity session has neither that account's credentials nor token
management privileges; no account or system-policy changes were made.

The earlier collection of narrow unit tests was removed. Repository policy now
prefers working features and a small number of broad integration scenarios.
The remaining passing integration test starts the real companion through its
lifecycle script, verifies stale-artifact cleanup and token enforcement,
connects a protocol-speaking game peer over a unique current-user pipe, then
traverses health, ping, capabilities, scene, world, campaign management, and
typed task-force-builder automation end to end. The builder result also proves
that ship, submarine, and air roster groups are populated and returns purchased
draft units with stable instance ID, unit/category/variant, nation, selected
loadout, quantity, cost, and hosted-air assignments. Its cleanup waits for
redirected log handles to close instead of racing companion shutdown.

The freshly deployed bridge loaded on Sea Power `0.8.2-23444+s358`, returned a
ready runtime fingerprint and `executionThreadGuardSelfTest: passed`, and passed
the complete control smoke. Ping and scene both ran on Unity thread 1. The
wrapper's hash implementation now uses .NET SHA-256 directly, avoiding a
Windows PowerShell module-resolution failure inherited through `pnpm`. The
build completed with zero warnings and errors, and the built/deployed DLL
hashes matched.

The same companion survived two consecutive full Sea Power process restarts.
Game PIDs changed `8980 -> 30452 -> 33264`, pipe sessions changed each time,
session generation advanced `1 -> 2 -> 3`, and every restart passed the full
control smoke with the same compatibility fingerprint. Rapid restart exposed a
Steam cooldown: one launch URI was ignored. The launch wrapper now retries the
same sanctioned Steam app URI every ten seconds within a bounded window; the
next immediate restart exercised and passed on attempt 2.

Fault injection then force-terminated validated companion PID `32676`. Startup
consumed the stale lifecycle state, replacement PID `44520` started, and the
still-running game reconnected and passed the full smoke. Finally validated game
PID `33264` was force-terminated. The companion moved to `waiting-for-game`
with no session, Steam relaunched game PID `508`, and the fresh plugin/session
again passed the full smoke and preserved the fingerprint.

Live feature verification then loaded Sea Power `0.8.2-23444+s358` scenario
`01 Red Sea Breakout - NATO (1988)`. `/game/scene` reported 41 total objects and
3 task forces; `/game/world` returned all 3 task forces and 18 units. Real
records included Yorktown, coastal installations, P6/Osa boats, Petya, and
Krivak. Two consecutive captures retained the same 18 unit IDs while Yorktown
(`unit:8`, `taskforce:Taskforce1`) moved at about 12.79 knots on heading 144.77,
proving both scenario-scoped identity stability and live state updates.

## Working port-operations shell

`content/campaigns/crimean-tom-port-operations` is now a disposable Task Force
Mode campaign projection. The build/deploy wrapper copies every file into Sea
Power's user campaign directory and verifies its SHA-256 hash. It deliberately
uses a repository-owned roster rather than a cross-campaign path: source
inspection proved that `RosterFile` is resolved relative to the authored
campaign, which explained the initially empty catalog.

Live verification on Sea Power `0.8.2-23444+s358` showed the development
campaign, opened its one-node `PORT OPERATIONS / MAINTENANCE` map, and used the
typed bridge action to open the native Task Force Builder. The builder displayed
500/500 points and a populated catalog containing ships such as Knox, Perry,
Spruance, Kidd, Virginia, and their configured loadouts, plus submarine and air
groups. No purchase or confirmation was made. No campaign save was created and
no existing user save was read or changed.

This shell is a maintenance draft editor, not a second campaign engine. The
external Crimean Tom service remains authoritative for theater time, force
location, readiness, stores, repair, reinforcement, and outcomes. The intended
workflow is:

1. pause only the player's administrative transaction at a durable campaign
   revision;
2. materialize that port's eligible docked force, reinforcement pool, budgets,
   and rules into the shell roster/save projection;
3. perform a controlled Sea Power restart when the projection changes, then let
   the player use the native builder/refit UI;
4. read the resulting draft through typed bridge state, validate it against the
   original revision, and commit one external campaign transaction;
5. rematerialize or restart as needed while the rest of the theater continues
   under the external simulation.

The referenced built-in mission is temporary UI/map context only. Starting or
finishing it must never resolve a Crimean Tom battle, advance the authoritative
campaign, or inject tactical results. A future empty/non-resolving maintenance
container is preferable once the required native UI context is understood.

## Next implementation slice

Turn the working builder entry point into a useful read-only maintenance draft:
extract selected/purchased unit instances, variant/loadout choices, points,
repairable damage, rearm state, and reinforcement availability using stable
source identities. Keep the external campaign revision and validation boundary
explicit. Do not add generic reflection automation or make the native campaign
save authoritative. Add typed add/remove/refit/confirm actions only as this
vertical flow naturally reaches those controls; do not stop implementation to
build unrelated menu-button links.

After the draft can round-trip without committing it, generate the local roster
projection from a disposable external port-state fixture, perform the controlled
restart, import the edited draft, validate it, and commit it to that fixture.
The first broad live scenario should cover docked refit plus reinforcement in a
single flow and prove that unrelated theater entities continue advancing.

## Remaining Phase 0 gates after that slice

- Complete the stronger named-local-account pipe rejection proof when a
  disposable second-user credential or pre-authorized helper is available.
- Expand fault injection to commands interrupted during write/execution when a
  deterministic suspension hook exists; process-level crash/restart coverage
  is complete.
- Pin the bridge compiler/SDK, trial C# 13 through source-level metadata shims,
  and verify compile, IL/API compatibility, clean load, and command smoke.
- Keep catalog/mod fingerprints and deterministic-hook method fingerprints
  fail-closed until their Phase 1 implementations exist.

## Verification commands

Use the repository automation and the repository-local runtime skill:

```powershell
pnpm test
pnpm build
pnpm dev
pnpm game:control-test
pnpm build
```

The final `pnpm build` intentionally stops the companion and Sea Power after
the live check while leaving the verified DLL deployed. Do not stage or commit
unless the user explicitly asks.
