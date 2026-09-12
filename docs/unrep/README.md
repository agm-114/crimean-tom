# UNREP

Independent AnchorChain mod for Sea Power `0.8.2-23810+s366`.
Adds a Supplies tab to Unit Status for player-controlled surface ships.
For development context, verified behavior and a runnable demo, see the
[development handoff](handoff.md).
The Supplies tab monitors inventories with category/search filters, quantities,
capacities and minimum retained stocks. Identical stores in the same category
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
  They do not yet affect propulsion, aircraft refuelling, consumption or repairs.

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
use vanilla approach and landing on compatible native recovery spots, keep their rotors running,
then use vanilla takeoff for the next leg without a cold-start delay. They wait for a clear spot and acceptable
sea state. Nearby aircraft and Deckpark units can block a landing spot.
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
