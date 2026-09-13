# Spacewar

Independent AnchorChain mod for Sea Power `0.8.2-23810+s366`.
Spacewar adds configurable satellites with circular and elliptical Earth orbits.

Select a player unit and open **Spacewar** in Unit Status.

- **Satellites** lists altitude, latitude, longitude, orbital period and whether
  each satellite is above the selected unit's geometric horizon.
- **Orbit** shows the chosen satellite's elements, speed and elevation, with a
  latitude/longitude chart of its next orbit's ground track. Gold marks the
  satellite; blue marks the selected unit. Select a satellite in either the
  table or the Orbit dropdown to inspect it.

The starter constellation contains four fictional satellites: two low-orbit
scouts, an equatorial geostationary relay and an elliptical relay. Role labels
describe the profiles; they do not activate native reconnaissance or communications.
These are orbital entities owned by Spacewar, not aircraft or native targetable
Sea Power units. The chart is a coordinate grid, not a coastline map.

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

Clock zero is the simulation reference, not a real astronomical epoch. Earth
longitude zero aligns with inertial longitude zero at that reference. The clock
is native `_missionStartTime + GameTime.missionSessionTime`, so native pause and
compression control orbital motion. Saved mission start time supplies the offset
after loading. Profiles must remain unchanged to retain the same ephemeris.
There is no separate advancing process or campaign synchronization.

Visibility is geometric elevation above a spherical surface observer. It does
not reveal contacts. Aircraft altitude, terrain masking, refraction, sensor range,
clouds, Earth oblateness, precession, atmospheric drag, lunar/solar gravity,
eclipses, TLE/SGP4, launches, maneuvers and weapons are not modeled in this version.

## Verification

Built with zero warnings and errors. A small check against the production
`Orbit.cs` covered circular altitude/speed/period, elliptical apsides, polar
latitude, overhead and opposite-side visibility, geostationary longitude and
invalid inputs. The UI and native save/load clock behavior have not been tested
in-game. No game was launched for this change.

Code map: `Plugin.cs` loads the independent mod, `SatelliteCatalog.cs` reads the
constellation, `Orbit.cs` owns motion and visibility, `SpacewarController.cs`
owns the native pages, and `GroundTrack.cs` draws the coordinate chart.
