# Adversarial architecture review — 2026-08-20

## Provenance

| Field | Value |
| --- | --- |
| Date | 2026-08-20 |
| Base commit | `ed22f12` (plus uncommitted working-tree changes) |
| Game build | Sea Power `0.8.2-23444+s358` |
| Reviewed documents | `architecture/system-architecture.md` (normative), `planning/implementation-plan.md`, `architecture/federated-simulation.md`, `architecture/dynamic-campaign-design.md`, `research/runtime-architecture.md`, `research/tactical-simulation.md`, `research/campaign-systems.md`, `research/data-and-modding.md`, `operations/testing.md`, `README.md` |
| Reviewed code | `src/SeaPower.RuntimeBridge/` (then named `src/CrimeanTom/`), `src/CrimeanTom.Contracts/`, `src/CrimeanTom.Companion/`, `scripts/crimean-tom-control.ps1`, `scripts/sea-power.ps1` |
| Game-code evidence | Verified against the decompiled `Seapower-Scripts` tree. Game paths below are relative to `decompiled/game/Seapower-Scripts/`, which is git-ignored — regenerate with `pnpm decompile`. |

The review was performed adversarially: the goal was to falsify the concept
before further development, not to confirm it. Game-feasibility claims are backed
by cited decompiled source lines rather than inference.

### Revisions

- **2026-08-20, same day:** F3 revised from Critical to High. The original text
  stated Sea Power has no floating origin. It does — the game calls it
  *relocation* (`RenderPosition.checkAndPerformRelocation`,
  `RelocationHandler.relocate`), so the local origin is re-centred on the camera
  past `RebaseToOrigin` rather than fixed at mission load. The constraint is
  narrower than first written but still one-bubble-per-process; the recommended
  change is unaltered. Executive-verdict risk 2, the F3 row and deep dive, the
  scalability ceiling, revision 7, and one test were updated to match.
- **2026-08-20, verification pass:** F4 and F5 corrected after inspecting the
  native lifecycle and save paths. `ObjectBase.Disable/Enable` provides reversible
  simulation suspension but retains the allocated object; it is not full
  dematerialization. `SaveLoadManager.WriteWeaponData` and `SceneCreator` do
  persist/recreate task-force `WeaponBase` objects, though completeness and gun
  projectile persistence remain unproven. Permanent-fence recommendations were
  replaced with experiment-gated rules.

---

## Executive verdict

**Requires architectural redesign.**

The redesign is scoped, not total. The *continuous external campaign + one Sea
Power client as a tactical worker* half (architecture decisions 1, 2, 6, 7, 9,
10; plan phases P0–P5) is sound and mostly survives review. The *federated* half
— architecture decisions 4, 5, and 8, the whole of `federated-simulation.md`, and
phases P6–P7 — rests on four properties that Sea Power's actual code does not
have and that a thin plugin cannot add. Those are not tuning problems; they
invalidate the primary/shadow/observer premise as written.

Three largest risks:

1. **Redundant Unity simulation cannot converge.** Sea Power's physics timestep
   is chosen from the local machine's measured FPS (`Seapower/GameTime.cs:314-323`),
   its logical clock advances by real frame time (`Seapower/GameTime.cs:336`), its
   per-object update order is spawn-order-dependent (`Seapower/GameMain.cs:51,144`),
   and its combat randomness comes from one global sequential
   `Unity.Mathematics.Random(12u)` (`Seapower/Globals.cs:236`) plus an *unseeded*
   `System.Random` in unit AI (`Seapower/AI.cs:186`). Two clients given identical
   inputs will diverge immediately and permanently, so shadows will sit in
   `Diverged` and warm promotion never becomes available. `federated-simulation.md`
   § "Divergence and resynchronization" assumes drift is bounded and correctable;
   the evidence says it is unbounded and structural.
2. **A Sea Power process is one tactical bubble, not an elastic worker.** The
   world has a floating origin, but it follows the *camera*: local coordinates
   are relative to `Globals._currentCenterTile`, and
   `RenderPosition.checkAndPerformRelocation` re-centres on the camera only once
   it passes `Globals._rebaseToOrigin` (options INI, default 1000 units ≈ 67 km
   at 67.2 m/unit). That is a relocation trigger, not a proven terrain or
   precision radius. One client nevertheless has one current local origin,
   positioned by where the player is looking rather than by what the scheduler
   assigned. `Time.timeScale` and pause are additionally
   process-global (`Seapower/GameTime.cs:70-80,299`). "Stream the entities each
   client needs" and per-assignment "permitted time scale"
   (`system-architecture.md` § Time model) are not implementable per-island
   inside one process.
3. **Authority and fog of war are mutually exclusive on a player client.** Being
   primary for an entity means holding its true state in-process.
   `dynamic-campaign-design.md` claims elasticity by giving player clients
   authority for nearby units, but any client that is primary for — or even
   renders a contact of — an opposing task force must instantiate real
   `ObjectBase` ground truth in a process the player controls.
   `federated-simulation.md` acknowledges this for full-state observers only; it
   applies to *every* tactical role.

---

## Architecture strengths

These materially reduce risk and should be preserved through any redesign:

- **Separation of the pure campaign engine from Unity** (`system-architecture.md`
  § Deployable components, decision 6). The core never referencing `ObjectBase` is
  the single highest-value decision in the repository; it is what makes the
  campaign survivable across game patches.
- **Fencing by epoch inside the same transaction as the append**
  (`federated-simulation.md` § Health and failover, the `begin transaction`
  block). This is stated correctly — check-then-append is explicitly called out as
  a race. Most designs at this stage get it wrong.
- **Refusal to claim scale numbers before benchmarks** (`system-architecture.md`
  § Performance and capacity policy, `implementation-plan.md` § Deferred
  decisions). The deferred-decision table with phase deadlines is genuinely good
  engineering hygiene.
- **Explicit non-goals list** (`system-architecture.md` § Explicit non-goals),
  especially "Bit-identical Unity lockstep" and "Reproducing every private Unity
  object in the portable save schema."
- **Evidence labels in `docs/README.md`** (Observed / Code evidence / Inference)
  and `testing.md`'s layer table. These make the docs falsifiable, which is why
  this review could be concrete.
- **The current code slice is genuinely thin and read-only.**
  `GameCommandDispatcher.cs` has a three-command allow-list and
  `MutatingCommandsEnabled = false`; work is marshalled through
  `RuntimeBridgeBehaviour.Update` (then `CrimeanTomRuntime.Update`). The shape is right.

---

## Critical findings

| ID | Severity | Category | Finding | Concrete failure | Blocks phase | Recommended change |
| --- | --- | --- | --- | --- | --- | --- |
| F1 | Critical | Game feasibility | Physics timestep is selected from local FPS (`Seapower/GameTime.cs:314-323`) | Client A at 45 fps steps at 1/60 s, client B at 28 fps steps at 1/30 s; a Harpoon's integrated flight path differs by hundreds of metres before terminal | P6 | Drop same-unit Unity redundancy; make failover cold-restore from checkpoint |
| F2 | Critical | Correctness | Global sequential RNG shared by all weapons (`Seapower/Globals.cs:236`) and unseeded AI RNG (`Seapower/AI.cs:186`) | Missile `_insDriftValue`/`_motorPerformance` (`Seapower/Missile.cs:57,61`) differ per replica; AI preset-attack branch (`Seapower/AI.cs:377`) differs per *process launch* | P6 | Treat Unity outcomes as authoritative-from-one-client only; never compare |
| F3 | High | Game feasibility | One local origin per process, re-centred only by camera movement past `RebaseToOrigin` (`Seapower/RenderPosition.cs:758-771`, `Seapower/RelocationHandler.cs`) | Scheduler assigns a Baltic island to a client whose camera is in the Med; at 67.2 m/unit the island sits ~3x10^4 units out with unvalidated float, terrain, sensor, and physics behavior, and only re-centres if the player looks there | P4 (design), P5 (hard) | Bind a client session to a measured tactical region; refuse assignments outside the validated radius |
| F4 | High | Game feasibility | `ObjectBase.Disable/Enable` is reversible and save-aware, but retains the constructed object, root, systems, and `GameMain` entry; `destroyObject` is still a semantic kill | Treating Disable as dematerialization does not free the capacity the scheduler expects; using `destroyObject` invents a loss | P2 | Validate Disable/Enable as quiet suspension, then separately prove any memory-releasing teardown |
| F5 | High | Game feasibility | In-flight weapons hold live references and absolute-time state; native saves enumerate task-force `WeaponBase` objects, but restoration completeness is unproven and pooled gun rounds have no identified save path | An island transfer silently loses a guidance/timer/decoy/gun-round state that the native path omitted | P5 | Run active-weapon round trips; fence only unsupported state families |
| F6 | Critical | Security | Authority requires ground truth in a player process | A co-op player assigned primary for a Soviet SAG can read the entire Red order of battle with UnityExplorer | P7 | Player clients may be primary only for their own coalition's islands |
| F7 | Critical | Architecture | `system-architecture.md` § Time model assigns each assignment a "permitted time scale"; `Time.timeScale` is process-global (`Seapower/GameTime.cs:70-80`) | Two islands in one client cannot run at 1x and 60x; pausing one pauses both | P4 | One island per client session, or drop per-assignment time scale |
| F8 | High | Correctness | Object update order is reverse insertion order over a `List<ObjectBase>` (`Seapower/GameMain.cs:51,144,179`) | Materializing a unit mid-mission permanently changes the relative update order of every existing unit; two replicas that materialized in different orders diverge with zero floating-point involvement | P6 | Same as F1 |
| F9 | High | Architecture | Hash agreement is used as a promotion eligibility criterion (`federated-simulation.md` § Health and failover, rank 4) while the same document says hashes cannot prove correctness | A replica that quantizes into agreement but holds a different `UnitInternalContact` solution is promoted and immediately mis-engages | P6 | Remove hash agreement from ranking; use it only to *reject* |
| F10 | High | Correctness | `GameTime.time` is a float accumulator (`Seapower/GameTime.cs:37,346`) | At 3000x compression the mission clock reaches ~50 sim-hours per wall-clock minute; near t≈1.3x10^5 s a 1/60 s step is the same magnitude as the float ULP and the clock quantizes, then stalls | P4 | Periodic session rebase; treat client `GameTime.time` as a session-local offset, never as campaign time |
| F11 | High | Architecture | Interaction islands assume task forces can be split; `Taskforce` owns the plotting table, data link, and the `WeaponAllocator` auction (`tactical-simulation.md` § AI layers) | Two ships of one task force under different primaries have no shared track picture; islands therefore collapse to one-per-task-force and then merge across engagements | P5 | Declare the task force the minimum island; forbid sub-task-force partition |
| F12 | High | Product/Correctness | No rule for outcomes shown to a player but never committed | Primary dies after rendering a missile hit; canonical state says no hit; the player already watched the ship burn | P4 | Commit-before-display for irreversible outcomes (kills, hits, detections) |
| F13 | High | Performance | Ship materialization is a synchronous blocking load — `createShip` forces `loadAsync: false` (`Seapower/ObjectsManager.cs:452`) then `Physics.SyncTransforms()` | P2A's "per-frame work budget" cannot be honoured; a task-force materialization is a multi-second freeze, which also breaks the lease heartbeat | P2 | Measure it first; budget in *seconds per island*, and suspend lease timers during materialization |
| F14 | High | Correctness | Above the 10x physics cap the game switches to an approximation with inflated `fixedDeltaTime` and divided `sensorDeltaTime` (`Seapower/GameTime.cs:271,353-360`) | P5's "operational outcomes fall within documented calibration envelopes" calibrates against a Unity model that is itself a different model above 10x | P5 | Calibrate only at ≤10x; forbid tactical fidelity above the cap |
| F15 | High | Security | Companion exposes an unauthenticated generic passthrough: `POST /game/commands/{commandType}` with arbitrary body (`src/CrimeanTom.Companion/Program.cs:33-47`) | Any local process (or a browser via a form POST to `127.0.0.1:5137`) reaches the plugin dispatcher; today harmless, on the day P2 enables mutation it is remote code effect on the game | P2 | Delete the generic route; one endpoint per allow-listed command, plus a local token |
| F16 | Medium | Security | Named pipe created with no ACL and `maxNumberOfServerInstances: 1` (`src/CrimeanTom.Companion/GamePipeBroker.cs:136-141`); no message-size or `_pending` bound; `ReadLineAsync` buffers unbounded lines | Any local user connects first and impersonates the game, or sends a 2 GB line and OOMs the companion | P0 | `PipeSecurity` for the current user; length-prefixed frames with a cap |
| F17 | Medium | Security | Full .NET stack traces returned over the wire (`src/SeaPower.RuntimeBridge/GameCommandDispatcher.cs`, then under `src/CrimeanTom/`) | Contradicts `system-architecture.md` § Local and remote protocols "structured error codes without leaking unrestricted game state" | P0 | Structured code + correlation ID on the wire; detail to the local log only |
| F18 | Medium | Correctness | `game:control-test` asserted `ping.mainThreadId -eq $scene.mainThreadId`, but that value was captured once in `Awake` and echoed back by the then-named `CrimeanTomRuntime` | The assertion was vacuous; it proved nothing about the executing thread | P0 | Compare `Environment.CurrentManagedThreadId` *at execution* against the captured ID and return both |
| F19 | Medium | Operations | Plugin reads private fields `_listOfAllObjects` / `_taskForces` (`src/SeaPower.RuntimeBridge/GameCommandDispatcher.cs`) with no assembly-version guard | A patch renames the field; plugin throws on every scene query with no fail-closed signal | P1 | Fingerprint the managed assemblies, not only the INI catalog |
| F20 | Medium | Operations | All `create*` return `null` when `Globals._useAssetBundles` is true, a user option (`Seapower/OptionsManager.cs:837`) | Materialization silently fails on a user machine with the option set | P2 | Assert the flag during handshake; refuse tactical assignment otherwise |
| F21 | Medium | Correctness | Sensor and flight-deck persistence are *optional* game settings (`Seapower/SaveLoadManager.cs:1063-1064`) | Checkpoint fidelity varies with a config file the campaign does not own | P2 | Read and pin these in the capability handshake |
| F22 | High | Architecture | Nothing enforces single-writer on the durable store before P8 | Two coordinators started against one SQLite file both allocate epoch E+1 | P3 | A CAS'd writer-epoch row from the first day of P3 |

### F1 / F2 / F8 — why redundant Unity simulation is falsified, not merely hard

`system-architecture.md` decision 8 says "External simulation is deterministic;
Unity is convergent," and `federated-simulation.md` § Divergence and
resynchronization gives a five-step escalation from hash compare to checkpoint
reload. The escalation ladder is a reasonable design *if* the base divergence rate
is low and driven by float rounding. It is not.

Concretely, in `GameTime.OnUpdate`:

```csharp
if (Singleton<Fps>.Instance.getFps() < 30f)  { Time.fixedDeltaTime = 0.0333333f; }
else if (Singleton<Fps>.Instance.getFps() > 40f) { Time.fixedDeltaTime = 0.0166667f; }
...
deltaTime = TimeCompression * Time.deltaTime / Time.timeScale;
```

The physics step is a function of the local machine's frame rate with hysteresis
at 30/40 fps, and logical delta is a function of wall-clock frame duration.
Missiles are `WeaponBase : ObjectBase` and so integrate in `GameMain`'s
per-object fixed-update loop (gun rounds run separately in `ProjectileManager`).
Either way a shadow on a slower machine integrates a missile at half the rate of
the primary — a first-order difference, not rounding.

Then `Seapower/Missile.cs:57,61`:

```csharp
missile._insDriftValue = Globals._unityRandom.NextFloat() - 0.5f;
missile._motorPerformance = 1.02f - Globals._unityRandom.NextFloat() / 25f;
```

`Globals._unityRandom` (`Seapower/Globals.cs:236`) is one process-global
sequential stream, also consumed by biologics (`Seapower/Biologic.cs:704-736`) and
TMA variance (`Seapower/Missile.cs:864`). Draw order depends on how many objects
exist and in what order they updated. `AI._aiRandom` (`Seapower/AI.cs:186`) is
`new System.Random()` with no seed at all — different on every process launch.
`Seapower/AI.cs:377` uses it to choose between preset attacks.

And `GameMain.PerformFixedUpdate` iterates `_objects` in reverse insertion order,
where insertion order is spawn order. Materializing a unit changes the update
order of everything else.

**Failure scenario.** A shadow is spawned per the `federated-simulation.md`
promotion sequence. At tick T the coordinator requests hashes. The primary reports
a missile at bearing 271.4° with a seeker lock; the shadow reports 271.9° with no
lock because it drew a different `_motorPerformance` and its AI took the other
preset-attack branch three seconds earlier. The service escalates: subsystem
hashes (differ), event replay (cursors already match), correction delta
(`federated-simulation.md` warns teleporting a missile invalidates guidance —
correct), checkpoint reload (native weapon restore is not yet validated, see F5).
The shadow is marked `Diverged` and demoted. This repeats within seconds for every
engagement. The redundancy layer never produces a promotable replica, so the P6
exit gate "a warm shadow takes authority within the measured recovery objective"
cannot be met.

**Smallest credible mitigation.** Delete same-unit redundant Unity simulation from
the normative architecture. Replace warm shadow failover with: (a) the external
operational resolver as the sole failover target, and (b) optional *cold*
reconstruction into a different client from the last committed portable checkpoint
at a quiesce boundary. Keep the epoch/lease/fencing machinery — it is still needed
to stop a returning primary — but stop claiming hot failover.

**Tradeoff.** Failover time goes from "cursor catch-up" (sub-second, aspirational)
to "checkpoint restore + island re-materialization" (seconds, and F13 says
possibly many). Fidelity drops for the duration. In exchange, P6 becomes
achievable and you stop paying a whole second machine per redundantly simulated
island for a replica that can never be promoted.

### F3 / F7 — a client is a bubble, not a worker

Sea Power *does* have a floating origin — the game calls it relocation, which is
why a search for the usual names misses it. Every geo↔local conversion routes
through `Utils.longLatToLocalV3(geo, Globals._currentCenterTile)` at 330.71 Unity
units per 0.2° tile, where one Unity unit is 67.2 m
(`Utils.heightInMeterToUnity` multiplies by `0.0148809375`). Objects carry a
double-precision `GeoPosition` as their canonical location and the transform is
derived from it.

`RenderPosition.checkAndPerformRelocation` compares the camera's horizontal local
position against `Globals._rebaseToOriginSq`; `_rebaseToOrigin` is read from the
options INI (`WORLD_SETTINGS/RebaseToOrigin`, default `1000` units ≈ 67 km). Past
that, the game re-centres on the camera and `RelocationHandler.relocate` shifts
every object, projectile, trail, cloud chunk, terrain chunk, and subscriber in a
single frame. Objects also self-correct in `ObjectBase.OnFixedUpdate` when
`_lastCenterTile != Globals._currentCenterTile`.

The constraint is therefore weaker than "the region is fixed at mission load,"
but it is still real and still one-per-process: **there is one camera-driven
local origin.** The ~67 km value is only the rebase trigger; the usable terrain,
sensor, and physics radius remains an experiment.

**Failure scenario.** `dynamic-campaign-design.md` § System boundary promises the
service "streams the entities each client needs." A player watching the Norwegian
Sea has spare capacity, so the scheduler assigns them a Black Sea island. At
67.2 m/unit the island materializes ~3x10^4 units from the current origin, with
terrain, sonar, bathymetry, collision, and precision behavior outside anything
the architecture has validated. Nothing in the spawn path checks that distance.
The origin will only
follow if the *player's camera* travels there — which is not something the
scheduler controls, and doing so moves every other assigned island equally far
from the new local origin.

Compounding this, `Time.timeScale` is one value per process and
`GameTime.IsPaused()` is `Time.timeScale < 0.05f` (`Seapower/GameTime.cs:299`).
`system-architecture.md` § Time model gives each assignment a "permitted time
scale" and says "Multiplayer has no unilateral global pause" — but the player's
ESC menu sets `Time.timeScale = 0` for everything in that process, including any
island they are primary for on someone else's behalf.

**Smallest credible mitigation.** Make the client session, not the assignment, the
unit of geography and time: a session declares one center tile and one time scale
at handshake; the scheduler may only assign islands within a validated radius of
that tile; a time-scale or region change is a session teardown and rebuild. Add a
hard plugin-side range assertion before any spawn.

**Tradeoff.** Elasticity collapses to "clients volunteer for a region," which is
closer to a conventional dedicated-server shard model. You lose the ability to
opportunistically offload distant work, which was a main argument for the
federated design.

### F4 / F5 — suspension exists; full dematerialization and weapon transfer need proof

`system-architecture.md` § Materialization lifecycle step 6 says "Plugins destroy
only objects belonging to the fenced assignment," and the failure table says
"Partial spawn/update → Roll back only assignment-owned objects." Ownership
scoping is fine. The terminal `destroyObject` path is a *kill*:

```csharp
public virtual void destroyObject(bool keepInFlightEffectAlive = false, bool score = true,
                                  TacView.TCEvent eventType = TacView.TCEvent.Destroyed)
```

It detaches from `Formation`, clears `_userRoot` tasks, destroys uncontrolled
`_childAircraft`, calls `_taskforce.RemoveTaskForceObject`, `ClearEntity()`,
`setDestroyedFlag(score, eventType)`, and raises `OnDestruction`. `score: false`
suppresses scoring but the destroyed flag, the TacView event, the child-aircraft
cascade, and every AI contact's reaction still happen.

There is, however, a native reversible middle state: `ObjectBase.Disable()`
removes the unit from active object/task-force collections, adds it to
`_taskforceDisabledUnits`, disables its ECS entity, deactivates the root, and
causes simulation update to return early. `Enable()` reverses those operations,
and native saves include disabled units. This is a strong quiet-suspension seam.
It does not free the constructed GameObject, systems, or `GameMain._objects`
entry, so it cannot by itself satisfy a scheduler that is trying to reclaim
memory or construction overhead.

**Failure scenario.** Calling `destroyObject` while demoting a healthy Perry
still creates the invented-loss behavior described above. Calling `Disable`
avoids that obvious kill but may retain most of the process cost and has not yet
been verified with embarked aircraft, formations, triggers, contacts, and a
save/load cycle.

**Smallest credible mitigation.** First test `Disable`/`Enable` without additional
teardown. Prove no destruction/AAR event, hosted-aircraft and formation behavior,
no dangling contact behavior, save/load restoration, and measured CPU/memory
reduction. Only if full release is necessary should a second spike detach and
destroy remaining internals, with the same safety conditions and explicit
game-version coupling.

F5 is the harder half. `Missile` carries `_seeker._activationTime`,
`_terminalStartTime`, `_lostGuidanceTime`, `_flightStage`,
`CurrentStrongestTargetEcho` (a live echo object),
`ConnectionLost`/`ConnectionLostForever` reactive properties, `_insDriftValue`,
`_motorPerformance`, and compares them against absolute `GameTime.time`.
`SaveLoadManager.WriteWeaponData` enumerates each task force's `WeaponBase`
objects; `SceneCreator.loadWeapons`/`initWeapons` recreates and launches them, and
weapon subclasses implement save/load state. Chaff/noisemaker objects are not in
the task-force weapon list, and pooled gun `Projectile` objects have no identified
equivalent path. More importantly, the presence of fields in a save is not proof
that guidance links, absolute timers, flight stage, RNG-derived values, and final
outcomes survive. The correct rule is experiment-gated: fence an active state
family until a native or portable round trip proves it safe, rather than declaring
all weapons permanently transferable or permanently impossible.

### F6 — authority and fog of war cannot coexist on a player client

`system-architecture.md` decision 10 says "Information scope is separate from
simulation state," and § Fog of war says the service "never sends true enemy state
to a client solely to make rendering or local prediction easier." Both are right
as policy. The physical fact underneath is that Sea Power's tactical picture is
derived — `PlottingTable` synthesizes task-force-relative tracks from ground-truth
`ObjectBase` instances that must exist in the same process
(`tactical-simulation.md` § Contacts and sensors describes this correctly). To
simulate a Soviet destroyer you must instantiate it, with its real position,
damage, magazines, orders, and sensor state, inside a process the player owns.

`federated-simulation.md` § Observer accuracy and hidden state states this
honestly for full-state observers and concludes "This is a product trust decision,
not something encryption inside the same game process can solve." Correct — but
the conclusion is under-applied. It applies identically to *primaries*, and
`dynamic-campaign-design.md` § Distribution and load balancing markets exactly
that: "give a player's client primary authority for nearby friendly units" is
safe, but the scheduler as specified has no coalition constraint at all.

**Smallest credible mitigation.** Add a normative constraint: a player-session
client may hold Primary/Shadow/full-Observer roles **only** for islands wholly
within its own coalition's information scope. Opposing-force islands go to a
headless worker or the operational resolver. Add a sentinel test that fails if any
assignment's island contains an entity outside the session's coalition scope.

**Tradeoff.** Player-vs-player engagements now always require a neutral tactical
host for at least one side — a dedicated Sea Power instance nobody plays on. That
is a real hosting cost and it removes "any player's spare capacity is usable" from
the value proposition. It is also the only version of this that is not a cheat
vector.

### F12 — recovery that rewinds what players already saw

`system-architecture.md` § Persistence and recovery says "No client-only event is
considered committed after coordinator recovery" and "Unacknowledged Unity work
since the last accepted event or portable checkpoint may be discarded."
`implementation-plan.md` § Time and causality asks "Does recovery rewind
information already exposed to players?" but no document answers it.

**Failure scenario.** A Harpoon impacts at client-local time T. The plugin
proposes `WeaponImpacted` + `DamageApplied`. The Sea Power process crashes before
the companion's durable spool flushes. Recovery restores the last checkpoint at
T−20 s. Canonically the missile is still in flight, then misses. The player
watched a hit, an explosion, and a fire.

**Smallest credible mitigation.** Split tactical events into *reversible
presentation* (position, contact updates — free to render optimistically) and
*irreversible outcome* (impact, damage, destruction, store consumption).
Irreversible outcomes are rendered only after the commit acknowledgement returns.
This costs one round trip of visible latency on explosions.

**Tradeoff.** In single-player-local that latency is ~1 ms and irrelevant. On a
remote service it is one RTT, and a missile impact visibly lags. That is still
better than un-killing a ship.

### F15 / F16 / F17 — the current slice's security posture

`Program.cs:33-47` accepts `POST /game/commands/{commandType}` with any command
name and any body, forwarding both to `GamePipeBroker.SendCommandAsync` and thence
to `GameCommandDispatcher`. Today the dispatcher's `switch` rejects unknown types,
so the blast radius is nil. But `implementation-plan.md` P0 § Remaining hardening
work lists "Restrict the local control listener and pipe to the intended local
user" and "Add serialization tests for every v1 envelope/payload and malformed
input" — it does not list removing the passthrough, and P2A adds mutating commands
behind a capability flag reached through *this same route*. The order of
operations is dangerous: the generic route should die before the first mutating
command exists.

`GamePipeBroker.cs:136` constructs `NamedPipeServerStream` with no `PipeSecurity`;
on Windows the default DACL is permissive enough that any user session can
connect. `maxNumberOfServerInstances: 1` also means a second Sea Power process on
the same machine cannot connect at all — worth noting given the design's
multi-client ambitions. Neither side bounds line length before `ReadLineAsync`,
and `_pending` is unbounded.

---

## Contradictions and ambiguous ownership

1. **Render proxy: fidelity level or replica role.** `dynamic-campaign-design.md`
   § One world, several fidelity levels lists "Render proxy" as fidelity level 4.
   `system-architecture.md` § Fidelity model explicitly says "`Render proxy` is a
   replica role, not canonical fidelity." The normative doc wins; the design doc
   must be corrected or it will be implemented twice.
2. **Hash agreement as truth vs. as detector.** `federated-simulation.md`
   § Replica reports: "Hashes are useful for detecting divergence, not for proving
   which two-replica result is correct." Same document, § Health and failover,
   promotion ranking item 4: "agreement with the last committed canonical hash."
   Ranking by agreement *is* treating the hash as a truth proxy.
3. **Pause semantics.** `system-architecture.md` § Time model: "Multiplayer has no
   unilateral global pause." `runtime-architecture.md` § Time and time compression
   and `Seapower/GameTime.cs:299` establish that pause is a process-global
   `Time.timeScale` any player can set. The doc's remedy (lose primary eligibility
   after a grace period) handles the *authority* consequence but not the fact that
   the local island stops advancing and must be resynchronised; F1 and the
   still-unvalidated F5 state families make that risky mid-engagement.
4. **P2 requires P3/P4 concepts.** `implementation-plan.md` 2A: "Require session,
   assignment, authority epoch, expected revision, and idempotency key on every
   mutation." Assignments and epochs are defined in P4 and implemented in P6.
   Either P2 uses placeholder values (in which case the requirement is theatre) or
   P2 depends on unbuilt work.
5. **Phase 4 pauses the campaign for handoff** ("Start with globally paused
   campaign handoff at a deterministic tick") while its own exit gate requires
   "Theater time and unrelated entities continue while the island is tactical,"
   and `system-architecture.md` § Status and scope says the design "supersedes any
   earlier assumption that the campaign normally stops." Whether paused handoff is
   a permanent property or a P4 scaffold is never stated.
6. **`testing.md` overclaims the control round trip.** "The smoke test … proves the
   reads ran on the same Unity main thread." See F18: it compares a constant to
   itself.
7. **Catalog fingerprint does not cover code.** `system-architecture.md`
   § Versioning pins "normalized catalog fingerprint and relevant mod fingerprint"
   and `implementation-plan.md` § Compatibility and operations asks "Does a
   game/mod update change a field without changing the fingerprint?" A patch that
   changes `Blastzone` penetration logic changes no INI. Nothing pins managed
   assembly hashes.
8. **"Trusted co-op can accept client tactical outcomes initially"**
   (`system-architecture.md` § Fog of war and security) versus "No code path may
   silently update canonical campaign state from an unfenced client message"
   (§ State ownership). Fenced-but-unvalidated is still fabrication-capable;
   plausibility checks are deferred to P9 while the P6–P7 product increment is
   already named "Trusted co-op alpha." `README.md` makes no trust-scope statement
   at all.
9. **Ownership of mission objectives/triggers is unassigned.**
   `system-architecture.md` § State ownership covers ten categories but not
   `MissionManager` variables, objectives, and triggers, which
   `runtime-architecture.md` § Saves and mission lifecycle says exist and drive
   mission termination. If a materialized island lives inside a loaded mission,
   whose objectives govern? Undefined.
10. **Camera/selection declared non-authoritative** (§ State ownership:
    "UI/camera/selection | Local client | Never campaign authoritative") but
    `RenderPosition` initialization is what fixes `Globals._currentCenterTile`
    (`Seapower/RenderPosition.cs:305-321`), which determines every unit's local
    coordinates. The camera is load-bearing.
11. **Event rate is missing from the deferred-decision table.** Checkpoint
    cadence/size is listed; the tactical *event* rate — which drives durable append
    throughput and therefore the commit-before-display latency in F12 — is not.
12. **Coordinator single-writer.** § Supported deployment modes says "The first
    release does not require coordinator leader election," which is true only if
    exactly one writer exists. P8 adds the fencing mechanism. P3–P7 have no
    enforcement.

---

## Missing state and lifecycle analysis

"Deterministic" below means *reproducible across two Sea Power processes*, which
is the property the federated design needs. **U** marks conclusions that could not
be settled from the decompiled source and that need a live experiment.

| State category | Extract | Reconstruct | Deterministic | Checkpoint | Transfer | Safely ignore | Experiment needed |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Geo position / heading / speed / depth | Yes | Yes | No (F1) | Yes | Yes | No | — |
| Telegraph / ordered speed / waypoints | Yes | Yes | Yes | Yes | Yes | No | — |
| Formation membership + station | Yes | Yes | U | Yes | U | No | Re-attach a unit to a live `Formation` post-spawn |
| Rigidbody velocity / control surfaces / flight physics phase | U | U | No (`Seapower/FixedWingFlightPhysics.cs:137` uses `Random.Range`) | U | No | For ships, probably; for aircraft, no | Measure aircraft state error after spawn-with-velocity |
| Magazines / stores / loadout | Yes | Yes | Yes | Yes | Yes | No | — |
| `BaseSystem` integrity / disabled systems | Yes | Yes | Yes | Yes | Yes | No | Confirm a write path exists, not just read |
| Compartment integrity, flooding rate, fires, DC teams | Partial | U | No (time+RNG progression) | U | U | No | Damage a ship, checkpoint, restore, compare sink time over 10 min |
| Progressive/secondary damage timers | U | No | No | U | No | No | Same experiment |
| Launcher state: prepared ammo, rail/hatch, reserved guidance channels | Partial | U | U | U | U | No | Round-trip a mid-prepare launcher |
| **In-flight weapons (missiles, torpedoes, bombs)** | Partial/native path exists | U | No | U | U | Fence until proven | Native mid-flight save/load with guidance, target, stage, timers, and outcome comparison |
| Sensor on/off/busy/search depth/channels | Yes (if `SaveLoadSensorDataEnabled`) | Yes | U | Yes | Yes | No | Confirm the option is on; pin it (F21) |
| Sensor `_activationTime` and other absolute-time fields | Yes | Only with a clock offset | No | Yes | Requires `GameTime.time` rebase | No | Measure whether `GameTime.time` can be set |
| `UnitInternalContact` (detect/classify/ID flags, solution quality, uncertainty ellipse) | U | Approximately | No | U | U | No — drives engagement | Extract, restore, compare classification progression |
| Task-force `PlottingTable` tracks, own/foreign track numbers, observation ring | U | Approximately | No | U | No (track numbers are session-local and player-visible) | No | Determine whether track IDs can be preserved across handoff |
| Data-link phase / `TaskforceManager` interval position | No | No | No | No | No | Yes (re-converges within one interval) | — |
| Unit AI state machine current state + timers | U | U | No (`Seapower/AI.cs:186`) | U | U | No | Read `StateMachine` current state; test restore |
| Behaviour-tree runtime state (`initBehaviourTree`, NodeCanvas) | U | Probably not | No | No | No | Probably — restart the tree | Confirm a fresh tree does not reset orders |
| AI target lists, reacquisition cooldowns, preset attacks | U | No | No | No | No | No | Same as `UnitInternalContact` |
| `FlightDeck`: hosted aircraft, assignments, loadouts, queued ops, deck/crew utilization | Yes (if `SaveLoadFlightDeckEnabled`) | Yes | U | Yes | Yes | No | Round-trip a deck mid-launch-cycle |
| Airborne aircraft hosted by a materialized ship | Yes | Yes | No | Partial | U — `Disable` avoids destruction but behavior is untested | No | Disable/Enable and full-removal experiments must cover this |
| Crew skill / survival credits | Yes | Yes | Yes | Yes | Yes | No | — |
| Environment: weather, sea state, sonar layer | Partial | Only by forcing values (`Seapower/Environment.cs:675,728` use `Random.Range`) | No | Yes (campaign-owned) | Yes | No | Confirm the layer depth can be written |
| Wakes, chaff clouds, decoys, particle effects | No | No | No | No | No | **Yes** | — |
| Towed arrays / cables | U | U | U | U | U | No — affects sonar | Test spawn-with-towed-array |
| `GameTime.time` origin | Yes | — | — | Must be carried | Required for every absolute-time field | No | **Can `GameTime.time` be set from a plugin?** (private setter; needs Harmony) |
| Mission objectives / triggers / variables | Yes | N/A | N/A | N/A | N/A | **Should be** — but ownership is undefined (contradiction 9) | Decide ownership |
| Score / AAR registers / `_weaponsSpent` | Yes | N/A | N/A | N/A | N/A | **Yes** — campaign owns the equivalent | — |

The single most consequential uncertainty in this table is `GameTime.time`: it has
a private setter, it is a float, and dozens of guidance, sensor, and AI decisions
compare against it. If it cannot be set, every restored entity's absolute-time
state must be rewritten with an offset at restore, which multiplies the extraction
surface.

---

## Failure and partition analysis

**1. Primary dies immediately after weapon launch, before event commitment.**
Steps: launcher consumes a round and spawns a `Missile` (draws from
`Globals._unityRandom`) → plugin queues `WeaponLaunched` + `StoreDelta` → process
dies before the companion's durable spool acknowledges.
Expected canonical result: no launch. The round is still in the magazine; no
missile exists; the target was never engaged.
Current design: **partially defined.** § Persistence and recovery correctly says
unacknowledged work may be discarded, and the bounded-loss window is a required
published number. What is *not* defined is the player-visible consequence — the
shooter's crew saw a launch and the magazine count decrement in the UI. See F12.
Also undefined: whether the *target's* client, which may have detected the launch
and begun evasion, must also roll back.

**2. Old primary reconnects after a shadow was promoted.**
Expected: the returning client is refused its old role, its epoch-E events are
rejected, it must re-handshake and rebase, and its local Unity world is discarded
and re-materialized from the committed checkpoint.
Current design: **defined and correct in principle.** § Replica roles and
authority: "A returning old primary must re-handshake and rebase before receiving
any role"; failure table: "Reject old-epoch events; require rebase and a new
assignment." The gap is mechanical: nothing says what happens to the ~30 s of
Unity world the old primary still has running. Native `Disable` can suspend its
objects without scoring kills, but may retain most allocations; full teardown is
still an F4 experiment rather than necessarily a mission unload.

**3. Coordinator dies during promotion.**
Expected: the promotion transaction either committed (new epoch E+1 durable, new
primary valid) or did not (old epoch still current, no valid primary, island falls
to operational). No intermediate state.
Current design: **defined, conditional on an unstated assumption.** The
`begin transaction … commit` block in `federated-simulation.md` is correct. It is
safe only under single-writer (F22) and only if lease *expiry* is evaluated
against durable service time rather than an in-memory timer that resets on
restart. § Recovery order step 4 ("Expire all pre-crash tactical leases and
increment affected epochs") covers this — but note it means every coordinator
restart invalidates every tactical island, which for a single-coordinator first
release is a full-theater fidelity outage. That cost is not stated anywhere.

**4. Two replicas disagree with no third replica.**
Expected: no truth is established; the coordinator resynchronizes both from the
last committed checkpoint or falls back to the operational resolver.
Current design: **correctly refuses to vote** ("majority voting requires at least
three independent replicas") but the resolution is under-specified. "Resync or use
an independent rule" — which rule? The only genuinely independent adjudicator
available is the external operational resolver, which by construction produces a
*different* answer from both. If the disagreement involves an F5 state family
whose native round trip has not passed, checkpoint reload is unavailable. Given
F1/F2/F8 this may be common until the runtime adapter is validated.

**5. Observer sees a unit but is not authorized to receive its hidden state.**
Expected: the observer receives a filtered projection — position and
classification consistent with its own coalition's track picture — and cannot be
promoted.
Current design: **defined as policy, undefined as mechanism.** To *display* an
enemy contact at all, Sea Power needs some object in the scene. A filtered
projection must therefore materialize a decoy `ObjectBase` carrying only
authorized fields, with no sensors, no AI, no weapons, and with its true position
replaced by the track solution. Nothing in the docs specifies this object, and it
interacts badly with the game's own `PlottingTable`, which will run its own sensor
logic against whatever is in the scene. This needs a concrete "proxy object"
specification before P7.

**6. Interaction island must merge while weapons are in flight.**
Expected: merge is deferred until no weapon crosses the boundary, or both islands
are quiesced and the weapon is resolved deterministically.
Current design: **experiment-gated.** § Interaction islands requires one merged
checkpoint while the split rule forbids active cross-boundary coupling. The
native weapon save path makes a merged checkpoint plausible but not yet safe. If
the F5 round trip passes for the active weapon family, a quiesced transfer may be
allowed; otherwise defer the merge or absorb the target island into the already
running weapon-holding primary so nothing in flight is serialized. The latter is
also constrained by the measured session radius from F3.

**7. Every tactical replica disappears.**
Expected: affected islands fall to operational fidelity from the last committed
checkpoint; unrelated theater continues; islands whose state cannot be safely
projected (weapons in flight, mid-flooding, mid-launch-cycle) enter Suspended.
Current design: **defined and reasonable** (§ Failure behavior, § Fidelity model's
Suspended state). The gap: "Suspended" is described as "bounded recovery state"
with no bound and no exit condition other than a client reappearing. If no client
ever reappears — the normal single-player case where the player quits — Suspended
islands must eventually be force-resolved. State what force-resolution does to
weapons in flight (recommendation: resolve them with the operational resolver
using launch parameters, discarding tactical guidance state).

**8. Sea Power updates while a campaign is active.**
Expected: fail closed. Existing clients on the old build keep their assignments
until quiesce; new assignments are refused; the campaign is playable operationally
until the catalog is rebuilt and a migration is applied.
Current design: **defined for data, not for code.** "Catalog/game mismatch | Reject
assignment before materialization" is right, and § Versioning pins the game build.
But Steam updates the game under a running process's feet and the player restarts
into a new build; the *catalog fingerprint may be unchanged* if only assemblies
changed (contradiction 7). Add managed-assembly hashes to the handshake, and treat
a build change as an immediate fence of all that client's assignments rather than
waiting for a materialization attempt.

---

## Scalability assessment

No numbers are attached to any of these; none are measurable from source.

**Likely bottlenecks, ranked by how early they will bite.**

1. **Durable append per tactical event.** The commit path is plugin → pipe →
   companion → gRPC → service → transactional append → acknowledgement, and
   § Delivery and consistency requires acknowledgement only after durable commit. A
   saturated missile engagement produces launch, guidance-handover,
   seeker-activation, contact-update, impact, damage, and store events across many
   shooters simultaneously. If F12's commit-before-display rule is adopted, this
   path is also on the visible latency budget. **This is the first thing that will
   break and it has no entry in the deferred-decision table.**
2. **Island growth into one island.** Per F11, the task force is the minimum viable
   island. Sea Power theaters have few task forces. Sensor and weapon coupling then
   links them during any engagement, and `system-architecture.md`'s hysteresis
   prevents rapid re-splitting. The steady state of an active theater is plausibly
   one island per side per region — i.e., one primary client per side, which is a
   single tactical host, which is precisely the bottleneck the federated design
   exists to remove.
3. **Materialization cost.** `createShip` blocks on synchronous resource loading
   (F13). Materializing a task force is a serial sequence of blocking loads on the
   Unity main thread. This bounds reconnect storms, island merges,
   promotion-by-cold-restore, and fidelity promotion when a player approaches.
4. **Checkpoint size.** A canonical checkpoint per unit includes systems,
   compartments, magazines, sensors, contacts, flight deck, and orders. Per-island
   it also includes the plotting table, whose size is O(units x tracks). Nothing
   bounds track count.
5. **Redundant Unity cost.** Currently one full machine per shadow, for a shadow
   that F1/F2/F8 say can never be promoted. If the F1 mitigation is adopted this
   cost disappears entirely — which is the strongest practical argument for
   adopting it.
6. **Per-process ceilings.** One camera-following local origin, one time
   scale, one pipe server instance (`GamePipeBroker.cs:139`), one mission scene.
   Relocation is also a whole-world single-frame cost that grows with object
   count.

**Benchmarks required before any scale claim, in priority order.**

- **B1 (do this first):** two Sea Power processes, identical scenario, identical
  build, no plugin intervention — measure position/heading/contact/outcome
  divergence versus mission time at 1x, 10x, and 100x, on identical and on
  deliberately mismatched hardware (one throttled below 30 fps to cross the
  `Time.fixedDeltaTime` hysteresis). This single measurement determines whether P6
  exists.
- **B2:** `GameTime.time` advance rate versus wall clock per process at each
  compression step; and float precision degradation of `GameTime.time` over a long
  high-compression session.
- **B3:** `createShip` / `createAircraft` / `createSubmarine` main-thread cost per
  platform class, cold and warm.
- **B4:** canonical extraction cost and allocation per object per frame, at the
  object counts a real scenario produces (P1D already covers this — keep it).
- **B5:** tactical event rate during a saturated multi-shooter engagement, measured
  by instrumenting the read-only extractor before any event contract is frozen.
- **B6:** durable append throughput and latency on SQLite and on PostgreSQL, at
  B5's rate.
- **B7:** interaction-graph size over a recorded engagement — how large does the
  connected component actually get, and how often would it split.

---

## Implementation-plan review

**P0 — freeze and harden the control baseline.**
Missing prerequisites: none. Ordering: correct.
Insufficient exit criteria: "One command cannot crash or stall Unity's frame loop"
is not tested by anything — `RuntimeBridgeBehaviour.Update` caps at 32 commands/frame
with no *time* budget. Add a per-frame millisecond budget and a test that submits a
deliberately expensive read.
Add to remaining work: delete the `POST /game/commands/{commandType}` passthrough
(F15); ACL the pipe and bound frame size (F16); structured errors without stack
traces (F17); fix the vacuous main-thread assertion and the `testing.md` claim that
depends on it (F18); pin `Globals._useAssetBundles`, `SaveLoadSensorDataEnabled`,
`SaveLoadFlightDeckEnabled`, and managed-assembly hashes into the handshake
(F19–F21).
Experiments required: none. **P0 is close to done and should be finished.**

**P1 — inventory, identity, and observation.**
Missing prerequisites: none.
Should move *into* P1: B1 and B2. `implementation-plan.md` defers all determinism
questions to P6's adversarial matrix. That is six phases too late — B1 costs two
game instances and a day, and it decides whether a third of the plan is real.
Insufficient exit criteria: "Repeated extraction at the same quiescent state
produces the same canonical serialization and hashes" tests one process at rest. It
does not test two processes, or one process in motion. Add: "two processes running
the same scenario produce hashes whose divergence over time is measured and
published."
Also add to 1B: determine whether `GameTime.time` can be written, since the entire
absolute-time state category depends on it.

**P2 — bounded mutation and materialization.**
Missing prerequisites: the quiet-removal path (F4) is assumed by 2C "Dematerialize
only the owned entity" but never scheduled as a spike. It is the riskiest single
piece of game-code work in the plan and it should be its own gate *before* 2B.
Ordering problem: 2A requires "assignment, authority epoch, expected revision" —
concepts P4/P6 own (contradiction 4). Either define a minimal epoch/revision
contract in P1 (cheap, and it belongs with identity anyway) or drop the requirement
to session + idempotency key at P2.
Insufficient exit criteria: "all supported fields within tolerance" — tolerance is
undefined and per-field. Require a published per-field tolerance table as the
artifact.
Experiments required before proceeding: B3 (spawn cost), quiet-removal spike,
`GameTime.time` writability.

**P3 — deterministic campaign kernel.**
Missing prerequisites: none. This phase is independent of the game and is the
best-specified phase in the plan.
Should move earlier: 3A and 3B can and should run in parallel with P1 — nothing in
them touches Unity, and the plan's own "work that can proceed in parallel" section
says so. Make that explicit rather than advisory.
Insufficient exit criteria: add the single-writer store fence (F22). Add an
explicit event-rate budget derived from B5.
Experiments: none required; this is ordinary implementation work.

**P4 — continuous single-client vertical slice.**
Missing prerequisites: F12's reversible/irreversible event split; the
session-region binding from F3/F7; the `GameTime.time` rebase strategy from F10.
Insufficient exit criteria: the mandatory scenario list has no "player observes an
outcome that is later not committed" case, which is the defining new failure mode
of this architecture.
Should move earlier: "Route ordered player commands through the service rather than
committing local intent directly" is the highest-value and highest-risk UX change
in the plan — late orders and prediction correction should be exercised here, not
deferred to P7C.
This remains, correctly, "the first architecture proof."

**P5 — operational simulation and dynamic fidelity.**
Missing prerequisites: B7 (real interaction-graph sizes) must precede the
coupling-threshold work, otherwise thresholds are guessed. F11's
task-force-minimum constraint must be decided before the graph is built.
Ordering: the island merge/split protocol remains gated by the F5 active-weapon
round trip. Use quiesced checkpoint transfer only for validated state families;
otherwise merge by absorption into the weapon-holding primary and never split an
active cross-boundary coupling.
Insufficient exit criteria: "Operational outcomes fall within documented
calibration envelopes" presumes Unity outcomes have measurable variance. Per F2
they do not yet — the variance of Unity outcomes for a fixed setup must be measured
first (a corollary of B1) or the envelope is meaningless. Cap calibration at ≤10x
compression (F14).

**P6 — redundant replicas and failover.**
This phase is **conditional on B1** and should be explicitly gated on it. If B1
confirms F1/F2/F8, P6 as written is not achievable and must be rewritten as:
lease/epoch/fencing service (6A, keep — it is needed regardless), *cold*
checkpoint-restore failover, and operational fallback (part of 6C, keep). Delete
6B's hash-comparison-driven convergence and the warm-shadow premise.
Insufficient exit criteria even in the redesigned form: "A warm shadow takes
authority within the measured recovery objective" must become "failover completes
within the measured objective, where the objective is derived from B3
materialization cost."
6A is worth building either way — the epoch fence protects against a returning old
primary and a restarted coordinator, both of which are real in single-player.

**P7 — realtime multiplayer experience.**
Missing prerequisites: the coalition-scoped-authority constraint (F6), and the
filtered-proxy object specification (timeline 5). Without both, 7B's "Tests with
sentinel secret fields at every serialization boundary" tests the *wire* while the
leak is in *process memory*.
Insufficient exit criteria: "Unauthorized state never crosses projection boundaries
in automated tests" should read "...and no client process holds ground-truth state
outside its coalition's information scope," which is a much stronger and testable
claim.

**P8 — scale, persistence, and service availability.**
Should move earlier: the leader/fencing mechanism for lease/epoch allocation (F22)
belongs in P3, not P8 — it is a few lines of CAS and it prevents a whole class of
silent corruption during six phases of development.
Otherwise correctly ordered. Load-test targets should be derived from B5/B6/B7
rather than chosen.

**P9 — hardening and release qualification.**
Should move earlier: "plausibility checks and independent adjudication for client
outcomes" is listed as P9 work, but the P6–P7 increment is publicly named "Trusted
co-op alpha." Either move basic plausibility checks (impossible kinematics,
impossible detections, store conservation) to P6 alongside event acceptance — they
are cheap server-side reducer validations — or state in `README.md` that co-op is
trust-based.
Add: game-update handling must include the assembly-hash fence (F19), not only
catalog migration.

---

## Proposed architectural revisions

### Required before further mutation work (i.e., before P2)

1. **Run experiment B1** (two-process divergence) and publish the result. Every
   decision below about redundancy is downstream of it.
2. **Delete the generic companion command passthrough**; one route per allow-listed
   command, local token required. ACL the named pipe, bound frame sizes, bound
   `_pending`, and remove stack traces from wire errors.
3. **Fix the main-thread assertion** so it compares the executing thread ID against
   the captured one, and correct `testing.md`'s claim.
4. **Extend the handshake fingerprint** to managed assembly hashes,
   `_useAssetBundles`, `SaveLoadSensorDataEnabled`, `SaveLoadFlightDeckEnabled`, and
   difficulty/gameplay options — refuse tactical roles on mismatch. *Tradeoff:*
   every game patch fences all clients until re-fingerprinted; that is the intent.
5. **Spike native `Disable`/`Enable` first, then any full quiet-removal path** (F4)
   as gates before 2B, with the acceptance conditions and measured resource relief
   listed above. Full teardown, if needed, deliberately depends on internals below
   the public seam and must be documented in `source-map.md`.
6. **Determine whether `GameTime.time` is writable** and decide the absolute-time
   rebase strategy.

### Required before continuous single-player (before P4 exit)

7. **Bind geography and time scale to the client session, not the assignment.** A
   session declares one theater region and one permitted time scale at handshake;
   assignments outside a validated radius of the *current* centre tile are
   refused, and the plugin reports relocation events so the service knows the
   bubble moved. Delete per-assignment "permitted time scale" from § Time model.
   *Tradeoff:* loses opportunistic offload, and couples assignment validity to
   player camera behaviour — which argues for a headless or camera-parked host
   for any island the assigning player is not personally watching.
8. **Split tactical events into reversible-presentation and irreversible-outcome**;
   render irreversible outcomes only after commit. *Tradeoff:* one RTT of visible
   latency on impacts in remote deployments.
9. **Cap tactical fidelity at the 10x physics cap.** Above it, the island is
   operational. *Tradeoff:* a player cannot watch a battle at 100x; they can watch
   it at 10x or read the operational result.
10. **Add single-writer enforcement on the durable store** (CAS'd writer epoch)
    from the first P3 commit.
11. **Assign an owner for mission objectives, triggers, and variables** —
    recommend: campaign-owned, with the loaded mission reduced to an empty container
    whose objectives are never used.
12. **Define Suspended's bound and its force-resolution rule**, including what
    happens to weapons in flight.

### Required before redundant simulation (before P6)

13. **If B1 confirms divergence: remove same-unit redundant Unity simulation from
    the normative architecture.** Rewrite `federated-simulation.md` around: one
    primary, epoch fencing, cold checkpoint-restore failover, and operational
    fallback. Retain assignments, leases, epochs, health reporting, and the
    promotion transaction. Delete Shadow as a Unity role; retain Observer strictly
    as presentation and Proxy as-is. *Tradeoff:* failover becomes seconds and lossy
    rather than sub-second and seamless — but it becomes real.
14. **Remove hash agreement from promotion ranking.** Hashes may only disqualify.
    *Tradeoff:* fewer signals for ranking; use cursor freshness and health instead.
15. **Make the task force the minimum interaction island** and forbid
    sub-task-force partition.
16. **Run native active-weapon round trips before enabling checkpoint merge.** For
    any unvalidated state family, absorb into the weapon-holding primary or defer;
    retain a split fence while any coupling crosses the boundary.
17. **Move basic plausibility validation** (kinematic limits, detection-range
    limits, store conservation, revision monotonicity) from P9 into the P6
    event-acceptance reducer.

### Required before multiplayer (before P7)

18. **Constrain player-session clients to own-coalition islands only** for every
    simulating role. Opposing-force islands require a headless tactical host or the
    operational resolver. *Tradeoff:* PvP requires neutral hosting capacity; this is
    the cost of not shipping a cheat vector.
19. **Specify the filtered-proxy object**: a Crimean-Tom-owned scene object carrying
    only authorized track state, with no sensors, weapons, or AI, and prove Sea
    Power's own sensor pipeline does not treat it as a real contact.
20. **State the trust model in `README.md`**, not only in the architecture doc.

### Optional later improvements

21. Content-addressed checkpoint storage and delta encoding (already P8; fine
    there).
22. Coordinator leader election (already P8).
23. Multiple pipe server instances so one machine can host several game processes.
24. TacView export as a diagnostic artifact — the game already emits
    `TacView.TCEvent`, which is a cheap independent record of what a client believed
    happened.

---

## Adversarial test additions

Not currently in `implementation-plan.md` § Test portfolio or `testing.md`:

**Determinism / divergence**

- Two unmodified Sea Power processes, same scenario, same build:
  divergence-versus-time curve at 1x/10x/100x. (This is B1 and it is the most
  important missing test in the repository.)
- The same, with one process CPU-throttled to force `Time.fixedDeltaTime` across
  the 30/40 fps hysteresis boundary.
- `GameTime.time` advance rate versus wall clock, and float-precision degradation
  over a long high-compression session.
- Same scenario, same process, two consecutive runs — measures the `AI._aiRandom`
  unseeded contribution alone.
- Spawn-order sensitivity: identical island materialized in two different orders in
  the same process; compare outcomes.

**Materialization / lifecycle**

- Quiet removal of a ship with an embarked, airborne helicopter — assert the
  helicopter survives and no destruction event is emitted.
- Quiet removal while the unit is the target of an in-flight missile — assert
  defined behavior (expected: refused).
- Materialize at increasing distance from `Globals._currentCenterTile` until
  position error exceeds tolerance; publish the radius. Separately, force a
  relocation event with N objects materialized and measure the frame cost.
- Materialize during an active flight-deck launch cycle; round-trip and compare
  queued operations.
- Damage a ship to progressive flooding, checkpoint, restore, and compare sink time
  across ten repetitions.
- Kill the process during `createShip`'s synchronous load; assert cleanup leaves no
  half-built object.
- Run a materialize/dematerialize cycle 500 times; assert no leak in
  `ObjectsManager._listOfAllObjects`, `GameMain._objects`, or the plotting table.

**Authority / time**

- Player presses ESC and pauses while holding a primary lease; assert fence, and
  assert the local world is discarded rather than resumed.
- Coordinator restart while an island is tactical; assert the full-theater fidelity
  outage is bounded and reported.
- Two coordinators started against one store; assert the second refuses to write.
- Late order arriving after its subject was destroyed by a committed event.
- Clock-rate divergence: primary's `GameTime.time` advancing 20% slower than
  campaign time for ten minutes; assert lead/lag window detection fires.

**Security / fog of war**

- Attach UnityExplorer to a client holding an observer role and attempt to
  enumerate opposing-force `ObjectBase` instances. Assert none are present. *This
  test, not wire-level sentinels, is the real fog-of-war test.*
- Timing side channel: measure materialization duration and checkpoint size as a
  function of the number of hidden enemies in the island.
- Local unauthenticated POST to the companion control port from a different process
  and from a browser form.
- Oversized single line on the named pipe; assert bounded memory.
- Fabricated outcome: a modified plugin proposes a kill on a unit it never
  detected; assert plausibility rejection.

**Operations**

- Steam updates the game while a campaign is active and a client is tactical.
- A patch that changes assembly content but no INI file; assert the fence fires.
- Backup/restore consistency between event positions and checkpoint blobs.

---

## Decision register

**Accept now**

- External durable authority; pure campaign core with no game assembly references
  (decisions 2, 6).
- Stable typed IDs never derived from Unity instance IDs or list positions.
- Idempotent fenced events with epochs, and the promotion-as-one-transaction rule.
- Event journal plus snapshots; SQLite first behind a storage abstraction.
- gRPC for the remote hop; newline JSON for the local pipe.
- Budget-driven capacity with no claimed numbers until benchmarked.
- The phase-gated plan structure and the deferred-decision table with deadlines.

**Change now**

- `Render proxy` is a replica role, not a fidelity level — correct
  `dynamic-campaign-design.md`.
- Remove hash agreement from promotion ranking.
- Move the single-writer store fence from P8 to P3.
- Move basic plausibility validation from P9 to P6.
- Move B1/B2 from "P6 adversarial matrix" to P1.
- Time scale and center tile are session properties, not assignment properties.
- Task force is the minimum interaction island.
- Weapons in flight remain a handoff fence only until their native/portable
  round-trip gate passes; active cross-boundary coupling remains a split fence.
- Player-session clients get own-coalition islands only.
- Delete the companion's generic command passthrough before P2.
- Fix the main-thread assertion and the `testing.md` sentence that depends on it.
- Add managed-assembly hashes to the compatibility fingerprint.
- Cap tactical fidelity at 10x compression.

**Defer behind an experiment**

| Decision | Experiment | Measurable criterion |
| --- | --- | --- |
| Keep or delete redundant Unity simulation of the same unit (arch. decisions 4, 8; all of P6B) | **B1** — two processes, same scenario, 1x/10x/100x, matched and mismatched hardware | Keep only if, at 1x on matched hardware, position divergence stays under a stated tolerance for ≥10 min *and* engagement outcomes (hit/miss, target selection) match in ≥90% of ten runs. Otherwise delete. |
| Whether campaign time can map to client time at all without continuous rate correction | **B2** — `GameTime.time` versus wall clock per compression step | Rate error must stay under a stated fraction of the lead/lag window over 10 min; publish the session-rebase interval implied by float precision. |
| Whether suspension and full dematerialization are safe | Native Disable/Enable, then optional full-removal spike (F4) | No destruction event, hosted aircraft/formation behavior defined, no dangling contacts, survives save/load, and measured CPU/memory relief. If only Disable passes, it is suspension rather than memory-releasing dematerialization. |
| Whether active weapons can cross a checkpoint boundary | Native and portable mid-flight round trips (F5) | Guidance/target links, stage, timers, decoys, and outcomes remain within published tolerances; unsupported families stay fenced. |
| Whether absolute-time state can be transferred | `GameTime.time` writability probe | Either it is settable (via Harmony on the private setter) or every absolute-time field needs offset rewriting; the latter roughly doubles P2's extraction surface. |
| Materialization budget and therefore failover objective | **B3** — spawn cost per platform class | Publish milliseconds per unit; the P6 recovery objective is derived from it, not chosen. |
| Tactical event rate and commit latency budget | **B5/B6** | Publish events/sec at engagement saturation and durable append latency; if commit-before-display exceeds a stated visible-latency budget, revisit F12's split. |
| Island coupling thresholds | **B7** — recorded engagement graph sizes | Publish the connected-component size distribution; if the p95 component is the whole side, the federated distribution premise fails independently of B1. |
| Filtered-observer feasibility | Proxy-object spike (timeline 5) | A proxy object renders a track without Sea Power's sensor pipeline treating it as a real contact, and UnityExplorer finds no opposing ground truth. |
| Public/untrusted trust model | Unchanged — P9 | Keep as deferred; but say so in `README.md` now. |

---

## Final recommendation

**The next milestone is a bounded two-part experiment, not a phase: "E1 —
Divergence and Removal."** It should take days, not weeks, and it decides the shape
of everything after P2.

**E1a — Two-process divergence measurement (B1 + B2).** Run two Sea Power instances
on the same scenario (`Intro > 01 Red Sea Breakout`, per `testing.md`'s existing
smoke path) with the current read-only plugin extended by exactly one new read-only
command: a canonical-state sample of every object (position, heading, speed, damage
summary, magazine counts, contact count) plus `GameTime.time`, `Time.timeScale`,
`Time.fixedDeltaTime`, and the measured FPS. Sample at fixed wall-clock intervals
from the companion. Do four runs: matched hardware at 1x, matched at 10x, matched
at 100x, and one instance CPU-throttled below 30 fps at 1x. Publish
divergence-versus-time curves and the `GameTime.time` rate difference.

This stays entirely inside P0/P1 capabilities, needs no mutation, no campaign
kernel, no service, and no new architecture. It costs one command and a sampling
loop. Its output either keeps `federated-simulation.md` or replaces it — and it is
the difference between building P6 and discovering during P6 that it cannot exist.

**E1b — Suspension and quiet-removal spike.** In a disposable scenario,
materialize one surface unit with an embarked helicopter, call native
`ObjectBase.Disable()`, save/load, and `Enable()` it again. Assert no destruction
or AAR event, define helicopter/formation/contact behavior, and measure CPU and
memory relief. Only then test a memory-releasing teardown without
`destroyObject`, using the same correctness assertions. Repeat the supported
lifecycle to check for leaks.

Together these falsify or confirm the two load-bearing assumptions — that Unity
replicas can converge, and that entities can leave a client without dying — that the
rest of the architecture is built on. Neither requires committing to any of the
revisions above; both make those commitments evidence-based.

Everything else in P0's remaining hardening list (F15–F21) should be finished in
parallel, since it is small, independent, and needed regardless of what E1 finds.

### Caveat on E1a

E1a as described runs two instances on one machine, which does not reproduce
cross-machine floating-point or driver differences. That is acceptable: if two
instances on *identical* hardware already diverge, the cross-machine case is
strictly worse and no further evidence is needed. Only if same-machine divergence is
small does a genuine two-machine run become necessary.
