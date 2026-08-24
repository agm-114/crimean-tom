# Player-selected streaming and bounded-sync evidence, 24 August 2026

## Scope and disposition

This record qualifies a local proof-of-concept path against Sea Power
`0.8.2-23444+s358` and the full 4,096-entity WW3++ campaign. It covers a
player-selected installation, automatic camera focus, worker-capacity use,
retained installation re-entry, campaign catch-up, and companion disk growth.
It does not claim a complete player UI, general catalog materialization, or a
long-duration soak.

Disposition: **the selected-unit local-sync POC works, with remaining latency,
portable-state, and product-UI gaps.**

## Live player path

- The running mission was
  `Fulda Gap Gone Hot - GIUK Dynamic Campaign Window`.
- Campaign entity `7160779d3f99d33f580a1077acc43652`, NATO reinforcement
  airfield 001 (`nato_small_airbase`), was submitted as player-controlled.
- The full-seed scheduler promoted it from Operational to Tactical. At the
  decision point the worker advertised capacity 8, reserve 1, and four usable
  slots after resident usage.
- Sea Power selected
  `campaign-unit:unit:crimean-tom:7160779d3f99d33f580a1077acc43652`
  without a manual focus request. The companion logged successful automatic
  focus after one attempt and durably wrote
  `assignment_1dee2e4db75109e7d690212f69b7f8ff.2.player-focus-completed`.
- The captured player view identified the selected object as `Small airbase`
  at 1,074 ft. A Crimean Tom message modal was visible and was dismissed through
  typed runtime automation; modal presentation still needs product UX work.
- During the observation Sea Power reported 60.004 FPS, a 16.667 ms engine and
  game fixed step, time compression 1, and internal time dilation 1.0.

The initial-focus defect was that `FocusAfterActivation` was consumed only on
an activation grant. A first Primary assignment has no activation grant. The
companion now observes the initial prepared batch, waits for a short quiet
period, retries focus, and records completion so reconnects do not repeatedly
move the player's camera.

## Retained ground and installation re-entry

Three resident strategic land objects had previously failed reassignment with
`stale-revision` because the land adapter lacked the retained-object rebind path
already present for surface objects. The land adapter now permits an exact
entity/profile/task-force match to rebind a healthy suspended retained object
to a different assignment at a higher authority epoch.

The live worker subsequently held both NATO ballistic-missile TEL visual
proxies as active epoch-5 Primary assignments. Runtime-owned-resource state
reported their existing Unity objects active at revision 1, while older
epochs were Released in campaign state. No replacement object was required and
the earlier stale-revision rejection did not recur.

## Bounded projection and catch-up

The companion no longer downloads the full 4,096-entity privileged diagnostic
view every two seconds. It reads a tactical-worker projection containing local
assignments plus coalition-friendly visual-proxy fields. The projection now
uses the coordinator's immutable last-committed cache after cold recovery rather
than rechecking SQLite on every poll, and retains coalition identity from the
worker's assignment history when no assignment is currently live. Friendly
proxies therefore do not disappear between tactical handoffs. With 1,827
friendly records serialized, 30 requests across active simulation commits
measured 33.79 ms median, 50.78 ms p95, and 61.41 ms maximum; the prior path had
shown roughly 4–5 second stalls.

Historical full-state events exceeded gRPC's default 4 MiB server send limit,
which made catch-up reconnect once per oversized event. The server now uses the
same 64 MiB service-frame ceiling already enforced by both clients. After a
service-only restart and multiple snapshot boundaries, the companion cursor
equaled the campaign head at 7,773 with lag 0 and continued advancing.

This larger ceiling is a continuity guard, not the final bandwidth design.
Simulation events still describe every changed full-seed entity at the current
cadence and should become bounded deltas.

## Companion disk-growth correction

The old companion state store wrote every committed event JSON forever. In the
live run this reached 8,265 files and 1,715,859,748 bytes; the largest event was
2,197,017 bytes. Downstream recovery only needs proof that a specific tactical
migration member committed.

New state therefore writes only a small marker per destination assignment in a
`TacticalIslandMigratedEvent`, then atomically advances the cursor. Legacy full
JSON remains readable for compatibility but is never newly written. In a live
10-second check the cursor advanced by 11 commits while event-file count and
bytes both grew by zero. The obsolete local cache was verified to contain no
migration event, pending outbound frame, or activation grant; automated removal
was blocked by the execution environment, so that generated directory remains
safe to remove manually.

## Verification ledger

- Solution build: passed, 0 warnings and 0 errors after the service was stopped
  to release its apphost lock.
- Campaign integration suite: 11/11 passed, including the combined-arms
  physical ground/installation/rotary-wing Operational story.
- Protocol suite: cache and both Operational-worker tests passed. The final
  broad real-host typed-UI story was canceled after more than seven minutes
  without new output; do not treat the whole protocol suite as green from this
  run.
- Runtime bridge status: deployed/built hashes matched and the fresh-launch
  AnchorChain marker was present for Sea Power process 52204.

## Remaining gates

1. Replace repeated full 1,827-friendly-record serialization with a versioned
   worker/player projection delta or conditional response.
2. Bound full-seed simulation event payloads rather than relying on the 64 MiB
   transport ceiling.
3. Add a player-facing selection/streaming progress surface and remove the
   generic message-modal artifact.
4. Qualify tactical fate return for a hostile and broaden portable state beyond
   the current surface plus allow-listed land/installation profiles.
5. Diagnose and bound the long real-host protocol story, then run restart and
   long-session soak tests.
