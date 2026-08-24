# Local tactical sync POC evidence, 23 August 2026

## Scope

This evidence covers one real Sea Power `0.8.2-23444+s358` process, one local
companion, and one isolated Campaign ServiceHost. It qualifies the narrow owned
surface synchronization path. It does not qualify a complete tactical battle,
a campaign-owned hostile loss, broader entity families, or two-container live
migration.

## Broad integration proof

`pnpm test:protocol` passed all 12 protocol stories after the POC changes. The
cross-component story now proves that:

- a changed owned-surface checkpoint produces a durable `StateSync` tactical
  proposal and updates canonical kinematics, resources, and damage;
- resolving past the old fixed tactical deadline returns no fallback while the
  owning heartbeat is live;
- the active tactical entity remains frozen while an unrelated campaign entity
  advances; and
- after the companion stops, the same assignment resolves through the existing
  120-quanta cold Operational fallback.

`pnpm test:campaign` also passed all 10 campaign integration stories. A Release
solution build completed with zero warnings and zero errors.

## Live runtime proof

The bridge was rebuilt/deployed, Sea Power was launched through Steam, and the
fresh `Player.log` marker identified the deployed Runtime Bridge DLL. Typed
Runtime Bridge commands loaded:

`scenario:missions/user_missions/crimean tom - tactical worker container`

The scheduler assigned bootstrap entity
`2589e340507d1ecdfc59172b427377fd`. Sea Power reported one active owned surface
record with the matching assignment and authority epoch. The service reported
the assignment `Active` and `LeaseLive: true`.

The assignment became active near campaign quanta 320. During unpaused tactical
play it remained active beyond quanta 1,220, well past the former 120-quanta
deadline. During that period, an unrelated entity reached revision 130. Sea Power reached
67.69 simulation seconds and remained unpaused.

An ordered movement update committed at canonical revision 5. The next periodic
unpaused readback committed revision 6 with Sea Power's realized coordinate
(`44.180002`, `28.649995`) rather than only the rounded commanded coordinate.
This is consistent with, and supplements, the protocol story's exact
`StateSync` frame assertion.

A second ordered update was issued while Sea Power was already running. The
companion paused only for the fenced lifecycle mutation, restored the prior
state, and committed heading `100000` millidegrees and telegraph `1`. The final
UI inspection still reported `isPaused: false`; the assignment remained Active
with a live lease while campaign time continued.

## Remaining boundaries

- State readback is two-second polling and commits at the current service head;
  it does not preserve a distinct tactical capture-time timeline.
- Only bridge-owned surface vessels publish this POC state.
- Magazine, sensor, formation, hosted-aircraft, and broader entity-family state
  remains incomplete or explicitly unsupported.
- A ServiceHost restart grace window and a long human-session soak are not yet
  qualified.
- The operator/player flow is still script-driven and does not yet prove a
  campaign-owned hostile's tactical fate returning to the journal.
