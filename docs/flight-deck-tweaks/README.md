# Flight Deck Tweaks

Independent AnchorChain mod. Adds a Loadout dropdown to pending-launch rows in
the Flight Operations table and tracks preparation separately for each aircraft.
The game still owns crew allocation, deck space, the task queue and launching.
Aircraft launch as the original batch, only after every member is ready.

## Preparation

New player preparation tasks start with empty internal fuel and no mounted
ammunition. One assigned crew works on one aircraft at a time, in this order:

1. Refuel.
2. Load built-in guns, chaff and flares.
3. Remove stores incompatible with the selected loadout, including excess quantities.
4. Load weapons shared by more of the aircraft's date-appropriate loadouts first.
5. Load the more specialized weapons last.

Changing loadouts keeps fuel, common stores and fractional loading or unloading
progress. Incompatible stores remain aboard until crews remove them; removal is
faster than mounting. Both loadouts must fit the ship's ammunition stock while
incompatible stores are still mounted. Those stores return to stock as unloading
finishes. Native code continues reserving the complete requested loadout.

State shows counts by actual activity, such as `1x Refueling`, `1x Awaiting Crew`
and `1x Ready`. Info shows assigned crew and the estimated time until the batch is
ready, assuming its current crew allocation. The game's flight-deck timing
multiplier still applies. Instant preparation settings remain instant.

Scope:

- Progress belongs to the queued batch, not a persistent aircraft serial number.
  A loadout swap preserves it; fully aborting the task discards it.
- Stores are tracked by ammunition reference and quantity, not individual pylons.
  Fractional quantities represent loading work, not launchable partial weapons.
  External tanks have mounting time; their fuel is not modeled separately.
- Recovery servicing, aircraft already airborne, AI preparation and scripted
  launches keep their native behavior. Spawned aircraft use the complete native
  loadout and fuel state once preparation finishes.
- Tactical saves retain detailed preparation in an extra field on the native
  pending-task record. Old saves without that field keep native timing for their
  existing tasks. The vanilla aggregate timer is still written for compatibility.

## Timing configuration

`rearm-times.ini` sits beside the deployed DLL. On the first preparation task,
missing entries are filled from installed game/mod aircraft and ammunition data.
Edit it and restart the game to tune rates. Deployment preserves an existing file.

| Section | Values |
| --- | --- |
| `AircraftRefuelSeconds` | Aircraft reference → seconds from empty to full internal fuel |
| `WeaponMountSeconds` | Ammunition reference → crew seconds per round/item |
| `WeaponRemoveSeconds` | Ammunition reference → crew seconds per round/item removed |
| `Defaults` | Fallback `MountSeconds`, `RefuelKgPerSecond` and `UnloadMultiplier` |

Starting defaults are 4 kg/s refueling, 120 seconds per external weapon, 0.3 seconds
per projectile, 2 seconds per countermeasure and 5 seconds per sonobuoy. Removal takes 35% of mounting
time. These are tuning values, not measured servicing times. Generated explicit
rows take precedence over defaults; delete a row to regenerate it from defaults.
All configuration values must be positive and at most 86,400; removal must be
faster than mounting. Invalid configuration logs an error and leaves new tasks
on native timing. The generated table is rewritten in sorted order, so custom
comments are not retained.

## Code boundaries

`Preparation/PreparationState.cs` contains the game-independent aircraft and crew
work model. `PreparationCatalog` reads loadout requirements and service rates;
`TaskPreparation` adapts that model to native task timing. `StoreReservation`
handles incompatible ammunition held during swaps; `PreparationSave` handles
tactical save data. UI code reads preparation state without owning its timers.

## Build

Source: [src/FlightDeckTweaks](../../src/FlightDeckTweaks). See the
[repository README](../../README.md) for shared build prerequisites and AnchorChain
setup. Run the commands below from the repository root.

Build without deploying:

```powershell
dotnet build src/FlightDeckTweaks/FlightDeckTweaks.csproj
```

Explicitly deploy only this mod:

```powershell
dotnet build src/FlightDeckTweaks/FlightDeckTweaks.csproj -p:DeployToGame=true
```

Deployment copies `FlightDeckTweaks.dll` into the game's
`Sea Power_Data/StreamingAssets/FlightDeckTweaks` directory. Override `SeaPowerDir`
or `AnchorChainDll` when using another installation. Restart Sea Power after
deploying an updated DLL. The stateful preparation model has offline checks for
priorities, loadout changes, crew scheduling and save roundtrips. Native UI,
inventory and save integration still need an in-game check. Startup was verified
in Sea Power 0.8.2-23444+s358 after fixing a Harmony hook-name collision. Do not
name ordinary patch methods `Prepare`: Harmony treats that name as its patch
initialization hook, even when the method also has a prefix attribute.
