# Well Deck

Independent AnchorChain mod for Sea Power `0.8.2-23810+s366`.
Select a player-controlled Tarawa and open Well Deck beside Flight Deck in
Unit Status. The tab shows embarked craft, staging capacity and
boat operations. Other ships have no tab
unless configured. Each ship has a separate inventory.
The operations table uses native GridView headers with draggable column dividers.
The tab resizes its controls to the available width and gives remaining height
to the scrolling operations table. A crew utilisation bar shows active preparation crews.
READY, LAUNCH and ALL use gold labels; row actions use the standard gray style.
Each preparation row has Abort and Preparation/Launch controls. Preparation
holds the boat aboard when ready; Launch enables automatic launch without
resetting progress. Recover All sits beside the capacity bars and is disabled
when no deployed, controllable boats remain to recall.

## Starting complement

`wet-decks.ini` sits beside the DLL. Section names are ship catalogue references.
`StorageCapacity` limits occupied space aboard; `StagingCapacity` limits space
used by boats preparing or ready to launch. Both use abstract space units.
Craft entries use `vessels/unit=count,space` or `land_units/amphibs_unit=count,space`.
A count alone defaults to one space
per craft. Counts range from 1 to 128, with no more than 128 craft in total.
Each craft must fit the staging capacity, and the starting complement must fit
storage. Omitted storage capacity defaults to the starting complement's total
space. Invalid configuration disables the mod with
an error in the game log. Missing catalogue units display as unavailable.

Tarawa has 20 SUs of storage and 8 SUs of staging. Its four assault boats cost
1 SU each; four amphibious AAV7s cost 2 SUs each; the testing Pegasus costs 8 SUs. Pegasus therefore occupies the
whole preparation area. These values are testing weights, not measured volumes.
The stock asset is an inflatable assault boat, not explicitly a RHIB. The
complement is a gameplay default, not a historical manifest. AAV7s require Amphibs
to be enabled; they remain unavailable without it. They use the ground-unit loader
and share preparation, recovery and deployment save tracking with boats.
Launches use its US variant and Commandos loadout, with SEALs aboard.
Tarawa also carries one deployable Pegasus for testing. Pegasus is not a wet-deck
parent. This extra craft does not represent a historical complement.

## Current scope

Choose a type and quantity. READY queues preparation and holds the boat ready.
LAUNCH queues preparation and launches automatically when ready. It also switches
existing preparation tasks to automatic launch. Abort returns a boat to stowage.
`PreparationSeconds` defaults to 90 simulation seconds per boat. Staging spaces
and `Crews`, default 1, limit concurrent preparation. Each preparing boat needs
one crew; additional boats wait for crew or deck space. Prepared boats
occupy their space until launched or aborted. Pausing stops preparation, and time
compression accelerates it. Saves retain progress and automatic-launch flags.
Blocked automatic launches remain ready for a manual retry and display the reason.
Launching requires ship speed at or below 5 knots and clear water astern.
Craft spawn directly astern with ten metres of hull clearance, facing away from
the parent. An obstructed launch point blocks launch rather than moving it farther away.
Each boat becomes a player-controlled vessel in the parent's taskforce.

Recover or Recover All replaces the boats' orders with a relative waypoint
astern of their ship. Recovery completes within 0.13 nautical miles, with parent
speed at or below 5 knots and boat speed at or below 8 knots. Abort cancels the
recovery order. Recovered boats return to stowage without a scored loss.
Destroyed boats do not replenish inventory. Tactical saves retain aboard counts,
staged counts and links from deployed boats to their parent ship.

Flooding, animations and AI-operated parent ships are not
implemented. Recovered boats return as inventory counts, not individual damage
or fuel records.
The existing flight deck remains unchanged. No new art is required.

## Build

```powershell
dotnet build src/WetDeckControls/WetDeckControls.csproj -p:DeployToGame=false
dotnet build src/WetDeckControls/WetDeckControls.csproj
```

Builds deploy by default into `Sea Power_Data/StreamingAssets/WetDeckControls`.
Generate the square Workshop icon with `./scripts/create-mod-icons.ps1 -Mod wet-deck`.
The internal mod identifier, deployment folder and `wet-decks.ini` filename remain
unchanged for existing installations and saves.
The build includes `thumbnail.jpg` when present.
Close the game before deployment. Deployment preserves an existing
`wet-decks.ini`; edit that copy and restart to change the complement.
Enable the mod through the game mod manager and restart.

Implementation lives in `WetDeckCatalog.cs` for configuration and inventory,
`WetDeckOperations.cs` for spawning, recovery and save hooks, and
`WetDeckController.cs` for the Unit Status tab and its content container.
Native button and table construction comes from the linked source in
[`SeaPower.SharedUi`](../../src/SeaPower.SharedUi/README.md), compiled into this DLL.
