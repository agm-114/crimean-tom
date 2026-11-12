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
   Select **F-14A**, **VF-41 Black Aces**, **2** assigned aircraft, and
   **Air To Air Long Range**. Click **Ready**. The scenario starts with two
   recovered fighters with different amounts of fuel, 20 mm ammunition, and
   chaff. One has an extra Phoenix to unload.
3. Advance to the point where one fighter is refueling and the other is
   unloading its extra Phoenix.

   ```powershell
   seapower-cli advance-sim --minutes 2
   seapower-cli speed 5
   seapower-cli wait-sim --seconds 22 --timeout 30
   seapower-cli pause
   ```

4. Hover over the batch's **State** cell in **Flight Operations**. Check that
   Aircraft 1 shows fuel, chaff, and gun ammunition and Aircraft 2 says
   **Unloading AIM-54A (Phoenix)**. Move the window if the tooltip is clipped.
5. With the tooltip open, run `seapower-cli screenshot`. Crop the same frame
   twice: the full Unit Status window including the tooltip
   (`deck-tweaks-flight-deck.png`), then just the tooltip
   (`deck-tweaks-rearm-tooltip.png`).

Keep the two PNGs in a folder outside the packaged mod. Run
`seapower-cli workshop-update --mod-dir <package> --preview-dir <screenshots>`
to review the upload. Add `--confirm` to submit it. Reuse the filenames to
replace the existing gallery images.
