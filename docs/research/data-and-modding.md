# Data and modding

## Storage model

Sea Power is strongly data-driven. The installed `StreamingAssets` tree in this
test environment contains thousands of INI, XML, audio, image, and compressed
asset files. INI is the dominant editable definition and save format.

The local root currently contains the base content under `original`, game/user
state under `user`, plus `ACConfigs` and the legacy pre-rename `CrimeanTom`
bridge directory. Current builds use `SeaPowerRuntimeBridge`. Workshop directories are
added separately by `FileManager`.

Representative data domains include:

- ships, submarines, aircraft, helicopters, and land units;
- weapon and sensor systems;
- ammunition, warheads, damage, effects, and audio;
- squadrons, variants, loadouts, nations, and eras;
- missions, campaigns, objectives, triggers, localization, and briefings;
- environment, clouds, camera, controls, and general configuration.

## Virtual file lookup

`FileManager` builds an ordered collection of `SearchDirectory` entries from:

- the local user-data directory;
- installed/subscribed Steam Workshop items;
- local mod directories and saved enable/order state;
- the protected base-game directory.

The enable flags and order are persisted in `usersettings.ini` under
`[LoadOrder]`. The base game is loaded last; user data is described by the UI as
the primary location and loaded first. File lookup walks the ordered search
directories and therefore permits a mod to override a virtual path supplied by
base content.

The Mod Manager can synchronize Workshop dependencies, enable already installed
dependencies, subscribe to missing dependencies, and topologically sort mods.
It detects circular dependencies and excludes the cycle from normal sorting.

### Practical consequence

Always identify both the virtual path and the physical source that won. A file
present under `original` may not be the definition being used. `FileManager`
contains a `GetModSourceForFile` path for this reason.

## INI handling

`IniHandler` is the common parser/writer. Callers use two patterns:

- `openIniFile` for an explicit or virtual path;
- `openVirtualIniFile(domain, category, name)` for merged system registries such
  as sensors, weapons, and damage.

The game caches INI data and maintains modifier registries. Accepting a new mod
order refreshes search directories and invalidates those registries; changing a
file on disk is not guaranteed to update a running mission.

Localization can be INI- or JSON-backed. Mission-editor annotations use JSON
inside the broader INI mission model and preserve unknown extension properties,
which is useful for forward compatibility.

## Unit construction

`ObjectsManager` selects a type-specific loader. `ObjectBaseLoader` then builds a
unit from data and Unity resources. Its responsibilities include:

- object root, submodels, meshes, materials, liveries, emblems, flags, and hull
  or serial numbers;
- animations, colliders, hit zones, damaged models, and compartments;
- sensors, target channels, cables/towed systems, optical profiles;
- launchers, guns, magazines, decoys, supply and cargo systems;
- flight-deck systems and hosted air groups;
- common identity, role, movement, crew, signature, and doctrine data.

Type-specific loaders add ship, submarine, aircraft, helicopter, land-unit, or
biologic behavior. A strategic unit definition should therefore reference the
game's unit/variant/loadout IDs rather than duplicate every tactical parameter.

## Weapons and sensors as registries

System definitions are shared registries. Unit INIs reference systems; the
loader materializes `SensorSystem` and `WeaponSystem` instances on the unit.
Ammunition definitions provide target types, flight/warhead characteristics,
penetration class, impact size, and other guidance or launch constraints.

This indirection is favorable for campaign generation: a runner can select a
unit, variant, and loadout without understanding the entire weapon simulation.
The plugin can ask the game to materialize the selected tactical object.

## Missions as exchange artifacts

The mission editor and `SaveLoadManager` demonstrate that a tactical situation
can be represented as INI sections for:

- scenario metadata, time, weather, center point, and player side;
- groups, individual units, formation, positions, and waypoints;
- objectives, triggers, zones, points, and map annotations;
- briefing/localized text and associated assets;
- doctrine, AI behavior, morale, and special mission data.

That makes a generated mission file a useful import/export, debugging, and
recovery artifact. It is not the primary continuous-campaign transport. Any
generator should be conservative: use examples emitted by the installed
mission editor, preserve unknown fields, and version Crimean Tom metadata in its
own section.

## Data mods versus runtime plugins

Two mechanisms coexist:

| Mechanism | Purpose | Health evidence |
| --- | --- | --- |
| Search-directory data mod | Overrides/adds INI, media, missions, localization, and related assets. | Correct winning virtual files and in-game content. |
| AnchorChain plugin | Executes managed code and runtime hooks. | Fresh plugin entry-point marker in `Player.log`. |

The in-game checkbox list describes the first mechanism. It must not be used as
the sole proof for the second.

## Safe development rules

- Keep generated decompilation in git-ignored `decompiled/`.
- Do not redistribute the game's assemblies or decompiled source.
- Build/deploy through `pnpm build`; do not hand-copy DLLs.
- Preserve user and Workshop data unless the user explicitly authorizes cleanup.
- Record the game build in generated campaign saves and refuse or migrate
  incompatible schemas explicitly.
- Prefer additive Crimean Tom sections/files over replacing broad base files.
