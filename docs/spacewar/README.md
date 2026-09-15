# Spacewar

Independent AnchorChain mod for Sea Power `0.8.2-23810+s366`.
Spacewar adds configurable satellites with circular and elliptical Earth orbits.

Spacewar owns the physical orbits. Nearby satellites have native aircraft
representations on the player's side, with tactical-map symbols and entries in
the native Formation Manager. Select a spacecraft, then
open **Orbit** in Unit Status to see its orbital elements, altitude, speed,
latitude, longitude and next orbit's ground track. Gold marks the selected craft.
The Orbit tab is unavailable for ordinary units. There is no separate satellite
list or spacecraft dropdown.

The tactical map also shows each spacecraft's next orbit as a colored ground
track, with a dot and name at its current physical latitude/longitude. This
includes spacecraft outside the local 3D area. Selecting a spacecraft thickens
its path. Tracks follow map pan and zoom, split at the date line, and update
every five simulation seconds. Markers update twice per real second, so native
pause and time compression apply. Geostationary tracks can collapse to a dot.
The overlay does not intercept map clicks or add drawings to mission saves.

The starter constellation contains four fictional satellites: two low-orbit
scouts, an equatorial geostationary relay and an elliptical relay. Role labels
describe the profiles; they do not activate native reconnaissance or communications.
The current visual and database template is the stock `civ_h700` aircraft, so
native labels still identify the class as Bizjet. A spacecraft mesh and dedicated
database classification remain to be added. The chart is a coordinate grid,
not a coastline map.

## Spacecraft type

`Spacecraft` is an independent C# domain type with no Unity or Sea Power assembly
dependencies. It owns identity, role, orbit, sampled physical position and
simulation time. `AdvanceTo` evaluates that physical state at the mission clock;
`Predict` evaluates a future position without changing current state. Clock rewind
is supported for starting another mission or loading a save.

`SpacecraftCatalog` owns all spacecraft, and the controller advances every one,
including those with inactive or missing native representations. `SpacecraftProxy`
is the Unity component that binds an aircraft to a spacecraft. It owns compressed
display coordinates, local-area enable/disable behavior and native UI integration.
The native aircraft type remains an implementation detail of that representation.
Physical state never comes from its transform, HUD altitude or aircraft physics.
Existing INI section names and the `SpacewarProfile` save key remain compatible.

## Install and configure

```powershell
dotnet build src/Spacewar/Spacewar.csproj -p:DeployToGame=false
dotnet build src/Spacewar/Spacewar.csproj -p:DeployToGame=true
```

Deploy with Sea Power closed. Enable **Spacewar** in Mod Manager and restart.
Deployment writes `Spacewar.dll` and `_info.ini` to
`Sea Power_Data/StreamingAssets/Spacewar`. It creates `spacewar.ini` only if that
file does not exist, preserving local constellation edits.

Edit the installed `spacewar.ini` and restart to change satellites. Each INI
section names one satellite. All values use a decimal point.

| Key | Meaning |
| --- | --- |
| `Role` | Descriptive label |
| `PerigeeKm` | Minimum altitude above the spherical Earth, at least 160 km |
| `ApogeeKm` | Maximum altitude, at least perigee and at most 100,000 km |
| `InclinationDeg` | Orbit-plane inclination, 0-180 degrees |
| `AscendingNodeDeg` | Ascending node longitude at clock zero |
| `PeriapsisArgumentDeg` | Periapsis orientation within the orbit plane |
| `MeanAnomalyDeg` | Orbital phase at clock zero |

Missing numerical keys default to a 500 km equatorial circular orbit at phase
zero. Invalid numbers or an invalid orbit reject the catalogue with a Spacewar
log message. At most 64 satellites are supported. The catalogue applies to every
mission while the mod is enabled.

## Motion and time

`Orbit.cs` solves Kepler's equation and rotates the orbital plane into a frame
that turns with Earth. It uses kilometres and seconds, Earth radius 6378.137 km,
gravitational parameter 398600.4418 km³/s² and sidereal day 86164.0905 seconds.
Positions are evaluated from time directly; they do not accumulate frame-step
integration errors. The element rotation follows the standard classical-elements
convention described by [poliastro](https://docs.poliastro.space/en/stable/autoapi/poliastro/core/elements/index.html).

Spacewar replaces aircraft motion and frame updates only for its spacecraft.
This bypasses atmospheric flight, fuel consumption and the native altitude clamp.
Native unit bookkeeping remains active; aircraft AI orders are suppressed for
spacecraft. Waypoints, speed and altitude orders do not change their orbits.

`OrbitalDisplay.cs` maps physical altitude to display altitude. Above 10 km,
`displayKm = 10 + 2 * ln(1 + (physicalKm - 10) / 2)`. A 500 km orbit displays
at 21.01 km; geostationary altitude displays at 29.58 km. Even the highest
supported orbit displays below 32 km. Conversion to Unity coordinates then uses
67.200066 metres per unit. The Orbit page reports physical altitude and speed,
plus display altitude and its ratio to physical altitude. Native HUD altitude
and velocity describe the displayed aircraft, not the physical spacecraft.

Representations enter within 450 km of horizontal map distance from the current
floating origin and leave beyond 500 km. The two thresholds prevent repeated
enable/disable transitions at the boundary. These are projected map distances,
not great-circle distances. Disabled representations leave the native active
unit lists and park at the origin with zero velocity. Their orbital profiles
continue to evaluate against mission time. They reappear when their ground
positions come back into the local area. Moving the camera to another area can
also change which spacecraft are local. Selecting a departing representation
is cleared before it is disabled. Position is reapplied after origin relocation,
including while paused.

Clock zero is the simulation reference, not a real astronomical epoch. Earth
longitude zero aligns with inertial longitude zero at that reference. The clock
is native `_missionStartTime + GameTime.missionSessionTime`, so native pause and
compression control orbital motion. Saved mission start time supplies the offset
after loading. Profiles must remain unchanged to retain the same ephemeris.
Each saved aircraft carries a `SpacewarProfile` key. Loading reattaches its
profile, and mission setup reuses restored spacecraft instead of duplicating them.
There is no separate advancing process or campaign synchronization.

The orbit library retains geometric elevation above a spherical surface observer.
There is no spacecraft-specific sensor or communications simulation. Compressed
representations are marked undetectable, cannot produce sensor contacts, are
rejected by native seeker targeting and engagement-range checks, and have
colliders and aircraft hit handling suppressed. They are display and selection
objects. Future orbital sensors and weapons must use the physical orbit;
compressed native distances cannot stand in for physical range.
Aircraft altitude in the geometric visibility calculation, terrain masking, refraction, sensor range,
clouds, Earth oblateness, precession, atmospheric drag, lunar/solar gravity,
eclipses, TLE/SGP4, launches, maneuvers and weapons are not modeled in this version.

## Verification

On 2026-09-14, Sea Power `0.8.2-23810+s366` loaded the compressed-display build in
`artifacts/spacewar-unit-check.ini`. Fresh logs recorded the four spacecraft,
then disabled Scout 2, Relay 1 and Relay 2 outside the local area. The bridge
reported only Tarawa and Scout 1 as active units. At 11.74 simulation seconds,
Scout 1's tactical-map symbol and HUD showed 68,933 ft, approximately 21.01 km.
It could be selected and viewed in 3D. Its physical orbit remains 500 km.
The Orbit tab appeared in the native UI tree. No Spacewar exceptions appeared.
Tested and deployed DLL SHA-256:
`DE2BFB0A7F500DF98E00C1D2EEBEF28673F41539E0C8C9C7E3DBFD5F86D59196`.

A check compiled against production `Orbit.cs` and `OrbitalDisplay.cs` passed
seven assertions covering the height threshold, LEO/GEO display heights, maximum
height, local-area hysteresis, distant-position rejection and unchanged physical
altitude. Build completed without warnings or errors.

The Orbit-content click was rejected after the panel became hidden during live
interaction. Its rendered layout, spacecraft exit/re-entry, repeated origin
relocation, time compression, save/load and combat exclusion remain unverified
in-game. The latter have implementation and static inspection only.
The bridge's current `position.altitudeMeters` field contains native height units;
use the game's altitude display or convert that field before comparing it to km.

Spacecraft were subsequently separated into an independent domain type and a
native proxy. Eleven checks compiled without game assemblies cover projection
plus spacecraft motion, paused state, prediction without mutation and clock rewind.

The tactical-map overlay and that refactor were verified in the same scenario
on 2026-09-14. Scout 1's orange path and label rendered on the local map. A zoomed
out view showed Scout 2's cyan track, Relay 2's green track and Relay 1's stationary
purple marker, although those three spacecraft had no active local aircraft.
Selecting Scout 1 thickened its path. The overlay remained aligned after map
maximization and zoom, and the mission resumed with no Spacewar exceptions.
The first overlay build did not render paths; explicitly assigning the native
parent map fixed it. The successful build had no warnings or errors.

Map-overlay build SHA-256:
`527C61FAE983E851867A2EC44DA96299DA5151A9ACECAEBFC34A431F7575E50C`.
Screenshots are in `%LOCALAPPDATA%/SeaPowerGameBridge/screenshots/`:
`b41e3fedd88a45ae8a87317c88aea6c0.png` shows the local track, and
`4caf02b62a194e55afc575c4df015e55.png` records the later map view.

Code map: `Plugin.cs` loads the independent mod, `SpacecraftCatalog.cs` reads the
constellation, `Orbit.cs` owns orbit calculations, `Spacecraft.cs` owns physical
state, `SpacecraftProxy.cs` updates native representations,
`OrbitalDisplay.cs` owns altitude compression and local bounds,
`OrbitalMap.cs` draws geographic tracks on the tactical map,
`SpacecraftPatches.cs` handles motion, combat exclusion and save identity,
`SpacewarController.cs` creates mission units and the selected spacecraft's page,
and `GroundTrack.cs` draws the coordinate chart.
