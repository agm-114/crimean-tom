# Regenerate the Deck Tweaks Workshop screenshots

Deploy the Deck Tweaks build being published. Enable AnchorChain, Deck Tweaks,
and the Game Bridge, and set **Flight Deck Timings** to **Realistic**.

1. From the repository root, start Sea Power and load the [carrier rearm scenario](scenarios/carrier-rearm.ini).
   Close any startup dialog before running the load command.

   ```powershell
   seapower-cli launch --confirm --require-pattern 'Deck Tweaks'
   seapower-cli load-scenario --scenario docs/deck-tweaks/scenarios/carrier-rearm.ini --confirm
   ```

2. With the mission paused, open Nimitz's **Unit Status → Flight Deck**.
   Select **A-7E**, **VA-82 Marauders**, **2** assigned aircraft, and
   **SEAD Long Range**. Click **Ready**. Both Corsairs have some fuel,
   20 mm ammunition, chaff, and a Sidewinder. They still carry Walleye IIs
   and data link pods from their previous loadout.
3. Advance one simulation minute, then leave the mission paused. Both aircraft
   should be refueling, with the same work remaining. The details should
   group them under **Aircraft 1 & 2**, with Shrikes, Rockeyes and drop
   tanks to load and the precision strike stores to unload.

   ```powershell
   seapower-cli advance-sim --minutes 1 --speed 5
   ```

4. Use `seapower-cli screenshot` to capture these four views at the same
   paused time. Move the window if the tooltip or menu is clipped.

   | View | Crop and filename |
   | --- | --- |
   | Flight Deck with timing collapsed | Unit Status window, `deck-tweaks-flight-deck.png` |
   | Hover over State or the ETA | Timing tooltip, `deck-tweaks-rearm-tooltip.png` |
   | Click the caret beside the ETA | Flight Operations and the expanded timing panel, `deck-tweaks-expanded-timing.png` |
   | Collapse timing and open the batch's Loadout dropdown | Flight Operations and the open menu, `deck-tweaks-loadout-dropdown.png` |

Crop the original PNGs before resizing. Export each crop at 200% as a PNG and
keep the files outside the packaged mod. Reuse the filenames to replace the
existing gallery images. For Deck Tweaks alone, pass the gallery directory as
`--preview-dir` to [`seapower-cli workshop-update`](../seapower-cli/README.md#updating-an-existing-workshop-mod).
For a bundle release, pass it as `-DeckTweaksPreviews` to the
[Crimean Tom release script](../crimean-tom/workshop-bundle.md).
