# Amphibs

Independent AnchorChain mod for Sea Power `0.8.2-23444+s358`.
`src/Amphibs/amphibious-variants.ini` registers seven amphibious variants without
replacing their stock ground units. They reuse the source models, equipment and
national variants. The Workshop description is blank.
See [the ground-unit survey](ground-unit-survey.md) for eligibility and sources.

## Water movement

Candidate profiles bypass the native ground-unit water stop and cap swimming
speed at `WaterSpeedKnots`. Conditional profiles do not register. Native ground
orders remain in use. Firing afloat is blocked unless `WeaponsAfloat=True`.

`WaterlineOffsetMeters` sets the model origin relative to sea level. Negative
values lower the hull, limited by seabed height in shallow water. `WakeScale`
scales native small-vessel bow-wave and propwash effects; zero disables them.
Effects originate near the bow and stern using `LengthMeters` and remain at
sea level independently of the hull offset. Emission stops on land, at rest or
when destroyed. These are visual wakes, not wake-homing torpedo targets.

The AAV7 and SA-8 passed an initial in-game water-waypoint check at 7 and 4.3 knots.
Waterline offsets are initial tuning, not measured drafts. Flotation samples native
Ceto ocean waves at the bow, stern, port and starboard edges of the measured hull.
It smooths heave, pitch and roll, with a 25-degree tilt limit. Wave queries ignore
wake overlays to avoid feedback. Spray emitters follow the local wave height.
This is kinematic wave following, not the ship compartment-force model: land
steering remains active, and flooding, capsizing and hydrodynamic thrust are not
simulated. Pausing freezes the smoothed pose. Shoreline transitions, save/load and
combat need further checks. `WaterRole`, dimensions other than length, mass and
propulsion are reference data; no sea-state restriction is enforced yet.
Boat Ops can carry and launch the amphibious AAV7 through its ground-unit loader.
Its default Tarawa complement includes four AAV7s, each occupying 2 SUs.

## Live water-effect tuning

The tuning panel is hidden by default. Set `ShowTuningPanel=True` in
`[WakeCoefficients]` in `water-effects.ini` and restart to enable it.
Selecting an amphibious vehicle then opens a draggable, resizable Water effects panel.
Bow position defaults to `1`, with a `0.5` to `2` slider range.
Bow position uses the box half-length: `1` is the bow edge, and `1.4` is 20%
of the full hull length beyond it. Other longitudinal coefficients use full length.
Surface, hull and stern foam default to disabled; spray, displacement and ripples remain enabled.
Switch to another unit and back to reopen a closed panel. Wake coefficients update
all spawned amphibious vehicles immediately, including while paused. The Vehicle
box group changes only the selected vehicle type's width, height and length
correction multipliers and waterline offset. Unity's vertical axis is Y.

The mod measures the assembled, undamaged main hull mesh in vehicle-local space
before creating effects. It excludes resource-container layouts, spare meshes,
LODs and mounted equipment.
Width controls foam size and expansion; length controls forward offsets, trail
spacing and momentum; height controls vertical offsets and wave displacement.
Particle scale is proportional to box width relative to a three-metre reference.
Box corrections allow hull-specific tuning. Catalogue dimensions are a logged
fallback when no hull mesh is found.

Separate checkboxes enable displacement, normal-map ripples, surface foam,
hull foam, stern foam, bow spray and propwash. Disable layers to inspect one
effect without losing its values. The native Ceto overlay supplies surface
height, normals and foam; native foam trails leave a fading wake. Spray keeps
uniform scale and uses an adjustable velocity multiplier.

Each effect's sliders sit below its enable checkbox and are disabled with it.
Effects stay on the vehicle centreline; lateral offsets are not configurable.
Old lateral-offset overrides are ignored. Hull and stern foam have independent
forward and vertical offsets, size, lifetime, spacing, expansion,
momentum, spin, jitter and opacity. Bow spray and propwash have separate offsets,
size and velocity controls. General settings control the speed range over which
effects reach full strength, relative to each vehicle's swimming speed. Geometry
sliders are box-relative coefficients; lifetime remains seconds and spin degrees/s.
Ceto's minimum trail spacing is about 6.72 metres.
Foam opacity scales with swimming speed, as it does for vanilla ships.

The panel exposes numeric tuning, not every vanilla asset option: particle
prefabs and overlay textures remain fixed, and cavitation is not implemented.

Save writes shared values to `[WakeCoefficients]` and box corrections plus waterline
height to the selected profile in `water-effects.ini` beside the deployed DLL.
Old per-profile wake parameters are ignored, not reinterpreted as coefficients.
It overrides the shipped INI and survives builds. Subsequent saves keep a `.bak`.
Revert restores the values from when the panel opened or its last successful save.
Copy saved overrides into the repository deliberately when promoting tuning to defaults.

## Build and icon

```powershell
./scripts/create-mod-icons.ps1 -Mod amphibs
dotnet build src/Amphibs/Amphibs.csproj
```

Builds deploy by default to `Sea Power_Data/StreamingAssets/Amphibs`. Close the
game first, or pass `-p:DeployToGame=false` for a compile-only build. Enable the
mod in the game's mod manager and restart to load it.

The icon script downloads the supplied U.S. Navy AAV photograph, verifies its
hash, crops it and adds the Amphibs title using the existing Workshop icon style.
Source and credit are recorded in `scripts/mod-icons.sources.json`.
