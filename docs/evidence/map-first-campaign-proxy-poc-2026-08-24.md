# Map-first campaign proxy POC — 24 August 2026

This is local runtime evidence against Sea Power `0.8.2-23444+s358` and campaign
`20260824000000000000000000000002`. The campaign was advancing unattended, so
entity totals are observations at a commit, not fixture constants.

## Result

- The first reconciliation exposed 1,761 map records: 1,235 non-destroyed NATO
  friendlies and 526 coalition-filtered hostile intelligence tracks.
- A later fresh-process sample exposed 869 records: 311 surviving friendlies and
  558 contacts. Zero physical proxy models existed before selection.
- Selecting `unit:crimean-tom:009f65bfb66f55fd0c6d55f460da1eb1`
  (US Atlantic attack submarine 005, `usn_ssn_los_angeles`) relocated the
  tactical window to 39.4641 N, 70.6871 W and produced exactly one selected
  presentation-only model. It remained outside native simulation and campaign
  authority: `hasNativeSimulationObject=false`, `isSimulated=false`, no game ID.
- After relocation the game reported 60.000 FPS, engine/game fixed timestep
  0.0166667 seconds, internal time dilation 1, and no unit beyond loaded terrain.

## Memory failure found and fixed

Before terrain-aware selection, focusing the same off-window record created its
model thousands of kilometres from the active terrain origin. Sea Power reached
about 12.2 GiB working set and 19.5 GiB private memory. The repaired path first
relocates the tactical terrain window. On a fresh process, the map-only phase was
4.95 / 11.39 GiB working/private; one selected physical proxy was 5.19 / 11.60
GiB. This is a short diagnostic pair, not a 4K soak budget.

## Open player-facing gaps

- The authored GIUK mission still loads static Pharris and Honolulu objects;
  they are not bound to current campaign task-force membership and both named
  campaign entities were already destroyed in this advancing save.
- Sea Power's Tactical Display is a local viewport. The map records exist in its
  drawing layer, but a discoverable theater/world strategic-map surface,
  viewport navigation, clustering, labels, and uncertainty-radius glyphs are
  still required.
- Generic presentation loading is live-proven for this submarine only. Installed
  catalog variant/loadout resolution and visual qualification remain incomplete.

Broad verification after the change: campaign integration 11/11 and protocol
integration 12/12 passed.
