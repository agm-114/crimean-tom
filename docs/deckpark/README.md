# Deckpark

Independent AnchorChain mod for Sea Power `0.8.2-23810+s366`.
Deckpark creates native ground units in fixed positions on player-controlled
surface ships. They follow the ship's translation, heading, pitch and roll while
keeping native sensors, weapon systems, ammunition, targeting and damage.

## Use

1. Select a ship and open **Deckpark** in Unit Status.
2. Choose a slot and ground unit. The starter list contains M163 Vulcan, M42,
   SA-8 and SA-13 vehicles.
3. Check the position. Values are metres relative to the ship's origin:
   positive starboard, positive up, positive forward. Negative values mean
   port or aft. Click **SET POSITION** to apply edits, then **MOUNT**.
4. Use **HOLD/FREE** for weapon release and **RADAR ON/OFF** for search radars.
   The mounted vehicle remains selectable in the native Formation Manager for
   sensors, engagements and ammunition inspection.

Mounting creates a new unit in the ship's taskforce and national livery, with its
normal starting ammunition. It does not transfer an existing unit from shore.
Mounted units start weapons free; search-radar emission is a separate control.
Native target detection, engagement rules and ammunition limits still apply.
This is not an automatic interception guarantee or a replacement for ship CIWS.

Tarawa has four configured slots using the stock flight-deck height. Other
surface ships receive two editable starter slots at quarter-length fore and aft,
initially 10 m above the origin. Adjust those heights to the actual deck before
mounting. Slots can be repositioned after mounting without replacing the unit.
The mod checks spacing between mounted units, but does not detect superstructures,
deck edges, firing obstructions or flight-deck occupancy. Keep mounts clear of
aircraft operating areas. Deckpark does not reserve native aircraft parking spots.

Each slot can be mounted once per mission. Destroyed units remain expended slots;
there is no remove-and-replace ammunition refill. There is no stow, replenishment,
deck load calculation or effect on ship stability in this version.

## Attachment and persistence

Mounted vehicles are stationary relative to the deck. Their rigidbodies are
kinematic and their native driving/terrain correction is disabled. Native weapon
systems still update; the carrier's velocity is retained for launch kinematics.
Collision pairs between a mount and its own carrier are ignored without disabling
the mount's hitboxes against other objects. Native navigation avoids the carrier
hull and excludes its deck mounts as separate collision-avoidance targets.
A destroyed or removed carrier loses
its mounted units. A destroyed mount cannot fire.

Tactical saves use native ground-unit records for ammunition and damage, plus
`DeckparkMount` for parent UniqueID, slot, offset, heading and weapon permission.
Ships save expended slots in `DeckparkUsedSlots`. Mounts resolve their parent by
UniqueID after loading; unresolved mounts cannot fire. Keep Deckpark enabled and
ship slot IDs unchanged when continuing a save containing mounts.

## Build and configuration

```powershell
dotnet build src/Deckpark/Deckpark.csproj -p:DeployToGame=false
dotnet build src/Deckpark/Deckpark.csproj -p:DeployToGame=true
```

Deploy while the game is closed. Enable **Deckpark** in Mod Manager and restart.
Explicit deployment copies the DLL and metadata to
`Sea Power_Data/StreamingAssets/Deckpark`, preserving an existing `deckpark.ini`.

In `deckpark.ini`, `[GroundUnits]` contains stock land-unit catalogue IDs with
`True` values. Add only standalone units with suitable models and weapon systems;
whole SAM sites and buildings are not suitable deck cargo. Other sections are
ship catalogue IDs. Each row uses a slot number from 1 to 8 and four values:
`starboard,height,forward,heading`. Restart after changing the catalogue.
Use a decimal point. Installed assets remain the source for weapons and sensors.

`DeckCatalog.cs` owns profiles; `DeckOperations.cs` owns mounting and save hooks;
`DeckMount.cs` follows ships; `DeckCollision.cs` excludes mounts from navigation
obstacles; `DeckparkController.cs` owns the native menu.
`deckpark-check.ini` is a disposable moving-Tarawa scenario.

## Verification

The project builds with zero warnings and errors. Runtime attachment verification
is recorded in `verification-20260912.md`. Combat and save/load require separate
live checks; retaining native weapon code does not prove successful interception.
