# Deck Tweaks

The project and DLL are `DeckTweaks`; the plugin ID is `crimean-tom.deck-tweaks`.
Deployment renames an existing `FlightDeckTweaks` folder to `DeckTweaks` and
disables the old DLL. If both folders exist, resolve the duplicate before deploying.

Independent AnchorChain mod. Adds a Loadout dropdown to pending-launch rows in
the Flight Operations table and tracks preparation separately for each aircraft.
The game owns crew allocation, deck space, the task queue and launching.
Aircraft launch as a batch, only after every member is ready.

## Preparation

Aircraft without a tracked hangar record start with empty internal fuel and no
mounted ammunition. Returning aircraft keep their remaining internal fuel and
weapons. As each clears the deck, Deck Tweaks queues it on its previous loadout.
Aircraft from the same returning flight, squadron, and loadout join the same
batch while it is holding for launch and has room. They wait for crew, deck space,
and supplies, then hold for your **Launch** order. You can change their loadout or
abort in Flight Operations. Each assigned crew works on one aircraft at a time:

1. Service a recovered aircraft for five minutes at the base preparation rate.
2. Refuel.
3. Load built-in guns, chaff and flares.
4. Fill weapon stations that are known to be free. Load weapons shared by more of the aircraft's date-appropriate loadouts first.
5. Remove excess or incompatible stores as needed, then fill newly available stations before unloading the next type of store.

Changing loadouts keeps fuel, common stores and fractional loading or unloading
progress. Incompatible stores remain aboard until crews remove them; removal
defaults to 1.1 times mounting time. Both loadouts must fit the ship's ammunition stock while
incompatible stores are still mounted. Those stores return to stock as unloading
finishes. Native code reserves the complete requested loadout.

**Abort** queues the batch to unload its mounted stores with any crews left
over from normal preparation. The row stays visible until every aircraft is
empty, then the aircraft return to the hangar with their remaining fuel.
**Cancel Abort** resumes the selected loadout from the stores still aboard.
The row hides Preparation, Launch and Prioritise while unloading.

State shows counts by actual activity, such as `1x Refueling`, `1x Loading Mk 46`,
`1x Unloading Mk 46`, `1x Awaiting Crew` and `1x Ready`.
Use **+** and **−** beside the total flight size in State to adjust a preparing
batch one aircraft at a time. **+** adds an available aircraft from the same
squadron. **−** returns the last aircraft to the hangar with its fuel and
mounted stores. The other aircraft keep their preparation progress.
Info shows assigned crew and the estimated time until the batch is
ready, assuming its current crew allocation. The game's flight-deck timing
multiplier applies. Instant preparation settings complete all work immediately.

With [UNREP](../unrep/README.md#aviation-fuel-and-deck-tweaks) installed and
aviation fuel enabled, crews draw internal fuel from the ship as they load it.
If the tank runs dry, affected aircraft wait and resume as stock arrives. This
also applies to instant preparation. Recovery, abort and loadout changes keep
fuel already aboard. Deck Tweaks works independently when UNREP is absent.

Select the caret beside the batch ETA in Info to show per-aircraft details across
the Loadout and Info cells, below the loadout dropdown. Select it again to restore
the ETA. The caret disappears when the batch is ready. Hover over the ETA, State
text or closed Loadout dropdown for the same
details. They show each aircraft's fuel in kg and percentage,
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
  queue. Loadout swaps and Cancel Abort preserve the current fuel and stores.
  Aircraft are not bound to the same serial number when respawned by the game.
- Mounted weapons remain reserved while an aircraft is aboard. Recovery does
  not also make those weapons available in the ship's ammunition pool.
- Stores are tracked by ammunition reference and quantity, not individual pylons.
  Fractional quantities represent loading work, not launchable partial weapons.
  Station layouts identify where a retained weapon could be mounted. If the
  layout cannot rule out a conflict, crews clear the possible blocker first.
  External tanks have mounting time; their fuel is not modeled separately.
- Normal player recovery captures fuel and stores before despawn. AI and
  instant/debug recovery use native servicing. Spawned aircraft use the complete
  native loadout and fuel state once preparation finishes.
- Tactical saves retain detailed preparation in an extra field on the native
  pending-task record and a separate hangar field, including aircraft still
  servicing. Saves without those fields use native timing for their
  existing tasks. The vanilla aggregate timer is also written for compatibility.

## Timing configuration

Open **Options → Gameplay → Deck Tweaks** for the preparation rates and
exception tables. **Use mod defaults** starts checked and follows the settings
shipped with each update. Uncheck it to edit the settings, then select
**Accept**. Custom settings are saved to `CrimeanTom/Settings/DeckTweaks.ini`
under the game's local data directory. A previous `rearm-times.ini` seeds the
custom values the first time you unlock the page.

Expand an override table to edit its values. In custom mode you can search by
in-game name or ID, choose an entry, enter a value, and select **Add**. Select
**Remove** to return an entry to its normal rate. Mounting, removal and refueling
rates apply when Deck Tweaks calculates a loadout's work.
Work already calculated for a pending batch keeps its rates until recalculated.

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
base is 60 seconds; type bases are 0.21 for projectiles, 2.8 for countermeasures and
rockets, 7 for sonobuoys, 14 for bombs, 28 for fuel tanks, 42 for depth charges and
560 for torpedoes. `MountSecondsPerKg=0.1`, `WeaponTimeMultiplier=1`, and removal
takes 110% of the mount time unless a weapon has an explicit removal override.
Explicit mounting overrides are 360 seconds for Phoenix, 600 for Harpoon,
900 for GBU-24, 1,080 for UMGT-1 and 420 for Mk54 depth charges.

The refuel table contains 15 outliers. For all other aircraft:

```text
full-tank seconds = RefuelFixedSeconds + RefuelSecondsPerKg × capacity kg
constant kg/second = capacity kg / full-tank seconds
```

Defaults are 300 fixed seconds plus 0.015 seconds per kg. The fixed term is part
of the rate calculation, not a separate delay. A half-full aircraft needs half
the full-tank time. An explicit aircraft override replaces full-tank seconds,
using a constant rate. Fixed-wing capacity comes from `[Performance]`;
helicopters use `[Physics]`. These are gameplay tuning values, not physical
service rates. Aircraft and nation multipliers apply to the resulting time.

The tuning targets roughly 20% refueling and 80% stores for typical armed
loadouts starting empty. Selected transport and reconnaissance aircraft use
2,700-second full-tank refueling overrides.

This is not a forced split for every loadout. Fuel rate stays constant for an
aircraft regardless of its weapons, and stores take time per item.
Heavy loads take longer, unarmed aircraft have little or no stores work, and
returning aircraft need only the fuel and stores they actually used.

Both operation multipliers apply to servicing, refueling, mounting and removal. They multiply
together, and the game difficulty setting scales the resulting duration.
For example, aircraft `1.25` × nation `0.8` leaves duration unchanged. Values above
1 are slower. Nations refer to the operator, not where the aircraft was built.
These modifiers do not change taxiing, launch intervals or AI
tasks. The stock instant setting completes preparation immediately.

All configuration values must be positive and at most 86,400.
`MountSecondsPerKg` and `RefuelSecondsPerKg` can be zero to disable mass scaling.

## Code boundaries

`Preparation/PreparationState.cs` contains the game-independent aircraft and crew
work model. `PreparationCatalog` reads loadout requirements and service rates;
`TaskPreparation` adapts that model to native task timing. `HangarPreparation`
captures normal landings and retains squadron aircraft through servicing,
cancellation and tactical saves. `StoreReservation` accounts for weapons held
aboard; `PreparationSave` handles pending-task save data. UI code reads preparation
state without owning its timers.

## Workshop screenshots

See [Regenerate the Workshop screenshots](workshop-screenshots.md).

## Store names

Deck Tweaks reads store names from Sea Power's ammunition localization. To name
a store in your mod, add its ammunition reference under `[AmmunitionNames]`
in `language_en/ammunition_names_ext.ini` in your mod folder. Use the matching
`language_*` folder for translations. Sea Power merges entries from enabled
mods and uses English when a translation is missing.

## Build

Source: [src/DeckTweaks](../../src/DeckTweaks). See the
[repository README](../../README.md) for shared build prerequisites and AnchorChain
setup. Run the commands below from the repository root.

Build without deploying:

```powershell
dotnet build src/DeckTweaks/DeckTweaks.csproj -p:DeployToGame=false
```

Build and deploy only this mod, the default on Windows:

```powershell
dotnet build src/DeckTweaks/DeckTweaks.csproj
```

Deployment copies `DeckTweaks.dll` and `_info.ini` into the game's
`Sea Power_Data/StreamingAssets/DeckTweaks` directory and includes the Workshop `thumbnail.jpg`
once `scripts/create-mod-icons.ps1` has generated it. Override `SeaPowerDir`
or `AnchorChainDll` when using another installation. Restart Sea Power after
deploying an updated DLL. Verify native UI, inventory and save integration in
the game using the repository's Sea Power testing skill.

Do not name ordinary patch methods `Prepare`: Harmony treats that name as its
patch initialization hook, even when the method also has a prefix attribute.
