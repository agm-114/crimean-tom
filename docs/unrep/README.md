# UNREP

Independent AnchorChain mod for Sea Power `0.8.2-23810+s366`.
Adds a Supplies tab to Unit Status for player-controlled surface ships.
For development context, verified behavior and a runnable demo, see the
[development handoff](handoff.md).
The Supplies tab lists inventories with category/search filters, quantity,
capacity and storage location. Select a transferable row and a receiving ship,
enter a quantity or use MAX, then choose TRANSFER.
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

Transfers use simulation time and stop while paused. Choose Alongside UNREP to
send the receiver to a rendezvous astern, then a port or starboard station.
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
cannot participate. Stock shortage or a full destination ends the job with its
delivered amount retained. Cancel never refunds cargo already delivered.

### Helicopter replenishment

Choose Helicopter VERTREP and select an airborne helicopter. It shuttles food,
water or spare parts between the ships using native flight movement. Each trip
carries at most 500 kg, with 30 seconds at pickup and delivery. `[Helicopter]`
configures capacity, handling duration and station tolerance. These are gameplay
defaults shared by helicopters, not aircraft-specific lift certifications.
Fuel, ammunition and aircraft cannot be carried by this mode.

Cargo leaves source inventory at pickup and enters receiving inventory at delivery.
Cancelling sends undelivered cargo back to the source. Replacing flight orders
returns undelivered cargo to source accounting immediately and releases the
helicopter. Losing the helicopter loses its sling load. Low fuel ends the shuttle.
Launch and eventual recovery remain player operations; finishing resumes the
helicopter's previous flight orders, not an automatic landing task.
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
