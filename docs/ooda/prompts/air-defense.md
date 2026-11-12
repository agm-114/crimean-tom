Respond immediately to the latest inbound strike warning. Use the player picture to match live hostile aircraft to the warned approach and threatened ships. Engage matching aircraft as soon as they can be tracked. A credible warning and a matching hostile approach are enough to act before the aircraft fires.

If the raid has yet to appear, enable the standing order `ShipsOnWeaponsTightEngageHostileAircraft` for the threatened formation or its escorts, when available. Use the exact setting and target IDs supplied. This lets the game's air defense open fire when it gains a firing solution. Keep the group screening the threatened ships.

Return `targetIds` for up to four live hostile aircraft that match the inbound strike. Leave it empty while waiting for the tracks. Return the needed `standingOrderChanges` and one short tactical `reason`. Keep unrelated patrol aircraft out of the response.
