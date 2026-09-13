# Airdrop

Independent AnchorChain mod for Sea Power `0.8.2-23810+s366`.
Configured player-controlled transport aircraft carry a finite manifest of ground
units. The starting profile gives each stock C-141B four HMMWVs and two HMMWVs
with M2 guns. Other aircraft require an explicit profile in `airdrop.ini`.

Select a transport and open **Airdrop** in Unit Status. Choose a ground unit and
quantity, then **ARM RELEASE**. The aircraft follows its existing flight orders.
Cargo releases along that flight path when the aircraft is airborne, wings are
level and the terrain, altitude and speed checks pass. **DISARM** cancels the
remaining releases without consuming cargo.

The default envelope is 150-600 metres above ground and 120-200 knots. Releases
are at least five simulation seconds apart. Water, steep terrain and occupied
landing areas block release. A blocked sequence stays armed until conditions
permit release or the player disarms it. Pausing stops releases.

Each successful release immediately creates a controllable native ground unit
below the aircraft, in its taskforce and national livery. The ground unit keeps
its normal weapons, movement, damage and mission-save behavior. The mod does
not simulate parachute descent, drift, airborne pallets or landing damage.
It does not take over aircraft movement or select a drop zone on the map.

## Cargo accounting

Each aircraft owns its starting complement. Successful spawns reduce aboard
counts once and increase released counts. Failed spawns retain cargo and disarm
the sequence. Aircraft destruction loses cargo still aboard. Released counts
record deliveries, not surviving ground units. There is no automatic reload or
recovery of dropped units.

Tactical saves record aboard and released counts on each aircraft. Loading a
save disarms release commands. Saved ground units use the game's own save
format. Save keys absent from older saves use the configured starting manifest.
Keep profiles unchanged while continuing a saved mission.

Manifest weights include a gameplay allowance for pallets. Capacity, weights,
quantity and release limits are gameplay defaults, not certified load planning
or a historical manifest. Cargo mass limits the configured complement; this
version does not modify the game's aircraft flight model or fuel consumption.
The native C-141B ammunition supply system remains independent. Supply crates
and UNREP integration are not implemented.

## Build and configuration

```powershell
dotnet build src/Airdrop/Airdrop.csproj -p:DeployToGame=false
dotnet build src/Airdrop/Airdrop.csproj -p:DeployToGame=true
```

Close Sea Power before deployment. Explicit deployment copies the DLL and
metadata to `Sea Power_Data/StreamingAssets/Airdrop` and preserves an existing
`airdrop.ini`. Enable Airdrop in the mod manager and restart.

Section names in `airdrop.ini` are aircraft catalogue IDs. Each ground-unit row
is `land_units/catalogue_id=count,massKg`. Total configured mass must fit
`CapacityKg`; at most 64 units can be aboard. Missing ground-unit assets display
as unavailable. Invalid configuration disables the mod with a game-log error.
Aircraft eligibility is explicit, so bombers, tankers and airliners do not gain
cargo merely because they are large.

## Code and verification

- `CargoCatalog.cs` reads profiles and owns per-aircraft manifest counts.
- `DropOperations.cs` checks the release envelope, spawns units and saves counts.
- `AirdropController.cs` owns the native tab and compact manifest table.
- `Plugin.cs` registers the independent `crimean-tom.airdrop` AnchorChain mod.

The project compiles with zero warnings and errors. Live checks covered paused
arming, two ground-unit releases, disarming, overwater blocking and tactical
save/load. See [verification](verification-20260912.md) for evidence and limits.
Use `scenarios/airdrop-check.ini` for ground releases and
`scenarios/airdrop-water-check.ini` for the overwater block.
