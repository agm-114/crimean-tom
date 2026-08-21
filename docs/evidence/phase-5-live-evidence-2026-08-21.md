# Phase 5 live scheduler evidence — 21 August 2026

## Disposition

The first representative Fulda Gap scheduler-to-Sea-Power lifecycle is working.
The service selected a complete representable interaction island, queued the
handoff to the connected companion, materialized a real Knox in Sea Power,
activated Tactical authority only after the initial checkpoint, drained it
after durable interest-loss hysteresis, and later re-promoted the retained
checkpointed object under a higher authority epoch.

This is a vertical lifecycle gate, not the complete Phase 5 capacity or fidelity
gate. Weapon/guidance/host coupling, multi-profile islands, tactical load limits,
and resolver calibration remain.

## Runtime identity

- Sea Power `0.8.2-23444+s358`, process 38168
- runtime fingerprint `92f00eeb425ad14914e646705b5068f0225898e6b929ccb2715b2e6115794df5`
- bridge assembly `679f4b476c15d5ce7eeaa4cd082d51cf9196a5994dd7d88aab48541159856371`
- catalog `01d4650c0f0ddeb6a106b3e2ded8c79d27ebbcad51319e56470f45d7fa3183ea`
- player task force `taskforce:Taskforce1`
- load cache: 1,532 hits, one miss after the changed bridge deployment

The game was launched through Steam and accepted a fresh AnchorChain health
marker. Typed UI automation loaded
`scenario:missions/intro/01 red sea breakout 1988` and continued the briefing;
no menu coordinate clicks were used.

## Representative campaign

Campaign `37772898357347cd9c9c6fd724037d11` is a fresh deterministic Fulda Gap
bootstrap with 12 regions, 15 entities, and 9 task forces. USS Pharris is a
singleton GIUK surface picket using the adapter's exact
`usn_ff_knox_84 / Variant13 / Default` profile. USS Providence belongs to the
existing North Sea force, preserving both entity and task-force counts while
preventing an unsupported submarine from making the demo island impossible.

The hosted monitor is:

`http://127.0.0.1:5081/monitor/?campaign=37772898357347cd9c9c6fd724037d11`

The host issues its own HttpOnly browser session, so opening that URL does not
require copying the service token into the browser.

## Lifecycle observations

The first live promotion reached campaign commit 63. USS Pharris changed from
Operational to Tactical only after assignment epoch 1 materialized in Sea Power
and published its initial content-addressed checkpoint. The runtime world placed
the campaign-stable identity inside the exact player task force.

Two identical healthy requests with no observation then produced:

1. `scheduler-hysteresis-hold`, leaving the active island Tactical;
2. `authority-quiescing`, followed by a final checkpoint and release.

At commit 68 the campaign entity was Operational and the epoch-1 assignment was
Released. Runtime resource diagnostics reported one `suspended-retained` object,
one checkpoint, zero active objects, and zero recovery-required objects.

The first attempt to promote that retained identity exposed a live-only defect:
the bridge treated any retained record as a duplicate materialization. The
runtime adapter now recognizes only a checkpointed, healthy, suspended
predecessor with an exact entity/profile/task-force match and a strictly higher
authority epoch. It rebinds and enables that object for the new assignment; a
failure restores suspension instead of leaving ambiguous authority.

After deploying the corrected bridge, a fresh promotion/drain/re-promotion
sequence ended at campaign commit 85. Assignment epoch 5 is Active, USS Pharris
is Tactical, and the bridge reports one active retained constructed object,
checkpoint present, and no detached or recovery-required state. The live game
record is game unique ID 398.

## Four-island tactical-capacity probe

A separate 4x Fulda campaign,
`160bc4f63fdd4d6f84fb2bd136c255ac`, contains 60 operational entities and 36
task forces. Its four independent USS Pharris pickets were observed together
with tactical capacity set to four. On the same game/catalog/bridge fingerprint:

| Operation | HTTP scheduler return | All four terminal state | Result |
| --- | ---: | ---: | --- |
| Initial materialize + checkpoint | 2,555.7 ms | 4,020.2 ms | Four Active/Tactical at commit 177 |
| Two-sample drain + final checkpoint | 336.3 ms | 2,011.0 ms | Four Released/Operational at commit 191 |
| Retained re-promotion + checkpoint | 337.9 ms | 1,318.4 ms | Four epoch-2 Active/Tactical at commit 200 |

The initial game unique IDs were `365`, `455`, `545`, and `635`; the same four
IDs remained after drain and re-promotion. Runtime diagnostics ended with four
active retained objects, four checkpoints, and zero detached or
recovery-required objects. This supports a local tactical capacity of four for
independent Knox islands on this host. It does not yet establish tick-rate or
combat-load headroom, and it must not be generalized to mixed unit profiles.

The live capacity monitor is:

`http://127.0.0.1:5081/monitor/?campaign=160bc4f63fdd4d6f84fb2bd136c255ac`

## Scene-aware worker refresh

The companion now publishes its current game session, compatibility
fingerprints, player task force, and materialization profiles in each campaign
heartbeat. Service session `8a6e6fd197bc4fce9586b9f0145c1f61` remained
unchanged while typed automation exited tactical play to debrief and loaded Red
Sea Breakout again. The service projection cleared the player task force at
debrief, then reacquired `taskforce:Taskforce1` and the
`usn_ff_knox_84 / Variant13 / Default` profile after mission load without a
companion restart.

After that transition, fresh 4x campaign
`739f13a58fde41ff9563bae754213672` promoted four independent USS Pharris
islands. The scheduler request returned in 2,623.8 ms and all four epoch-1
assignments were Active/Tactical at commit 177. Sea Power reported four active
owned objects (unique IDs `405`, `495`, `585`, and `675`) and zero recovery
debt. The hosted monitor is:

`http://127.0.0.1:5081/monitor/?campaign=739f13a58fde41ff9563bae754213672`

## Remaining gate

- Measure tactical tick throughput and main-thread costs under active combat;
  the four-island materialize/drain burst baseline is now recorded.
- Add safe profiles for more naval/air units before claiming mixed-island
  coverage.
- Extend interaction islands with active weapons, guidance, sensors, hosts, and
  plotting relationships, then test merge/split hysteresis live.
- Calibrate operational outcomes against repeatable Sea Power observation
  batches rather than treating this one mission as combat-model validation.
