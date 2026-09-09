# Flight Deck Tweaks

Independent AnchorChain mod. Adds a Loadout dropdown to pending-launch rows in
the Flight Operations table and tracks preparation separately for each aircraft.
The game still owns crew allocation, deck space, the task queue and launching.
Aircraft launch as the original batch, only after every member is ready.

## Preparation

Aircraft without a tracked hangar record start with empty internal fuel and no
mounted ammunition. Returning aircraft retain their remaining internal fuel and
weapons through vanilla recovery servicing. The next player preparation batch
uses available recovered aircraft from that squadron first, then untracked
aircraft. One assigned crew works on one aircraft at a time, in this order:

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

Hover over a row's State cell or closed Loadout dropdown for each aircraft's fuel in kg and percentage,
effective refueling rate while below full, and time to full fuel. Missing, partially loaded,
excess and incompatible weapons show quantities and remaining work time per type.
Each type also shows its completion estimate including earlier refueling,
loading and unloading operations. Queued estimates start once a crew is assigned.
Rates and times include aircraft/nation modifiers and deck difficulty, assuming
uninterrupted crew.
Target stores progress is weighted by mounting work; fuel and unloading are
reported separately.

With the Loadout dropdown open, hover over an alternative to estimate the swap.
The estimate uses a copy of current fuel and stores, including partial loading
and removal of incompatible weapons. It shows the assumed crew count, excludes
crew/deck waiting, and flags insufficient ammunition. Estimates refresh when the
tooltip opens. Hovering does not change preparation or reserve ammunition.

Scope:

- Progress belongs to individual aircraft within a batch or squadron's hangar
  queue. Loadout swaps and cancellation preserve it. Aircraft are not bound to
  the same serial number when respawned by the game.
- Mounted weapons remain reserved while an aircraft is aboard. Recovery does
  not also make those weapons available in the ship's ammunition pool.
- Stores are tracked by ammunition reference and quantity, not individual pylons.
  Fractional quantities represent loading work, not launchable partial weapons.
  External tanks have mounting time; their fuel is not modeled separately.
- Recovery servicing duration, aircraft already airborne, AI preparation and scripted
  launches keep their native behavior. Normal player recovery captures fuel and
  stores before despawn; instant/debug recovery is not captured. Spawned aircraft use the complete native
  loadout and fuel state once preparation finishes.
- Tactical saves retain detailed preparation in an extra field on the native
  pending-task record and a separate hangar field, including aircraft still
  servicing. Old saves without those fields keep native timing for their
  existing tasks. The vanilla aggregate timer is still written for compatibility.

## Timing configuration

`rearm-times.ini` sits beside the deployed DLL. Aircraft and weapon tables contain
explicit exceptions only. Missing rows use the defaults and are not generated,
so these tables stay small.
Edit it and restart the game to tune rates. Deployment preserves an existing file.

| Section | Values |
| --- | --- |
| `AircraftRefuelSeconds` | Aircraft reference → seconds from empty to full internal fuel |
| `WeaponMountSeconds` | Ammunition reference → total crew seconds per round/item, replacing base plus mass |
| `WeaponRemoveSeconds` | Ammunition reference → crew seconds per round/item removed, replacing the unload fraction |
| `AircraftOperationTimeMultipliers` | Aircraft reference → preparation duration multiplier, default 1 |
| `NationOperationTimeMultipliers` | Operating ship/airfield nation → preparation duration multiplier, default 1 |
| `Defaults` | `MountSeconds`, `MountSecondsPerKg`, `RefuelFixedSeconds`, `RefuelSecondsPerKg`, `UnloadMultiplier`, `WeaponTimeMultiplier` |

Default weapon loading is `type base + mass in kg × MountSecondsPerKg`, then
multiplied by `WeaponTimeMultiplier`. Missing mass contributes zero. The general
base is 45 seconds; type bases are 0.15 for projectiles, 2 for countermeasures and
rockets, 5 for sonobuoys, 10 for bombs, 20 for fuel tanks, 30 for depth charges and
400 for torpedoes. `MountSecondsPerKg=0.05`, `WeaponTimeMultiplier=1`, and removal
takes 35% of the mount time. Five explicit weapon overrides retain longer vanilla
preparation for Phoenix, Harpoon, GBU-24, UMGT-1 and Mk54 depth charges.

The refuel table contains 15 outliers. For all other aircraft:

```text
full-tank seconds = RefuelFixedSeconds + RefuelSecondsPerKg × capacity kg
constant kg/second = capacity kg / full-tank seconds
```

Defaults are 600 fixed seconds plus 0.03 seconds per kg. The fixed term is part
of the rate calculation, not a separate delay. A half-full aircraft needs half
the full-tank time. An explicit aircraft override replaces full-tank seconds,
still using a constant rate. Fixed-wing capacity comes from `[Performance]`;
helicopters use `[Physics]`. These are gameplay tuning values, not physical
service rates. Aircraft and nation multipliers apply to the resulting time.

The offline comparison puts 335 of 561 loadouts within 25% of native preparation
time, and 415 within 35%. It is approximate, not an in-game timing measurement.
Some native loadout times differ without a corresponding equipment difference.
For example, the Ka-27's ASWHunterLight remains substantially faster than its
native 30 minutes.

| Loadout | Native minutes | Calculated minutes |
| --- | --- | --- |
| SH-2F ASW | 30 | 25.7 |
| F-14A AirToAir | 15 | 18.4 |
| F-14A AirToAirIntercept | 30 | 32.9 |
| Ka-27 ASW | 30 | 26.5 |

Both operation multipliers apply to refueling, mounting and removal. They multiply
together, and the game difficulty setting still scales the resulting duration.
For example, aircraft `1.25` × nation `0.8` leaves duration unchanged. Values above
1 are slower. Nations refer to the operator, not where the aircraft was built.
These modifiers do not change recovery servicing, taxiing, launch intervals or AI
tasks. The stock instant setting remains instant.

All configuration values must be positive and at most 86,400, except
`MountSecondsPerKg` and `RefuelSecondsPerKg` may be zero to disable mass scaling. Removal must be
faster than mounting. Invalid configuration logs an error and leaves new tasks
on native timing. The generated table is rewritten in sorted order, so custom
comments are not retained.

## Code boundaries

`Preparation/PreparationState.cs` contains the game-independent aircraft and crew
work model. `PreparationCatalog` reads loadout requirements and service rates;
`TaskPreparation` adapts that model to native task timing. `HangarPreparation`
captures normal landings and retains squadron aircraft through servicing,
cancellation and tactical saves. `StoreReservation` accounts for weapons held
aboard; `PreparationSave` handles pending-task save data. UI code reads preparation
state without owning its timers.

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
