# Deterministic tactical runtime adapter

## Purpose

Sea Power's default simulation schedule is unsuitable for redundant tactical
replicas. It changes its fixed timestep from measured FPS, advances logical time
from Unity frame time, runs important work in both `Update` and `FixedUpdate`,
uses process-global sequential RNGs, contains unseeded `System.Random` instances,
and updates objects in reverse insertion order.

Crimean Tom therefore needs a game-version-specific deterministic runtime
adapter. The adapter does not replace Sea Power's physics, sensors, weapons, or
damage model. It replaces the sources of time, input order, randomness, and
object ordering that make two otherwise identical tactical sessions diverge.

The adapter is a prerequisite for hot redundant Unity replicas. Until its
acceptance tests pass, a Sea Power client remains a single authoritative
tactical executor with checkpoint/cold-restore and operational fallback.

## Design boundary

The campaign service owns policy and canonical time. The companion transports
step grants and records diagnostics. The plugin implements the Sea Power hooks:

```text
campaign service
  canonical tick + ordered inputs + simulation manifest
                         |
                         v
local companion -> named pipe -> deterministic runtime adapter
                                      |
                   +------------------+------------------+
                   |                  |                  |
              clock/schedule      RNG router        stable ordering
                   |                  |                  |
                   +------------------+------------------+
                                      |
                               Sea Power systems
                                      |
                         checkpoint + subsystem hashes
```

The DLL remains thin in campaign policy, networking, and persistence, but this
adapter is necessarily a substantial game-specific compatibility layer. Every
hook is tied to managed-assembly fingerprints and must fail closed after a game
update.

## Runtime modes

The capability has four explicit modes:

| Mode | Behavior | Authority eligibility |
| --- | --- | --- |
| Off | Unmodified Sea Power timing and RNG | Single-primary trusted execution only |
| Observe | No behavior changes; record time, update, RNG, and state divergence | No redundant promotion claim |
| ControlledExperimental | Deterministic hooks enabled in disposable tests | Never campaign authoritative |
| Validated | Hooks passed the pinned-build acceptance suite | Eligible according to negotiated role |

Mode is selected before tactical materialization and cannot change while an
assignment is active. The handshake reports the mode, hook manifest version,
patched-method hashes, simulation settings, and validation evidence version.

## Canonical tactical step

The coordinator does not issue one network message per physics step. It grants a
bounded deterministic step range:

```text
SimulationEpoch
AssignmentId / AuthorityEpoch
StartTickInclusive
EndTickExclusive
FixedStepDuration
OrderedCommandsAndEvents
RngEpochAndSeedManifest
ExpectedCheckpointCursor
MaximumPermittedLead
```

The companion durably receives the grant and sends it through the local pipe.
The plugin advances the session locally, applies inputs only at their named tick,
and reports the last completed tick plus canonical subsystem hashes.

Wall time determines how quickly a machine consumes granted steps, not what a
step means. A fast client may wait at the lead boundary. A slow client consumes
catch-up batches without changing the logical timestep. Rendering can drop or
interpolate frames, but simulation ticks cannot be skipped or stretched.

`FixedStepDuration` and wall-time tempo are deliberately separate. The fixed
step is invariant under load. If required clients cannot sustain the requested
pace after optional work is removed and islands are reassigned or demoted, the
coordinator may lower the synchronization domain's `TargetTicksPerWallSecond` at
an ordered effective tick. It never asks one multiplayer assignment to run at a
different time scale from the others. See [Load management](load-management.md).

## Clock and scheduling control

Setting `Time.fixedDeltaTime` alone is insufficient. The adapter must audit and
control all paths that influence tactical state:

- `GameTime.OnUpdate`, including its FPS-based timestep branch;
- `GameTime.OnFixedUpdate` and physics-approximation behavior;
- `GameMain.Update`, `FixedUpdate`, and `LateUpdate` ordering;
- game schedulers invoked from `GameUpdater`, `GameFixedUpdater`, and
  `GameLateUpdater`;
- simulation-affecting `MonoBehaviour.Update` or coroutines outside those roots;
- direct uses of `Time.deltaTime`, `Time.fixedDeltaTime`, unscaled time, realtime,
  frame count, and wall-clock APIs;
- jobs whose completion/merge order can affect state;
- pause and time-compression inputs.

The target controlled loop is:

1. Receive or dequeue a granted logical tick.
2. Apply all ordered inputs for that tick in stable sequence.
3. Set the Crimean Tom logical time and fixed step.
4. Run each required fixed-phase system exactly once in documented order.
5. Run scheduled ordinary/lazy/late systems according to a deterministic cadence.
6. Complete and merge jobs in stable order.
7. Capture event proposals and optional subsystem hashes.
8. Mark the logical tick complete.
9. Allow presentation to interpolate the latest completed state.

**Validation gate:** Unity may not permit clean separation of presentation and
simulation without patching too many independent `MonoBehaviour` callbacks. If
the call-site audit or prototype shows uncontrolled simulation work remains, the
adapter must fall back to a constrained Unity-driven mode and hot redundancy is
not considered validated.

## Time representation

Crimean Tom uses an integer tactical tick and rational/fixed duration as its
canonical clock. Sea Power's `float GameTime.time` becomes a session-local
compatibility projection:

```text
GameTime.time = SessionTimeOrigin + CompletedTick * FixedStepDuration
```

Absolute-time fields are stored as tick-relative durations or offsets in
portable checkpoints. The adapter periodically rebases the Sea Power float clock
at a quiescent boundary if long-session precision measurements require it.

Any game code that reads wall time for simulation decisions must be patched to
logical time or classified as presentation-only. Date/time, frame rate, logging,
audio, animation, and UI may continue using wall/presentation time when they do
not feed canonical simulation.

## RNG control

The adapter owns a versioned RNG manifest. At minimum the audit must cover:

- `Globals._unityRandom`;
- `Globals._rnd`;
- private/static `System.Random` instances such as `AI._aiRandom`;
- `UnityEngine.Random` state and direct `Random.Range` calls;
- environment, damage, behavior-tree, sensor/TMA, and weapon-specific RNGs;
- RNG used inside jobs or third-party behavior frameworks.

There are two implementation levels:

1. **Seeded sequential compatibility:** reset every known RNG to the same seed
   and require identical object sets, spawn order, update order, and draw count.
   This is the smallest experiment but is fragile.
2. **Keyed deterministic routing:** patch simulation-critical draws to derive
   values from campaign seed, simulation epoch, tick, stable entity ID,
   subsystem, event/purpose key, and draw index. Unrelated entities then cannot
   perturb each other's streams.

Keyed routing is preferred for long-term resilience. It requires a complete
call-site inventory and stable semantic purpose keys; blindly replacing every
`Random.Range` with one global seeded stream only recreates the existing draw
order problem.

Every deterministic random decision needed for an irreversible outcome can
optionally be recorded as an event/audit value. This helps reproduce a primary's
result even when a future game version changes the implementation.

## Stable object and system ordering

All replicated sessions receive the same materialization manifest sorted by
stable campaign ID and object dependency:

1. task forces and shared managers;
2. hosts/bases;
3. surface/subsurface/land units;
4. hosted aircraft and formations;
5. sensors, weapon relationships, and other deferred links;
6. saved active weapons when supported.

The adapter verifies actual registration order after materialization. Where game
loops depend on mutable insertion order, it either maintains identical order or
patches iteration to a cached stable order at deterministic boundaries.

Runtime spawns receive a service-assigned spawn sequence. Destruction/removal
does not compact or reorder a deterministic identity sequence in a way that
differs between replicas.

## Inputs and nondeterministic external events

All simulation-affecting inputs enter through the ordered tick stream:

- player and AI commands;
- time-scale/pause policy;
- materialization and lifecycle changes;
- externally supplied weather/environment changes;
- cross-island observations and interaction events;
- corrections and checkpoint activation;
- administrative simulation configuration.

Local UI input is captured as intent and cannot directly mutate an authoritative
replica before the service assigns an effective tick. Presentation may predict
reversible effects, but canonical simulation consumes the ordered command.

OS scheduling, measured FPS, network arrival order, locale, machine clock, and
local graphics settings are never simulation inputs.

## State comparison and correction

Deterministic control reduces divergence; it does not make Unity physics
cryptographically deterministic across hardware. Replicas therefore report:

- completed logical tick and input cursor;
- time/schedule/RNG manifest versions;
- canonical entity and subsystem hashes;
- unquantized diagnostic samples for position, velocity, sensors, weapons,
  damage, stores, AI, and task-force state;
- measured frame and tick performance.

Hash equality confirms equality only for fields represented by the canonical
schema. A matching top-level hash does not prove omitted hidden state is safe.
Promotion eligibility additionally requires full-state coverage for every
subsystem relevant to the assignment.

Corrections are applied only at defined boundaries. Active guidance, collision,
progressive damage, flight operations, and behavior trees may require a native
save/load restore or full island reconstruction rather than field-level edits.

## Failure and fallback

- If any required hook fails its method fingerprint, deterministic mode refuses
  to activate.
- If the client cannot consume ticks within lag policy, it loses primary or
  promotion eligibility and is drained, reassigned, or disabled as a simulation
  source; the timestep is never enlarged to catch up.
- If an uncontrolled time/RNG call is detected in audit instrumentation, the
  validation suite fails.
- If two controlled replicas diverge beyond tolerance, both preserve evidence;
  the service does not vote between two results.
- If checkpoint correction cannot restore promotable state, failover uses the
  operational resolver or a cold tactical reconstruction.
- If deterministic mode harms ordinary gameplay, it can be disabled without
  disabling the ordinary authenticated control bridge.

## Verification program

### D0 - call-site inventory

- Scan the pinned assemblies for time, RNG, update, coroutine, job, and object
  registration call sites.
- Classify each as simulation-critical, presentation-only, or unknown.
- Record declaring type/method and assembly method hash in a generated manifest.
- No `unknown` call site may remain on a promotion-critical state path.

### D1 - baseline divergence

- Run repeat scenarios with no deterministic hooks.
- When multi-instance isolation exists, run simultaneous matched and mismatched
  hardware/FPS cases.
- Measure divergence of time, kinematics, contacts, RNG-dependent choices,
  weapons, damage, and outcomes at 1x, 10x, and the game's approximation modes.

### D2 - controlled clock only

- Disable FPS-based timestep selection.
- Apply a fixed logical step and ordered commands.
- Verify tick counts and time fields, then measure remaining state divergence.
- Determine whether ordinary `Update` and independent callbacks still make
  divergence structurally frame-dependent.

### D3 - seeded RNG and stable ordering

- Seed every inventoried RNG, normalize spawn order, and verify draw/order
  diagnostics.
- Repeat D1 and identify remaining nondeterministic subsystems.

### D4 - keyed RNG and controlled scheduling

- Add keyed RNG for critical divergent call sites.
- Patch or deterministically schedule remaining simulation callbacks where
  feasible.
- Repeat across different frame rates, CPU throttling, and machines.

### D5 - checkpoint and failover

- Restore a full island from the same native/portable checkpoint into two
  controlled sessions.
- Include damage progression, contacts, flight-deck activity, and supported
  in-flight weapons.
- Run ordered commands, compare hashes/outcomes, inject primary failure, and
  determine whether the other replica is genuinely promotable.

## Acceptance criteria for hot replicas

Hot primary/shadow simulation remains in the architecture only if the validated
adapter demonstrates all of the following on the pinned build:

- exact tick/input/RNG/order agreement for the complete test duration;
- no simulation-critical dependence on measured FPS or wall time;
- no unexplained subsystem-hash divergence;
- matching irreversible outcomes across a statistically meaningful scenario
  suite, including weapons and progressive damage;
- bounded correctable kinematic error across supported hardware;
- successful checkpoint restore without missing promotion-critical state;
- acceptable frame/tick cost and no unbounded catch-up behavior;
- fail-closed detection after a patched method or game assembly changes.

The numerical tolerances, test duration, scenario count, and hardware matrix are
published from D1 measurements before D4/D5 are judged. They are not selected to
make a failed implementation appear successful.

If these criteria fail, Crimean Tom retains deterministic external simulation,
one tactical primary per island, epoch fencing, operational fallback, and cold
checkpoint reconstruction. The campaign architecture remains valid; only hot
redundant Unity execution is removed.
