# Local visual-only render-proxy POC — 23 August 2026

## Scope

This record qualifies the preferred presentation-only representation and the
local promotion/demotion boundary on Sea Power `0.8.2-23444+s358`. It extends,
but does not replace, the earlier native-fenced proxy evidence in
`local-friendly-render-proxy-poc-2026-08-23.md`.

The proof used campaign `2026082300004000a000000000000001`, the tactical worker
container mission, the local ServiceHost, and one real Sea Power process. CI was
not used because repository compute minutes were unavailable.

## Observed live results

- With no active tactical assignment, `GET /game/render-proxies` reported seven
  deployed friendly Knox proxies under provider `visual-only-preferred`.
- Every proxy reported `gameUniqueId: null`, `isSimulated: false`,
  `representationKind: visual-model-and-map-annotation`,
  `hasNativeSimulationObject: false`, `worldModelVisible: true`, and
  `mapMarkerVisible: true`.
- The implementation used `ObjectBaseLoader.loadObjectFromInternalResources`
  only. It did not add a `Vessel`, systems, sensors, native physics, or an
  `ObjectsManager` entry. All behaviours and colliders in the presentation
  hierarchy were disabled, any rigidbody was made kinematic/non-colliding, and
  only LOD0 remained active.
- Promoting entity `7ccc5ae1b3abe1d623a8653822ce4c7d` created one active owned
  native surface record (`gameUniqueId: 92`) and hid its matching visual proxy.
  The other friendly proxies remained visible. Sea Power returned to unpaused
  1× play after preparation.
- A released entity with a retained suspended native record was represented
  again by its world model and map annotation. The record remained suspended,
  so the proxy did not create a second simulation authority.
- Reconnecting with three active native records whose campaign assignments were
  already released caused the companion to contain them at a pause safe point:
  runtime resources changed from three active to zero active / three
  suspended-retained, and Sea Power returned to unpaused 1× play.
- During a six-second concurrent-clock sample, campaign time advanced from 4620
  to 4680 quanta and tactical simulation time advanced from 86.573 to 92.673
  seconds. During a later eight-second active-assignment sample, campaign time
  advanced from 10820 to 10900 quanta, tactical time advanced from 191.113 to
  199.244 seconds, and the assignment remained `Active` and `leaseLive: true`.

## Failure found and corrected

The first live demotion attempt called `set-owned-surface-suspension` while the
tactical mission was running. The bridge rejected it with
`mutation-precondition-failed`, as designed. Authority fencing now runs through
a bounded pause/restore safe point. Reconnect reconciliation also compares
runtime-owned resources with the campaign's current assignment projection and
suspends stale active records. Campaign assignment IDs are normalized from the
HTTP view's bare GUID form to the runtime's `assignment:<guid>` form before that
comparison.

## Qualification boundary

The screenshot proved the tactical mission remained playable, but did not show
the proxy fleet in the current viewport: the worker-container mission and camera
were near Iceland while the proof campaign fleet was in the Black Sea. Runtime
state proved that both model roots and map drawing objects were registered at
campaign coordinates. A theater-aligned worker mission or typed proxy-focus/map
navigation command is still required for direct on-screen visual qualification.

This remains a trusted-local, one-profile POC. It does not qualify hostile or
uncertain-contact presentation, broader catalog families, remote-player
filtering, long-session soak, or a campaign-owned hostile tactical result.
