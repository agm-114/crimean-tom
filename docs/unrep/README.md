# UNREP

Independent AnchorChain mod for Sea Power `0.8.5-261008`.
Flight-deck traffic and zone APIs vary between game assemblies. UNREP uses them
when present. Builds without them use landing-point claims and blocking links,
the existing 30 m conflict check, and a physical recovery-strip clearance check.
Normal flight operations retain priority in both paths. The compatibility fallback
has been compiled against the installed assembly, but has not been verified in-game.
Adds a Supplies tab to Unit Status for player-controlled surface ships.
For development context, verified behavior and a runnable demo, see the
[development handoff](handoff.md).
The Supplies tab monitors inventories with category/search filters, quantities,
capacities, current use/production rates and minimum retained stocks. Identical stores in the same category
share one row with summed quantities/capacities and a list of locations. Mounted
weapons and magazine ammunition remain separate. Transfers use the underlying
usable magazines rather than replacing them with a synthetic inventory.

Enable UNREP and Enable VERTREP checkboxes expose separate Unit Status tabs for
that ship. Both default off. UNREP lists compatible receiving ships with a queue
and an expandable supply plan for each ship. VERTREP has a planned
transfer queue and a pool of assigned helicopters, with no total quantity to enter. Disabling a method hides
its tab and cancels that ship's outgoing jobs of that type, with normal breakaway
or cargo return. Incoming transfers do not require the receiver's tabs enabled.

Expand a ship in UNREP to preview its compatible supplies and current transferable
amounts. Check or uncheck Use to enable or disable individual supplies; select a
row and use UP/DOWN to change their order. ENABLE ALL and DISABLE ALL are shortcuts.
QUEUE adds the ship to the receiver queue. Ship UP/DOWN changes priority for the
next free alongside station; ships already approaching or alongside retain their
station. CANCEL releases the ship through normal breakaway. REQUEUE starts another
visit after completion or cancellation, preserving supply order and enabled rows.

Select a transferable item in Supplies, enter a minimum retained quantity, then
choose SET MINIMUM. This absolute quantity covers all transferable storage with
the same resource key aboard that ship, even across displayed categories.
Mounted weapons do not count toward that floor. UNREP previews, ongoing alongside
deliveries and helicopter pickups respect the minimum. Raising it does not recall
cargo already picked up or delivered. Limits do not prevent combat expenditure,
native automatic replenishment or other mods from consuming stores. The switches
and minimums persist in vessel saves. Receiver queues, supply ordering and enabled
rows are session-only, as are active jobs.
Empty optional rows, including absent aircraft and empty replenishment cargo,
are hidden. Critical UNREP reserves, ship ammunition, and configured aviation
ammunition remain visible when depleted. Rows update as stocks change.
Locations format internal names such as `WeaponMagazineMK45_1` as
`Mk 45 magazine 1`; internal resource identifiers remain unchanged.

## Inventory

- Ship magazines list individual missiles, torpedoes, shells, countermeasures
  and other ammunition present in the ship's native magazine records.
- Mounted launcher weapons are shown separately and cannot be offloaded.
- Aviation stores show native common ammunition points and accountable
  categories. Common points are not a physical per-weapon inventory.
- Aircraft rows show available squadron aircraft, pending launch batches and
  recovery servicing. Aircraft transfer by flying and recovering, not as cargo.
- Replenishment cargo shows each native supply system's fuel, ammunition points
  and accountable categories.
- Fuel reserve, food, fresh water and spare parts use UNREP-owned inventories.
  These are configurable gameplay quantities, not historical manifests.
  Fuel, food and parts are consumed; fresh water is produced up to capacity.
  Empty stores do not stop propulsion or damage control. Aircraft refuelling
  and native replenishment cargo remain separate from these reserves.

Use / production shows signed kg/hour for these reserves. Negative rates consume
stock; positive rates produce it. Rates update with engine orders and actual DC
work, and show zero at an empty consuming bin or a full producing bin. While
paused, the column shows the operating rate but quantities do not change.
Consumption applies to living, operational surface ships regardless of whether
their UNREP/VERTREP tabs are enabled. Minimum retained stocks limit transfers,
not onboard consumption.

Fuel use follows the demanded engine setting, including during acceleration:
full reverse uses 100% of the full-ahead rate, full ahead 100%, and flank 200%.
Custom speed orders interpolate from stop to full and from full to flank using
the ship's native telegraph speeds; reverse orders interpolate against full
reverse speed. All stop consumes no fuel reserve.

Food and spare parts have a steady hourly draw. Each DC team actually repairing
leaks, fires, flooding, structure or systems adds extra parts use. Teams merely
assigned to an undamaged compartment add nothing. Native repairs continue at
their usual rate even after spare parts reach zero.

The `[Consumption]` section of `supplies.ini` sets each rate as
`BaseKgPerHour + KgPerTonnePerHour × displacement`. Defaults per tonne/hour are
0.25 kg fuel at full, 0.002 kg food, 0.001 kg parts and 0.02 kg water production.
Each active DC team adds 250 kg/hour of parts use. These are gameplay defaults.
The prefixes are `FuelFull`, `FoodUse`, `SparePartsUse`, `WaterProduction` and
`SparePartsPerDCTeam`. Override either coefficient under a ship catalogue section,
or set both to zero to disable that rate. Missing settings use these defaults,
including in existing installed INI files. Changes take effect after restarting.
All stock changes use simulation time and clamp between zero and capacity.

Ships can participate in UNREP and VERTREP simultaneously by default. To require
one method at a time, edit `supplies.yml` beside the installed `UNREP.dll`:

```yaml
allowConcurrentUnrepAndVertrep: false
```

Restart Sea Power after changing this setting. A missing file or omitted setting
defaults to `true`. Both methods must still be enabled in the ship's Supplies tab.
UNREP leaves receiving space reserved for cargo already aboard VERTREP helicopters.
Deployment includes `YamlDotNet.dll` and preserves existing `supplies.yml` settings.

`supplies.ini` defines initial quantities and capacities as
`BaseKg + KgPerTonne × displacement in tonnes`, using native vessel displacement.
For example, `FoodBaseKg=100` and `FoodKgPerTonne=2` give a 5,000-tonne ship
10,100 kg of food. `FoodCapacityBaseKg` and `FoodCapacityKgPerTonne` determine
its storage limit independently. Fuel, water and spare parts use the same scheme.
`[Defaults]` applies to every ship; a catalogue section such as `[usn_lha_tarawa]`
overrides either term independently. Invalid displacement uses the base alone;
results are capped at one billion kg and starting stock cannot exceed capacity.
Native stores are read from the game, not scaled by displacement.
Deployment preserves an installed configuration. Restart after editing it.

## Transfers

Transfers use simulation time and stop while paused. Queue a receiver in UNREP to
send it to a rendezvous astern, then a port or starboard station.
The supplier keeps its route with demanded speed capped at 8 knots. It must be
independent or a formation leader. Two receivers can take stations; others queue.
Receivers leave their formation temporarily and restore their prior route and
speed after breakaway. Replacing a receiver's waypoints releases it to the player.

Receivers choose the nearest free side whose station is clear of other hulls.
They align astern and match velocity before advancing alongside. Speed correction
accounts for lateral approach, heading and closing speed, with gradual changes.
Station entry and exit use separate distance tolerances. Transfers require five
simulation seconds of steady supplier course, measured by turn rate.
Native collision steering and escape speed changes remain active; parallel partners
are exempt only when projected hull corridors stay clear for 90 seconds.
Receivers keep trying to reach their assigned station without an approach timeout
or automatic restart farther astern. Breakaway
finishes on clearing the lateral boundary, or reports failure and restores orders
after 300 seconds without progress. These changes have only been build-checked.

Default handling limits are 0.2 nm and 12 knots for both ships, plus station
position, heading and relative-speed checks. Rates are 20 kg/s, 0.1 items/s or
1 ammunition point/s, configurable in `[Transfer]`. Both ships must be controlled
by the player, in the same taskforce, and allow supply in standing orders.
Turning, leaving range or exceeding speed pauses handling. The route planner
does not evaluate a safe replenishment corridor; choose open water clear of land
and other traffic. A stationary supplier does not start moving automatically.

Transfers debit the source and credit compatible destination storage as work
finishes. Individual weapons transfer only between magazines containing the
same ammunition reference. Accountable categories and common ammunition points
can move between supply systems and flight decks. Native automatic replenishment
still operates separately. Destroyed ships cancel transfers; damaged storage
cannot participate. Alongside UNREP serves enabled supplies in their listed order,
each to the source minimum or receiving capacity, during one station visit.
Unavailable, disabled or full supplies are skipped. Disabling or moving a supply
changes subsequent handling immediately; delivered quantities remain delivered.
When no enabled supplies can move, the receiver breaks away. Requeue it to make
another visit when more stock or space is available. Delivered counters reset per
visit. Cancel never refunds cargo already delivered.

### Helicopter replenishment

VERTREP has Helicopters and Transfers subtabs, with assigned, flying and idle
counts above both. Helicopters provides the assignment controls and aircraft
table. Transfers lists compatible receiving ships with expandable supply plans.
Each plan shows enabled supplies, available, delivered and airborne quantities.
Use the supply UP/DOWN buttons to set its order and Use switches to exclude items.
QUEUE activates a receiver, and ship UP/DOWN sets its priority. Helicopters choose
the first runnable supply in the first runnable receiver at each pickup.
Reordering does not redirect cargo already airborne.

In Helicopters, select an airborne helicopter and choose ASSIGN. Assigned helicopters serve the
queue continuously, carrying food, water or spare parts using native flight
movement. Their table shows current supply, receiver, cargo, delivered quantity
and flight/handling status. Multiple helicopters share the queue; pickups reserve
receiving space and respect the source minimum across concurrent loads. Each trip
carries at most 500 kg, transferred immediately at touchdown. Helicopters
fly a direct approach to compatible native recovery spots, keep their rotors running,
then use vanilla takeoff for the next leg without a cold-start delay. They wait for a clear spot and acceptable
sea state. Nearby aircraft and Deckpark units can block a landing spot.
Each ship has a shared waiting pool feeding a short queue at each recovery point:
one active helicopter and one selected follower joining or holding at alignment.
Selection runs after all visits update and chooses the nearest compatible waiting
helicopter for each opening. After five simulated minutes waiting, the oldest
eligible helicopter gets priority over newer arrivals. Existing selections remain
stable, and normal-flight interruptions preserve waiting age. Two available points
can select two helicopters in the same update. Everyone else remains in orbit.
The selected helicopter receives one ship-relative approach order to a gate 300 metres behind the
landing entry along the recovery rig's heading. Within 150 metres of that gate,
`HelicopterApproach` takes control of movement. It preserves arrival velocity,
settles onto the approach line and aligns with the rig. It holds there under the
same controller until the deck and corridor are clear, then reserves the landing
spot and advances to the native approach-offset point. Once it moves at least
75 metres forward from alignment, its follower position opens. The next helicopter
can prepare while the first approaches, lands, handles cargo and takes off. The follower stays
at alignment until the first aircraft releases the deck. It issues no micro-waypoints and does not invoke vanilla
Approach or RTB, which can add unwanted routing.
The controller integrates velocity with bounded acceleration and jerk, limits
closure by stopping distance, and accounts for the destination ship's motion.
Pitch and roll follow acceleration; angular rates and movement limits use the
helicopter's native parameters with conservative approach caps. This is a custom
kinematic controller, not the vanilla flight-physics solver or a timed position lerp.
At the entry, within 0.25 metres and 0.05 m/s relative speed with settled attitude,
it hands the current pose to HelicopterLanding without snapping to a target.
Native landing then owns the descent and rotation with no movement override.
Normal queued launches and recoveries have priority. The custom approach claims
the deck only when leaving alignment and yields to normal operations before native
landing starts. Known native flight operations block their own point and conflicting
points/zones. Unallocated native tasks temporarily block the whole deck so they can
choose a point first. A native landing already in progress completes, retaining the
existing cargo turnaround. Cancelling while still in custom approach releases the
queue position and redirects the airborne helicopter without a landing/takeoff detour.
Guided helicopters retain `HasControl` and enabled hitboxes so the native radar,
visual sensors, weapon seekers and damage collision paths still see them. The mod
suppresses only their native movement and own AI while it owns pose. Native landing
and deck turnaround retain that targeting eligibility too. Ship-parented aircraft
keep the original world-origin relocation exception. Destroyed or disabled aircraft
leave the controller and release their slots. Detection and weapon suitability
still follow the game's normal rules.
The previous scheduler and targeting changes loaded on 2026-09-14 in the six-CH-46
fleet playground. Combat behavior remains unverified. The shared-pool/per-point
queue, earlier follower preparation and waiting-age changes are build-checked only;
they have not been deployed or observed in-game.
Waiting helicopters orbit their current destination at 0.5 nm and 150 m altitude:
the supplier before pickup, or the receiver while carrying a delivery. Native
flight controls follow successive ship-relative points, advancing before arrival
to keep moving. Cancelled deliveries returning cargo orbit the supplier instead.
An already committed landing completes, then clears the deck if normal operations
are waiting. Departures wait for a clear corridor.
Departure runs native preflight animations and takeoff, then climbs under native
flight control. The landing spot stays reserved until the helicopter is 20 metres
above the deck; only then does it receive the next transfer route.
Both ships need a compatible helicopter deck. `[Helicopter]`
configures capacity. The old `HandlingSeconds` setting is ignored. These are gameplay
defaults shared by helicopters, not aircraft-specific lift certifications.
Fuel, ammunition and aircraft cannot be carried by this mode.

When source stock reaches its minimum or receiving storage is full, the helicopter
skips that row and serves the next runnable transfer. With no runnable rows, it
stays assigned and waits at the supplier. It resumes automatically when stock
above the minimum and receiving space become available. Lowering a minimum can
resume a waiting operation. Queued receivers remain queued at those limits.
CANCEL removes a receiver from scheduling and returns its airborne cargo while
keeping helicopters assigned. Disabling a supply returns that supply's airborne
cargo and stops new pickups. REQUEUE is available after cargo return, preserves
the receiver's supply settings and resets its delivered counters. UNASSIGN
releases the selected helicopter after returning any cargo. Disabling VERTREP
cancels all outgoing plans and releases the assigned helicopters.
Unavailable ships or source storage, helicopter loss, low fuel and replacement
flight orders can also end the assignment.

Cargo leaves source inventory at pickup and enters receiving inventory at delivery.
Cancelling sends undelivered cargo back to the source. Replacing flight orders
returns undelivered cargo to source accounting immediately and releases the
helicopter. Losing the helicopter loses its sling load. Low fuel ends the shuttle.
Initial launch and final hangar recovery remain player operations. Finishing a
landed assignment first takes off, then resumes the helicopter's previous flight
orders. Deck turnarounds skip hangar taxiing, elevators and despawn, keeping the same aircraft and assignment alive.
There is no visible sling, hose or crane model.

Native saves retain native stock changes. UNREP saves its additional inventories
on each vessel and includes airborne cargo in the source's saved stock.
Active transfer orders are not restored after loading. Finish or cancel transfers
and allow breakaway before saving; native saves can otherwise retain temporary
movement orders without the UNREP job that owns them.

```powershell
./scripts/create-mod-icons.ps1 -Mod unrep
dotnet build src/UNREP/UNREP.csproj
```

Builds deploy `UNREP.dll`, `_info.ini` and the generated thumbnail to
`Sea Power_Data/StreamingAssets/UNREP`. Enable UNREP in Mod Manager and restart
the game. For compilation without deployment, use `-p:DeployToGame=false`.

The icon uses [Rentz and Ranger Unrep](https://commons.wikimedia.org/wiki/File:Rentz_and_Ranger_Unrep.jpg),
a U.S. Navy photograph by PH3 Layman. Its download URL and SHA-256 are recorded
in `scripts/mod-icons.sources.json`; the icon script downloads and crops it.
