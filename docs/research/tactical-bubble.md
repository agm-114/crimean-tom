# Tactical bubble, identity, and off-camera simulation

This note is for Crimean Tom's long-term split: Sea Power simulates a **player
tactical island**, and the campaign kernel simulates everything that is **not**
instantiated in that process. The installed game does not cull simulation to the
player's map or camera. "Bubble" is therefore three different radii that must
not be mixed.

Observations are against Sea Power `0.8.2-23444+s358` (Steam app `1286220`,
depot buildid `24297410`, Unity `6000.0.67f1`, PhysX `4.1.2`).
`Seapower-Scripts.dll` SHA-256
`33f4051f46788d7cf7b0032ef492231d1292cc49a0bb172ce0d16d9d244c5cbb`, last written
19 August 2026. Paths are relative to `decompiled/game/Seapower-Scripts/`.

## Three radii

| Radius | What it actually is | Approximate size on this build | Source |
| --- | --- | --- | --- |
| Camera relocation | When the **camera** is far enough from the current local origin, the whole world is re-centred | `RebaseToOrigin` default 1000 Unity units ≈ **67.2 km** | `RenderPosition.checkAndPerformRelocation`, `Globals._rebaseToOrigin` |
| Instantiated tactical set | Every live `ObjectBase` in `GameMain._objects`, including units the player cannot see | Measured to **~300 km** radius / **~376 km** pair separation in Red Sea Breakout without proving weapon/terrain continuity | [Phase 1 live evidence](../evidence/phase-1-live-evidence-2026-08-20.md) |
| Native campaign mission pull-in | Which **abstract campaign groups** `CampaignMain.SetupGroupsForTacticalMission` copies into a generated tactical mission | `Location.GetDistance(...) / 1000 < 925.9999990463257` ≈ **500 nmi / 926 km** from the central group | `CampaignMain.cs` |

Crimean Tom's operational kernel is a fourth layer: units that **never become**
`ObjectBase` in this process. That is not a Sea Power radius. It is campaign
policy.

## What one Sea Power process simulates

**Code evidence.** `GameMain` walks `GameMain._objects` backwards every Unity
`Update` and `FixedUpdate`. Disabled objects skip ordinary per-object update;
everything else that is still registered keeps running, including units far
from the camera and far from the current centre tile.

**Observed.** In a loaded Red Sea Breakout capture, 40 objects spanned 302 km
from the origin and 376 km pair-to-pair. Three were beyond the 67.2 km rebase
threshold while the camera was only 5 km from the origin. Across five unpaused
seconds their geodetic positions and kinematics hashes still changed. That is
evidence that off-camera units continue to integrate, not that a 300 km
sensor/weapon/terrain bubble is qualified.

`Disable()` is reversible suspension: the constructed object, systems, inactive
root, and `GameMain._objects` entry remain. `destroyObject` is a kill. Neither
is a quiet "remove from the player's bubble" API. See
[Runtime architecture](runtime-architecture.md).

### What relocation is not

Relocation follows the **camera**, not the scheduler and not the selected unit.
It is a single-frame whole-world shift of transforms, trails, projectiles,
clouds, effects, and terrain chunks. It does not stop simulating distant
`ObjectBase` instances. Air units without control skip the bulk relocate and
re-derive their transform on the next fixed update only if they have control or
are `Disabled`.

Float precision at large local coordinates is the real geographic limit of one
process, and it is still unmeasured for combat. At 67.2 m per Unity unit, a
contact 500 km from the origin sits ~7,400 units out. Do not treat the 67 km
rebase trigger as a simulation culling radius.

## Native dynamic-campaign materialization

`CampaignMain.StartMission(group)` does **not** stream entities into an already
running tactical scene. It:

1. Clears `MissionEditorModel.Groups` and adds the central group.
2. Adds every other campaign group whose `Location.GetDistance` is under
   ~926 km (500 nmi).
3. Writes per-unit `GeoPosition` from each group's station offsets.
4. Calls `MissionEditorModel.SetupMission(this, groupOfUnits)`.

`CampaignMain.SceneManager_sceneLoaded` is empty. The pause-menu save/exit
commands on the dynamic-campaign path throw `NotImplementedException`. Treat
this as an unfinished prototype of "generate a mission from nearby abstract
groups," not as a live handoff API.

Abstract campaign groups themselves are not `ObjectBase`. `CampaignUpdater`
advances them with a Burst `IJobParallelFor` that steps `float2` locations at
`GameTime.deltaTime * speedKnots * 0.514444` m/s along graph waypoints, then
updates patrols, airports, ports, and `IUpdateable` groups. Adds/removes are
deferred. That graph motion is the native analogue of Crimean Tom's
Operational kernel, still inside Unity, still on `GameTime.deltaTime`.

## Land units versus abstract land

Tactical land is a first-class `ObjectBase` subclass.

`LandUnitParameters.LandUnitSubType` is `AirBase`, `Installation`, `MobileUnit`,
`AAA`, `SAM`, `MissileSite`, `Radar`, `Port`, `Bridge`, `OilRig`. Mobile units
drive a `Rigidbody` from `OnFixedUpdate` when they have control. Installations
still run fire, destruction, altitude correction against loaded terrain chunks,
and the shared weapon/sensor stack.

`SceneCreator` also loads **background** land units from mission
`BackgroundCityFile`, `BackgroundAirportFile`, `BackgroundPortFile`, and
`BackgroundInstallationFile` sections. Those are scenery/tactical objects in
the scene, not campaign-graph groups.

Crimean Tom's Fulda seed therefore marks Central Front formations
`SimulationDomain.LandAbstract` with a hard Operational ceiling: they must not
be promoted into `LandUnit`. Coastal SAM, radar, and ports that should exist
inside a Sea Power bubble are a different family and would materialize as
`LandUnit`.

## Identity

| Identifier | Lifetime | Role |
| --- | --- | --- |
| `ObjectBase.UniqueID` (`int`) | One tactical scene. Allocated by `SceneCreator.GenerateUid()` (`Interlocked.Increment` of `_UID`). Restored from mission INI `UniqueID`. | Native object key, ECS `SPObjectRef.ObjectUID`, save cross-links for spotted/processed contacts. **Not** a campaign entity ID. |
| `ObjectBase._campaignTag` (`string`) | Persisted on save as `CampaignTag` when non-empty. | Native hook for an external campaign instance. The Runtime Bridge reports `IdentityProvenance = campaign-tag` when this is set. |
| Crimean Tom entity ID | Journaled campaign identity | Survives disable, scene reload, and worker replacement. `get-world-snapshot` exposes both this and `GameUniqueId`. |

A plugin that keys durable state on `UniqueID` will collide across missions and
after reload. Use the campaign tag or an external ID, and treat `UniqueID` as a
session-local handle.

## Implications for units outside the player's bubble

For Crimean Tom:

1. **Inside one Sea Power process, "outside the player's view" is still
   tactical.** Off-map units that are instantiated keep integrating. Do not
   expect Sea Power to cheaply abstract them.
2. **Capacity is the instantiated set**, including `Disable`d retained objects.
   Load shedding has to demote islands out of Unity (Operational kernel) or
   accept retained cost. See Phase 2 suspension evidence.
3. **A second Sea Power process does not share this origin.** Each client is
   one centre tile, one `Time.timeScale`, one pause. Distant theater that must
   not pay Unity cost belongs in the campaign service.
4. **Native campaign pull-in (~500 nmi) is a mission generator heuristic**, not
   a measured physics bubble and not a Crimean Tom assignment radius.
5. **Land combat that is not a `LandUnit` stays Operational.** Abstract
   formations should never be streamed into Sea Power.

The follow-up measurement that is still open: a distant sensor/weapon
engagement that crosses loaded vs unloaded terrain, including collision,
effects, guidance, and damage. Until that exists, do not claim a qualified
tactical radius larger than the 67.2 km relocation trigger plus the Phase 1
kinematics observation.

`get-world-snapshot` now reports geodetic maxima, objects whose tiles are not
in `TerrainManager.TerrainChunkDictionary`, and launched weapons on those
unloaded tiles (`SpatialDiagnostics` schema 2). `pnpm game:bubble-probe`
samples `/game/world` twice and writes that envelope only after a tactical
mission and its initial terrain load are ready. A non-zero
`activeWeaponCountBeyondLoadedTerrain` is a hint, not a qualified combat
radius. A zero count is not proof that such combat is impossible.

## Related notes

- [Runtime architecture](runtime-architecture.md) — origin, time, disable/destroy.
- [Tactical simulation](tactical-simulation.md) — sensors, AI, weapons, damage.
- [Campaign systems](campaign-systems.md) — linear, task-force, and map campaign.
- [Source map](source-map.md) — types to re-open after a game update.
