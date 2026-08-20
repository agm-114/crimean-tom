# Determinism call-site audit — 2026-08-20

Pinned game build: `0.8.2-23444+s358`.

This is the D0 coverage baseline for the deterministic tactical runtime adapter.
It records syntax-level evidence, not a completed call graph. Hot-replica
validation remains fail-closed while tactical-or-unknown entries lack a method
classification and pinned method hash.

## Scan coverage

The local decompilation contained 1,961 C# files under
`decompiled/game/Seapower-Scripts`. Ast-grep scanned all 1,961 with zero skipped
files using `--no-ignore vcs --no-ignore hidden`. Plain `rg` counts were used as
a false-negative cross-check where namespace qualification made a simple AST
pattern too narrow.

| Syntax family | Matches | Files | Immediate interpretation |
| --- | ---: | ---: | --- |
| `GameTime.time` | 1,060 AST / 1,055 text | 237 | Absolute logical-time reads are pervasive across weapons, sensors, AI states, air operations, damage, effects, and presentation. |
| `GameTime.deltaTime` | 96 | 33 | Ordinary-frame simulation integration exists outside the fixed phase. |
| `GameTime.fixedDeltaTime` | 345 | 54 | Fixed-phase integration is also broad. |
| `GameTime.sensorDeltaTime` | 1 | 1 | The sensor clock has a distinct scaled delta at its root. |
| Unity `Time.deltaTime` | 47 | 15 | Direct wall/frame-derived integration bypasses the logical clock. |
| Unity `Time.fixedDeltaTime` | 11 | 6 | Native code reads and changes the Unity physics step. |
| Unity `Time.time` | 32 | 14 | Some schedules use Unity's capped scaled clock instead of `GameTime`. |
| Unity `Time.unscaledTime` | 6 | 3 | Mostly diagnostics/UI, but each use still requires classification. |
| Unity `Time.realtimeSinceStartup` | 97 | 21 | Includes FPS-driven timestep selection and lazy/presentation cadence. |
| Unity `Time.frameCount` | 14 | 6 | Frame identity participates in relocation/logging and requires classification. |
| Unity `Random.Range` / `Random.value` | 62 text hits | 22 combined files | Thirty-three heuristic hits in 11 files are tactical-or-unknown; campaign and presentation hits are separate. |
| `System.Random` constructors | 18 | 14 | Eleven distinct owners include tactical AI, damage/system survival, global utilities, native campaign, and presentation. |
| `Globals._rnd` | 262 AST / 258 text | 55 | One unseeded sequential global stream has a large tactical call surface. |
| `Globals._unityRandom` | 10 | 5 | The fixed-seed stream is still draw-order dependent. |
| job `Schedule` calls | 20 | 19 | Object calculations, collision avoidance, flooding, pathfinding, plotting-table ECS, and native campaign movement all schedule jobs. |
| job `Complete` calls | 32 | 27 | Completion exists at both central and subsystem-specific seams. |
| `yield return` | 309 | 69 | Coroutine state is broad; heuristic grouping leaves 231 hits across 43 files tactical-or-unknown. |
| `StartCoroutine` | 68 text hits | 28 | Callers exceed the 24 member-call AST hits because invocation shapes vary. |

The AST/text count differences are retained rather than normalized away. They
show why neither tool is sufficient alone for the final generated manifest.

## Initial classification

Known simulation-critical sequential RNG owners include:

- `Globals._rnd`: waypoint generation, ejection and tactical utility decisions;
- `AI._aiRandom`: per-unit AI attack selection;
- `SystemCompartment._svRandom`: system-survival/damage decisions;
- `Globals._unityRandom`: missile performance/INS, biologic behavior, and TMA
  variance;
- direct Unity random calls in aircraft engagement/patrol states, fixed-wing
  physics, land-unit behavior, formations, environment, patrol areas, spawn
  templates, and order-tree selection.

Known presentation or non-authoritative native-campaign owners include load
screen choice, camera/cloud effects, map/editor placement, commander names, and
the disposable linear-campaign UI. They still remain in the manifest because a
future call edge can promote them into tactical relevance.

Known critical jobs include object calculations, collision avoidance, flooding,
pathfinding, self/track updates, and plotting-table vehicle updates. The native
`CampaignUpdater` job belongs to Sea Power's non-authoritative strategic model.
`SensorManager` remains an explicit live-instantiation unknown rather than being
assumed dead from source shape.

## `GameTime.time` rebase verdict

A direct rebase is **not safe on this build**. `GameTime.time` and related clock
properties have private setters, while at least 975 heuristic `GameTime.time`
hits across 217 non-UI/non-native-campaign files include stored absolute
deadlines and elapsed-time comparisons. Representative critical families are
weapon launch/guidance stages, torpedo search and reattack states, sensor and
data-link cadence, AI state entry/exit timers, flight-deck preparation, damage
progression, and effect/object destruction.

Changing only the clock would instantly invalidate those deadlines. A safe
rebase requires either:

1. a complete registry that shifts every live absolute timer atomically at a
   quiescent boundary, with save/load and newly created object coverage; or
2. method-level patches that project a session-relative logical clock at every
   simulation-critical read.

Neither contract exists yet. Controlled mode must therefore refuse clock
rebasing and report long-session float precision as a blocker. This is a useful
negative result: the implementation will not silently corrupt active weapons,
sensors, damage, or AI to make a phase checkbox pass.

## Remaining D0 work

- Generate declaring type/method ownership and method fingerprints for every
  raw call site, rather than stopping at file/line evidence.
- Classify the 231 tactical-or-unknown coroutine hits and all job merge order.
- Trace `WeightedRandomBag` and other indirect random wrappers to their callers.
- Enumerate direct `MonoBehaviour` callbacks and serialized scene components
  that execute outside `GameMain` roots.
- Promote every simulation-critical unknown to a keyed/seeded hook plan or an
  explicit blocker before any hot-replica claim.
