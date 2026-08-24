# Strategic AI fixed-defense qualification, algorithm v16

## Defect found

A read-only audit of the algorithm-v15 4,096-entity proof at campaign time 200
found 38 airbase-defense groups containing both immobile installations and
logistics sites. Thirty-six had active Patrol, Intercept, or Escort orders.
Operational installations have a deliberate zero movement rate, so the whole
group could never satisfy the arrival predicate. Those permanent orders also
prevented the strategic director from retasking the group.

This was an operational sub-AI defect, not merely weak tactics. A runway, radar,
GBAD layers, fuel farm, and ammunition site form one local defense complex. The
AI must not detach movable-looking support records from that authority island or
try to move the fixed members.

## Algorithm-v16 rule

The coalition projection now records whether a group contains any installation.
Such a group can commit only to a `Defend` objective in its current region and
never receives a movement order. The canonical command boundary also rejects
anything except a same-region `Hold`, so a player or tool cannot recreate the
impossible state. Its sensors and weapons continue to engage in place through
the Operational resolver. A broad deterministic integration
fixture combines an installation and a logistics member, then proves that both
coordinates remain fixed, no active order is created, and the durable objective
is local defense.

Because this changes deterministic campaign behavior, the algorithm version is
16. Existing v15 saves still fail closed; save-contract upgrade remains an
explicit product gap.

## Full 4K result

The clean run used campaign ID `20260824000000000000000000000002`, the complete
55-region/81-base/4,096-entity/919-group seed, and twenty separate ten-quanta
advances.

| Boundary | Observation |
| --- | ---: |
| Bootstrap | 8,477.1 ms |
| Opening orders | 741 |
| Final time / commit | 200 / 5,912 |
| Coalition strategies / objectives / commitments | 2 / 123 / 919 |
| Active referenced mobile-group orders | 769 |
| Fixed groups | 145 |
| Mixed installation/logistics groups | 38 |
| Fixed groups with movement orders | 0 |
| Fixed groups with non-defense objectives | 0 |
| Fixed groups with remote objectives | 0 |
| Recent Operational outcomes | 484 |
| Average / maximum / final ten-quanta advance | 578.2 / 1,373.1 / 527.9 ms |
| Live service managed / working memory | 38.6 / 172.1 MiB |

After a clean service stop, the persistence probe recovered the same 4,096
entities and 919 groups at commit 5,912 in 1,336.5 ms. It found 769 active
referenced orders, zero completed or unreferenced retained orders, algorithm 16,
and canonical hash
`ca327814b2fb4f3eab00ab0dd39d645f36b1121401a23ef95040e18c2b987b3d`.

The 11-test campaign integration suite passed in six seconds. This proves the
fixed-defense invariant and deterministic recovery; it does not make the AI a
human-equivalent opponent. Capability-matched force packages, adaptive replans,
deception, recurring air operations, and campaign victory logic remain open.
