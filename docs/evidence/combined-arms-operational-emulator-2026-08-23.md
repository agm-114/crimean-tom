# Combined-arms Operational emulator evidence — 23 August 2026

> Historical snapshot: this proof qualifies the earlier 905-entity seed. The
> current full-catalog seed contains 4,096 entities; its inventory and checks
> are recorded in the full-catalog expansion evidence.

## Claim

The deterministic Operational analogue now represents fixed-wing aircraft,
helicopters, surface/subsurface units, logistics, physical ground units,
installations, and campaign-scale land abstractions in one campaign state.
Broad kernel coverage proves representative surface-to-installation,
air-to-ground, helicopter-to-installation, low-end GBAD-to-helicopter, and
ballistic-launcher-to-airbase exchanges. Formation abstractions do not perform
detailed ground combat against their projected physical target objects.

The 905-entity default seed contains 88 ground entities, 174 installations, 21
rotary-wing entities, and 26 Operational land abstractions. Each abstraction
projects two armor targets and one low-end SHORAD target. Seventy-three strategic
SAM, early-warning radar, and ballistic entities use the resident policy: they
request Tactical hosting without player interest and remain Operational if no
compatible worker is available.

## Local verification

| Check | Result |
| --- | --- |
| Deterministic campaign kernel integration suite | 11/11 passed |
| Combined-arms exchange story | Passed within the kernel suite |
| Full ServiceHost protocol/bootstrap story | Covers exact 905-entity/domain counts and resident offline fallback |

The deterministic algorithm version is 11. Its golden long-horizon and migrated
state hashes were regenerated only after the new domain behavior passed the
same broad replay stories.

## Deliberate limitations

- This is Operational-emulator proof, not live Sea Power materialization proof.
  Ground, installation, and rotary-wing runtime adapters still need to be added.
- The installed catalog has no period ballistic TEL object. Four NATO and six
  Warsaw Pact launchers use conspicuously named SAM-launcher visual substitutes;
  their ballistic ranges and target behavior live in campaign capabilities.
- Armor projections intentionally carry no ground-combat weapon capability.
  They are targets for aircraft, helicopters, ships, and missiles while the
  parent abstraction remains responsible for formation-scale behavior.
- The previous 801-entity performance capture is historical. A fresh vanilla
  versus 905-entity campaign run is still required before making a performance
  claim about this revision.
