# Runtime architecture

Observations are against Sea Power `0.8.2-23444+s358` (Steam app `1286220`,
depot buildid `24297410`, Unity `6000.0.67f1`). Paths are relative to
`decompiled/game/Seapower-Scripts/`, which Git ignores. Regenerate it with
`pnpm decompile`. Line references drift between builds. The type and member names
are the stable part. ILSpy `--list` namespaces are `SeaPower.*`; the checked-in
path convention below follows the `--project` folder names.

## Process and loader boundary

Sea Power is a Unity game launched through Steam app ID `1286220`. On this
development installation, Doorstop starts BepInEx, BepInEx starts the
AnchorChain preloader, and AnchorChain discovers Sea Power Runtime Bridge. This loader chain
is outside the game's own data-mod load order.

The authoritative in-game bridge health signal is a fresh `Player.log` line:

```text
[Sea Power Runtime Bridge] loaded via AnchorChain ...
```

An entry in the in-game Mod Manager is not equivalent to that signal. During the
earlier live pass, the old local `CrimeanTom` data directory appeared unchecked
and without display metadata while the DLL had nevertheless loaded successfully.
Current builds deploy the generic bridge under `SeaPowerRuntimeBridge`; Crimean
Tom remains the external campaign and networking system.

## Initialization

`GameMain` is the central Unity `MonoBehaviour` and singleton-facing coordinator.
Its lifecycle is approximately:

1. `Awake` initializes debug/log services (`DM.OnStart`, `Log.OnStart`).
2. `Start` enables game input. In `Standalone` mode it calls
   `GameInitializer.init()` itself; other modes call it from their own flow.
3. `GameInitializer.init()` sets invariant culture, disables saving, calls
   `GameTime.OnStart()`, resolves the `Singletons` scene parent, then walks a
   fixed ordered list of `OnStart()` calls. In order, that includes options,
   watermark, session, localization, animated projector, camera, screen recorder,
   objects, clouds, textures, terrain, autogen, terrain satellite, object
   placement, environment, sky, scene creation, input, render position, FPS,
   environment audio, music, projectiles, kinematics cache, debug shadow-zone,
   voice, optional mouse control, task forces, mission manager, AI controller,
   encyclopedia data when needed, and the event log.
4. A normal mission then presents its briefing before tactical play. Museum and
   standalone modes take different paths.

**Code evidence, and important for plugins:** `GameInitializer.init()` sets
`GameMain._initialized` immediately only when `SceneCreator.IsLoadingDone` is
already true. In the normal asynchronous load, `SceneCreator.LoadMission` sets
both flags near completion and then enforces one fixed update with
`always: true`. `GameMain`'s `FixedUpdate`, `Update`, and `LateUpdate` all
early-return while `_initialized` is false or `_closing` is true. A plugin that
reads or mutates tactical state must gate on completed scene loading and
`_initialized`, not merely on the singletons existing.

Initialization relies heavily on global singletons and static `Globals` state.
That gives a plugin direct access to the tactical runtime, but makes it difficult to
instantiate safely in a headless process.

## Update phases

`GameMain` drives three Unity phases plus scheduled jobs. The ordering below is
code evidence from `GameMain`, `GameFixedUpdater`, `GameUpdater`, and
`GameLateUpdater`, not inference.

### Fixed update

`GameMain.PerformFixedUpdate()`:

1. `JobPerformer.CompleteFixedUpdateJobs()`: completes the object-calculation
   and collision-avoidance jobs scheduled at the end of the previous step.
2. `GameFixedUpdater.fixedUpdate()`, which runs in this order:
   `ProjectileManager.OnFixedUpdate` → `GameTime.OnFixedUpdate` →
   `SensorManager.OnFixedUpdate` (only if an instance exists; see
   [Dormant and diagnostic code](#dormant-and-diagnostic-code)) →
   `TaskforceManager.OnFixedUpdate` → `AircraftManager.OnFixedUpdate` when that
   singleton exists.
3. Every registered object's `OnFixedUpdate()`, iterated **backwards** over
   `GameMain._objects`, removing null entries as it goes. Off-camera and
   off-map units that are still registered keep running; there is no view-frustum
   or distance cull on this loop. See [Tactical bubble](tactical-bubble.md).
4. `JobPerformer.StartFixedUpdateJobs()`: schedules the next
   `ObjectCalculationsJobScheduler` and `CollisionAvoidanceJobScheduler` batch,
   each only if its previous job has completed.

`GameMain.EnforceFixedUpdate(bool always)` runs a fixed step only if no fixed
update has happened yet, unless called with `always: true`. `GameTime.Pause()`
calls it with the default argument, so in an established mission it normally
does **not** add a pause-time flush step; it only guarantees the first fixed step
has occurred.

### Update

`GameMain.Update()` runs `GameUpdater.update()`, then `GameUpdater.lazyUpdate(-1f)`,
then per-object work. `GameUpdater.update()` calls, in order:

`GameTime.OnUpdate` → `UnitSensorCache` → `EventLog` → `RenderPosition` →
`InputHandler` → `Fps` → `EnvironmentAudioManager` → `DecalsManager` →
`AircraftManager` → `CloudsManager` → `TerrainPainter` → `SplatPainter` →
`VegetationMaskPainter` → `TerrainManager` → `ObjectsPlacement` →
`MissionManager` → `TaskforceManager` → `ObjectsManager` → `Morale` →
`AutogenManager` → `Environment` → `AnimatedProjector` → `AIPlayer` →
main game view model → debug draw → `VoiceMessage` → `MouseControlState` →
`SeaPowerAI.AIController` → periodic `PerformanceMonitor.LogPerformance` (every
30 s of real time) → `TacView` → `SaveLoadManager.OnUpdate`.

Then, iterating **backwards** over `GameMain._objects` and skipping `Disabled`
objects, it calls `OnUpdateEveryFrame()` followed by `OnLazyUpdate()` on each.

The names obscure two details:

- `GameUpdater.lazyUpdate` has an empty body. The global lazy-update tier does
  not exist in this build.
- Per-object `OnLazyUpdate()` is called **every frame**, not on a slower tier.
  `GameMain.Update` computes a lazy-update fraction from
  `Time.realtimeSinceStartup` and `GameTime.TimeCompression`, but discards the
  result without using it. Any throttling is inside the object's own
  `OnLazyUpdate` implementation, which does check `GameTime.IsPaused()` before
  doing simulation work.

`ObjectBase.OnLazyUpdate` is where a lot of per-unit bookkeeping lives:
incoming-weapon and incoming-gunfire calculation, weapon-state and formation
flags, per-`BaseSystem` updates and the `_overallDamage` sum, threat alerts,
EMCON, and free jam/laser channel counts.

### Late update

`GameLateUpdater.lateUpdate()`: `DynamicSky` → `CameraManager` →
`TerrainSatelite` → `AutogenManager` → main game view model. `GameMain` then
walks `_objects` **forwards** and clears each object's `_loadedFromSaveFile`
flag, so that flag is only observable for one frame after a load.

### Exception guard

`GameMain` subscribes to `Application.logMessageReceived` and counts
`NullReferenceException` / `MissingReferenceException` occurrences. Three within
a 0.1 s window (`nullRefThreshold`, `timeWindowSeconds`) opens a blocking error
dialog. A plugin should still fail locally and log full context; relying on this
outer guard can leave the mission in a partially updated state, and tripping it
is itself disruptive.

## Object registry, update order, and lifecycle

### Registries

`ObjectsManager` owns `_listOfAllObjects` plus typed subsets:
`_listOfAllUnits`, `_listOfAllVesselObjects`, `_listOfAllAircraftObjects`,
`_listOfAllRafts`, `_listOfAllMines`, `_listOfAllWeapons`, and player /
non-player splits for objects, vessels, and aircraft. `GameMain` keeps its own
parallel `_objects` list, appended by `GameMain.addObject(obj)`, which also
calls `obj.OnStart()`.

**Update order is reverse insertion order.** `GameMain._objects` is a plain
`List<ObjectBase>` appended during creation and iterated from the end to the
start in fixed and ordinary update. Newly appended objects therefore run before
older objects. Differences in creation order change relative update order and
can invalidate comparisons between otherwise similar runs.

### Weapons are objects; gun rounds are not

The two systems behave differently:

| Concept | Type | Where it lives | Updated by |
| --- | --- | --- | --- |
| Missiles, torpedoes, bombs, decoys | `WeaponBase : ObjectBase` | `ObjectsManager._listOfAllObjects` and `_listOfAllWeapons` | `GameMain._objects` loops, like any unit |
| Gun projectiles | `Projectile` (plain class, not a `MonoBehaviour`, not an `ObjectBase`) | `ProjectileManager._activeProjectiles` | `ProjectileManager.OnFixedUpdate` |

`ProjectileManager` pools `Projectile` instances by ammunition file name,
reusing a destroyed one where possible, incrementing a unique ID and stamping
`_birthTime = GameTime.time`. An in-flight missile is therefore visible to any
enumeration of `_listOfAllObjects`; an in-flight gun round is not.

### Creation

`ObjectsManager` exposes `createShip`, `createSubmarine`, `createAircraft`,
`createHelicopter`, `createLandUnit`, and `createBiologic`. Each takes a variant
reference, loadout variant, INI name, numbering, geo centre and position,
heading, and a target `Taskforce`.

Three properties of this path constrain any runtime materialization:

- **It is gated on `Globals._useAssetBundles`.** Every `create*` method wraps its
  work in `if (!Globals._useAssetBundles)` and otherwise falls through and
  returns `null`. That flag is read from the options INI
  (`OptionsManager`, `UseAssetBundles`, default `false`). A caller must check the
  flag rather than trust a non-null return.
- **Loading is synchronous for vessels.** `createShip` passes `loadAsync: false`
  to `VesselLoader.loadShipFromInternalResources` regardless of its own
  `loadAsync` parameter, then calls `Physics.SyncTransforms()`. There is no way
  to spread a single ship spawn across frames through this API.
- **Construction is multi-stage.** The loader builds the root object, systems,
  sensors, launchers, magazines, hit zones, compartments, and flight deck; the
  caller then calls `init`, `initBehaviourTree`, per-`BaseSystem` `init`, sets
  the INI name, telegraph, and heading, and finally `SetFinalSettings`. A
  `ResourceLoader.ResourceBatch` completion callback calls `setInitialized()`.
  Failure partway leaves a partially built object.

### Identity

`ObjectBase.UniqueID` is a scene-local `int`. New objects get
`SceneCreator.GenerateUid()`, which is `Interlocked.Increment` of a static
`_UID`. Mission files restore `UniqueID` and also persist `File/CurrentUid` so
later allocations do not collide inside that scene. ECS `SPObjectRef.ObjectUID`
and save cross-links for spotted/processed contacts use this value. It is **not**
a campaign entity ID and will collide across missions and after a fresh load.

`ObjectBase._campaignTag` is a separate string, written as `CampaignTag` when
non-empty. The Runtime Bridge `CaptureUnit` reports `GameUniqueId`,
`CampaignTag`, and `IdentityProvenance` (`game-unique-id` vs `campaign-tag`).
Crimean Tom's durable identity remains the external entity ID; treat `UniqueID`
as a session-local handle. See [Tactical bubble](tactical-bubble.md).

### Disable, enable, and destruction

`ObjectBase.Disable()` is a native reversible suspension path. It sets
`Disabled`, clears control, removes the object from `ObjectsManager` and its
active task-force collections, adds it to `_taskforceDisabledUnits`, deactivates
the root GameObject, and disables its ECS entity. `OnFixedUpdate()` returns after
centre-tile maintenance when `Disabled`, while ordinary per-object update is
skipped by `GameMain`. `Enable()` reverses those registrations. Disabled units
are included by `SaveLoadManager` and reconstructed as disabled by
`SceneCreator`.

That is valuable for load shedding and a likely first quiet-removal experiment,
but it is not full dematerialization: the `ObjectBase`, its constructed systems,
the inactive root, and its entry in `GameMain._objects` remain allocated. It also
needs live tests for formation, hosted-aircraft, plotting/contact, trigger, and
save/load behavior.

The Phase 2 quiet-removal spike now rejects a plugin-composed deeper teardown.
`OnReturnedToPool()` only calls the private `ClearEntity()` seam; it does not
remove task-force/manager registrations, dispose the full object graph, or own
the root GameObject lifecycle. Combining that partial seam with manual Unity
destruction would be an unsupported teardown with orphan/leak risk. The first
surface family therefore supports retained `Disable`/`Enable` suspension only;
memory-releasing dematerialization is not claimed.

`ObjectBase.destroyObject(bool keepInFlightEffectAlive, bool score, TacView.TCEvent eventType)`
is the terminal destruction path and is semantically a **kill**, not a quiet
despawn. It detaches from `Formation`, clears user tasks, destroys uncontrolled
child aircraft, removes the object from its `Taskforce`, clears its ECS entity,
removes it from `ObjectsManager`, sets the destroyed flag, emits a TacView event
(default `Destroyed`), tears down towed systems, stops jamming, raises
`OnDestruction`, and deselects the camera if it was the selected object.

`score: false` suppresses scoring only. The destroyed flag, the TacView event,
the child-aircraft cascade, and every downstream AI and track reaction still
happen. A memory-releasing dematerialization path would have to extend the native
disable semantics with a carefully proven teardown of the remaining references
and GameObjects rather than call `destroyObject`.

## World coordinates and the floating origin

This is the part of the runtime with the least resemblance to a plain Unity
scene. Relocation, instantiated-set size, and native campaign mission pull-in
are three different radii; they are compared in [Tactical bubble](tactical-bubble.md).

### Geodetic position is the relocation/persistence representation

`GeoPosition` is a struct with `double _latitude`, `double _longitude`, and
`double _height`. Every object carries `_geoPosition`. During ordinary fixed
update `ObjectBase.CalculateGeoPosition()` derives it from the live Unity
transform and current centre tile; relocation and explicit teleport/load paths
can reconstruct the transform from `_geoPosition`. The two representations are
therefore synchronized bidirectionally. Tactical physics primarily uses the
float transform, while geodetic state is the stable representation to extract
across centre-tile changes.

### The tile grid and the unit scale

The world is divided into 0.2° × 0.2° tiles, 1800 of them around the equator,
wrapping at ±180° (`Utils.longLatToTile`). One tile is `330.71396` Unity units
(`Utils.longLatToLocal` / `longLatToLocalV3`).

The scale follows from `Utils.heightInMeterToUnity`, which multiplies by
`0.0148809375`:

```text
1 Unity unit = 1 / 0.0148809375 = 67.2 metres exactly
```

Two independent constants confirm it: a 0.2° tile is ~22.24 km, and
330.71396 × 67.2 m ≈ 22.2 km; and launch ranges are converted from nautical
miles by `× 27.559496`, where 1852 / 67.2 = 27.559. Ship-scale geometry is
therefore a small fraction of one Unity unit, and `Vector3.magnitude` between two
objects is in 67.2 m units, not metres.

### The current centre tile

`Globals._currentCenterTile` is the tile that local coordinates are relative to,
with `Globals._currentCenterTileUnityPos = _currentCenterTile * 330.71396f` fed
to shaders. Conversion is `Utils.longLatToLocal(geo, centerTile)` and back via
`Utils.worldPositionFromUnityToLongLat`.

It is set by `RenderPosition.setGlobalCurrentTilePos(geoPos)`, called from:

- `SceneCreator`, once at scene creation, from the mission INI's
  `Environment/MapCenterLatitude` and `MapCenterLongitude`;
- `RenderPosition.OnStart`;
- `ObjectsManager`, when the initial active object is selected;
- `RenderPosition.checkAndPerformRelocation()`, at runtime.

### Relocation (the floating origin)

`RenderPosition.checkAndPerformRelocation()` compares the camera's horizontal
local position against `Globals._rebaseToOriginSq`. `Globals._rebaseToOrigin` is
read from the options INI (`WORLD_SETTINGS`, `RebaseToOrigin`, default `1000`).
That value makes **1000 Unity units approximately 67 km**. When the camera exceeds it, the game
re-centres on the camera's current geo position and calls
`RelocationHandler.relocate(delta)`.

`RelocationHandler.relocate` shifts the whole world in one frame:

- recomputes every object's transform from its `_geoPosition` against the new
  centre tile, and offsets its alignment tasks;
- moves the particles world-location root;
- rewrites every registered `TrailRenderer`'s vertex positions;
- relocates projectiles, cloud chunks, effects, terrain chunks, scenery, and
  autogen;
- invokes every subscribed `Vector3Event` listener;
- forces a `DynamicSky` update and records `LastRelocationEventTime`.

It logs a `Relocation Event, delta: ... current center tile: ..., frame: ...`
line to `Player.log` under the terrain section, which is a convenient marker when
diagnosing position problems.

One asymmetry is worth knowing: `relocate` **skips** objects that are air units
without control and not destroyed. Those objects instead re-derive their own
position on the next fixed update, because `ObjectBase.OnFixedUpdate` contains:

```csharp
else if ((_hasControl || Disabled) && _lastCenterTile != Globals._currentCenterTile)
{
    base.transform.position = Utils.longLatToLocalV3(_geoPosition, Globals._currentCenterTile);
    _lastCenterTile = Globals._currentCenterTile;
}
```

Note that this per-object catch-up itself only applies to objects that are
`_hasControl` or `Disabled`.

### Consequences

- The **relocation threshold** is ~67 km of horizontal camera displacement from
  the current local origin. That is not by itself a measured precision or terrain
  radius. The process still has only one current centre tile, and it follows the
  camera rather than a scheduler assignment.
- Objects far from the current centre sit at large float coordinates until a
  relocation brings the origin to them. At 67.2 m per unit, a contact 500 km away
  is ~7,400 units out; at 5,000 km it is ~74,000 units, where float spacing is
  roughly half a metre. The actual usable physics, sensor, and loaded-terrain
  radius must be measured rather than inferred from the rebase threshold.
- Relocation is a whole-world, single-frame event that touches every object,
  every trail, and the terrain. It is not free and it is not silent.
- Because the derived transform is reconstructed from `_geoPosition`, the
  geodetic value is the right thing to extract, checkpoint, and compare. Unity
  transforms are only meaningful together with the centre tile that produced
  them.

### Teleport protection

`ObjectBase._teleportProtection` suppresses one fixed update's worth of
centre-tile correction (`if (_teleportProtection) { _teleportProtection = false; }`
runs *instead of* the reposition branch). The game sets it when code moves an
object. `WeaponSystemHardpoint` sets it on a weapon at release. The
surface and altitude correction path also reads it together with
`_correctAltitudeOnSpawn` and a `GameTime.TimeCompression > 80f` check. Any code
that repositions an object directly should expect to interact with this flag.

## Time and time compression

### The clock

`GameTime` is a static class exposing `time`, `unscaledTime`, `deltaTime`,
`fixedDeltaTime`, `sensorTime`, and `sensorDeltaTime`. `GameTime.OnUpdate()`
advances them:

```csharp
deltaTime      = TimeCompression * Time.deltaTime / Time.timeScale;
sensorDeltaTime = deltaTime / ((TimeCompression > _physicsTimeScaleCap)
                               ? TimeCompression / _physicsTimeScaleCap : 1f);
time       += deltaTime;
sensorTime += sensorDeltaTime;
```

Three properties follow directly:

- **Logical time advances by real frame duration.** `Time.deltaTime` is
  wall-clock frame time. Game time is not a fixed tick; the same scenario
  advances differently on different hardware or under different load.
- **`time` is a `float` accumulator with a private setter and is never reset.**
  `GameTime.OnStart()` resets the compression index, not the clock.
  `SceneCreator._missionStartTime = GameTime.time` captures a per-mission offset.
  `CurrentMissionSessionTime` subtracts the offset, so `time` keeps growing across
  missions for the whole process lifetime. Float precision degrades as it grows,
  and a great deal of guidance, sensor, and AI logic compares against absolute
  `GameTime.time` values.
- **Sensor time runs on a separate, capped budget** above the physics cap.

### Compression ladder

`GameTime._timeScaleValues` is a discrete ladder:
`0.1, 0.2, 0.5, 1, 2, 3, 5, 10, 20, 30, 50, 100, 200, 300, 500, 1000, 2000, 3000, 5000`.
The default index is 3 (`1x`).

`GameTime.OnStart(bool isDynamicCampaign)` sets the ceiling: for a normal mission
it reads `General/MaxTimeCompression` from `config.ini` (default `100`); the
dynamic campaign path calls `OnStart(true)` and permits `3000`.

### The physics cap

`_physicsTimeScaleCap` is `10`. The `TimeCompression` setter is where the
approximation begins:

```csharp
Time.timeScale = (value > _physicsTimeScaleCap) ? _physicsTimeScaleCap : value;
```

At or below the cap, Unity `timeScale` follows the requested compression. Above
it, Unity physics stays capped while logical time runs faster:
`GameTime.OnFixedUpdate` inflates `fixedDeltaTime` by
`TimeCompression / _physicsTimeScaleCap`, and `sensorDeltaTime` is divided by the
same factor. `GameTime.IsPhysicsApproximationEnabled()` reports this state.

High compression is therefore a different simulation, not the same simulation
evaluated more often. Anything calibrated or compared across the 10× boundary is
comparing two models.

The same cap leaks into subsystems that key off Unity's scaled clock rather than
`GameTime`. `TaskforceManager.OnUpdate` measures its data-link interval with
`Time.time`, which is capped at 10×, so above the cap the shared track picture
refreshes progressively less often per unit of game time.

### Frame-rate-driven physics timestep

`GameTime.OnUpdate` also adjusts Unity's own fixed timestep from the measured
frame rate, once per real second:

```csharp
if (Singleton<Fps>.Instance.getFps() < 30f)      { Time.fixedDeltaTime = 0.0333333f; }
else if (Singleton<Fps>.Instance.getFps() > 40f) { Time.fixedDeltaTime = 0.0166667f; }
```

with `DM._usingLowFrameRateTimestep` tracking which side it is on. A machine that
crosses the 30/40 fps hysteresis band changes its physics step mid-mission, which
changes projectile and vehicle integration. Two machines at different frame rates
are running different timesteps.

`GameTime.TimeDilation` (a Burst `SharedStatic<float>`) records the ratio of
requested compression to the capped Unity `Time.timeScale`, rising immediately
and decaying slowly. It expresses deliberate time dilation above the physics cap;
it is not a direct CPU-load or missed-deadline measurement.

Crimean Tom's controlled mode does not reuse the FPS-driven timestep as a load
lever. It fixes the logical step, reports falling tick throughput as client
pressure, and lets the coordinator remove optional work, migrate/demote whole
interaction islands, disable that process as a simulation source, or reduce the
global target ticks per wall second. See the
[active system architecture](../architecture/system-architecture.md) and the
[deferred distributed-simulation note](../future/distributed-simulation.md).

### Pause

`GameTime.Pause()` optionally mutes per-object and environment audio, forces a
`DynamicSky` update, calls `GameMain.EnforceFixedUpdate()` so queued state is
made consistent, sets `TimeCompression = 0`, and sets
`InputHandler.IsInPauseMode`. `GameTime.IsPaused()` tests whether
`Time.timeScale < 0.05f`. It does not read a separate flag.

Pause and compression are therefore **process-global**. There is no per-unit,
per-task-force, or per-region time scale, and there is no way to advance one part
of the scene while another is stopped.

## Task forces and information ownership

Each `Taskforce` owns units, formations, patrol zones, air strikes, missile
groups, task-force contacts, weapon allocation, data-link state, spotted-object
state, and end-of-mission unit/weapon registers.

`TaskforceManager.OnUpdate` runs a phased data-link cycle, skipped entirely while
paused. `_dataLinkUpdateRate` is read from INI (`General/DataLinkUpdateRate`,
default `1` second) and the elapsed measure is `Time.time`, i.e. Unity's scaled
clock. Within a cycle:

1. On the first frame of a cycle, reset the shared sensor-echo and
   detected-emitter indices and call `updateDataLinkStart()` on every task force.
2. Every frame, if `DM_Sensors._allowSensorUpdate`, call `updateDataLink(fraction)`
   on every task force with the fraction of the interval elapsed.
3. When the interval is exceeded: call `updateIncremental(1f)`, schedule
   `GeoPositionEcsSyncScheduler`, manually update the ECS
   `PlottingTableSystemGroup` with `SyncManager.ShouldUpdate = true`, publish
   `updateTaskforceContacts()` on every task force, and start the next cycle.
4. Otherwise call `updateIncremental(along)` with the remaining fraction.
5. Finally call each task force's own `OnUpdate()`.

The player does not read the ground-truth `ObjectBase` list directly.
The user-facing picture is a task-force-relative plotting table synthesized from
sensor tracks and shared information, republished on an interval, and that
interval is measured in capped scaled time.

## Global mutable state and randomness

Simulation-relevant randomness in this build comes from three process-global
streams, none of which is keyed per entity or per event:

| Stream | Declaration | Used by |
| --- | --- | --- |
| `Globals._unityRandom` | `new Unity.Mathematics.Random(12u)` | Missile INS drift and motor performance, biologic behaviour, TMA solution variance |
| `Globals._rnd` | `new System.Random()` with no seed | Pilot ejection rolls, automatic waypoint pattern generation |
| `AI._aiRandom` | `private static System.Random` with no seed | Per-unit AI preset-attack selection |

`Globals._unityRandom` has a fixed seed but is a single sequential stream, so the
value any given draw receives depends on how many draws every other subsystem has
made first. That count depends on object count and update order. The two `System.Random`
instances are default-constructed and therefore differ on every process launch.

The practical consequence is that exact replay is not guaranteed and two
processes cannot be assumed to agree. Deterministic-looking scenarios may still
repeat when they do not reach a differing random/order path, so divergence must
be measured rather than asserted for every run.

`Globals` also holds `_currentCenterTile`, `_missionSingletonsParent`,
`_useAssetBundles`, `_maxObjectAltitude`, `_rebaseToOrigin`, the shared
`_sensorEchoes` and `_detectedEmittingSensorSystems` pools, `_trailRendererList`,
and `_mainGameViewModel`. It is the de facto root of the tactical runtime.

## ECS and jobs

Important parallel/ECS uses include:

- track, vehicle, plotting-table, and sensor components;
- `PlottingTableSystemGroup`, updated manually at each data-link interval
  boundary rather than by the default ECS player-loop scheduling;
- `GeoPositionEcsSyncScheduler`, scheduled at the same boundary;
- `ObjectCalculationsJobScheduler` and `CollisionAvoidanceJobScheduler`,
  completed at the start of fixed update and rescheduled at the end, each guarded
  by `HasPreviousJobCompleted`;
- campaign group position updates through `IJobParallelFor`, batch size 32.

`ObjectBase.ClearEntity()` on destruction is the seam between the GameObject
world and the ECS world; a plugin that removes objects has to go through it or
leave orphaned entities.

## Dormant and diagnostic code

Several types look load-bearing from their names but do nothing in this build.
Recording them prevents wasted investigation:

| Type / member | State |
| --- | --- |
| `SeaPowerAI.AIController.OnUpdate` | Empty body, though it is called every frame and `OnStart` does initialize `AIPathFinder`. The established per-unit `AI` and `Taskforce` layers are the live ones. |
| `GameUpdater.lazyUpdate` | Empty body. |
| `CampaignFixedUpdater`, `CampaignLateUpdater` | Empty. |
| `SensorManager` | No construction or use was found in decompiled scripts beyond generated registration and `GameFixedUpdater`'s guarded call; a serialized scene component remains possible and must be checked live. If instantiated, `OnFixedUpdate` allocates two `count × count` `NativeArray<float>` buffers, completes an O(n²) range/bearing job, disposes the results unread, and logs every fixed step. It is not evidence of the live detection model. |
| `GameMain` lazy-update fraction | Computed from `Time.realtimeSinceStartup` then discarded. |

Real detection and track handling are distributed across `SensorSystem`
subclasses, `UnitInternalContact`, `PlottingTable`, and the ECS sensor systems.
See [Tactical simulation](tactical-simulation.md).

## Saves and mission lifecycle

`SaveLoadManager` uses INI-style files and serializes far more than unit
positions. Saves can include task forces, objects, waypoints, formations,
weapons, objectives, triggers, sensor/track state, map drawings, and mission
metadata. There are separate helpers for ordinary mission saves and campaign
saves.

Two parts of the save scope are **optional and read from configuration**
(`SaveLoadManager.LoadConfig`, from the general settings INI):

- `SaveLoadSensorDataEnabled` (default `true`) gates `SaveLoadSensorData`;
- `SaveLoadFlightDeckEnabled` (default `true`) gates `SaveLoadFlightDeck`.

Anything depending on sensor or flight-deck persistence has to read these flags
rather than assume the fields are present.

The save format has a native in-flight `WeaponBase` path. For each coalition,
`SaveLoadManager` writes the task force's weapon list to numbered weapon sections
and calls each weapon's `SaveStateToFile`. `SceneCreator.loadWeapons` and
`initWeapons` recreate those objects, parse references, and later launch them;
`WeaponBase`, `Missile`, `Torpedo`, and `Bomb` contain save/load overrides.

This establishes intent to persist task-force missiles, torpedoes, bombs, and
other included `WeaponBase` objects, not completeness. Chaff and noisemaker
objects are excluded from `Taskforce._taskforceWeapons`, so this path does not
cover every decoy. It must be tested mid-flight for
guidance links, absolute timers, target state, fuel/stage, and matching outcomes.
Pooled gun `Projectile` objects are not task-force `WeaponBase` objects and no
equivalent save path has been identified for them. Tactical bookkeeping such as
`WeaponsSpent` and `WeaponReport` is separate from both mechanisms.

`MissionManager` owns mission variables, objectives, trigger collections, map
areas/points, civilians, briefing/end data, and mission unload/reload behavior.
Mission completion produces the after-action report and a campaign-facing result
where applicable.

For Crimean Tom, treat tactical saves as game-owned compatibility and diagnostic
artifacts. The campaign service keeps its own versioned state and exchanges live
assignments, portable checkpoints, ordered commands, and tactical events through
the plugin. It does not use a mission input/result pair as the normal
continuous-campaign boundary.

## Architectural constraints for plugins

- Unity objects and most game singletons must be accessed on the main thread.
- Gate on `GameMain._initialized`; the update loops do not run before it.
- Reactive UI properties and ECS entities can lag the ground-truth object by an
  update phase; choose one authority for each observation.
- Avoid retaining raw `ObjectBase`, `Entity`, or view-model references across
  scene, load, materialization, or mission transitions.
- Use stable external IDs and re-resolve game objects after loads/transitions.
  Do not key durable campaign state on `ObjectBase.UniqueID`.
- Prefer `GeoPosition` over Unity transforms when recording or comparing
  position; a transform is only meaningful with its centre tile.
- Expect relocation events to move every transform without any object having
  "moved" in world terms.
- Use native `Disable`/`Enable` as the first suspension experiment; do not treat
  it as proven memory-releasing dematerialization, and do not treat
  `destroyObject` as a despawn.
- Do not assume repeatability: three unseeded or shared RNG streams and reverse
  insertion-order updates mean the same inputs are not guaranteed to produce the
  same outputs.
- A log marker proves the plugin entry point executed, not that a campaign hook
  or feature-specific patch ran.
