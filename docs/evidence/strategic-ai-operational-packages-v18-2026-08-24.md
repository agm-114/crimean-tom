# Strategic AI operational packages, algorithm v18

## Scope

Algorithm v18 turns groups that share a strategic objective into explicit,
durable Operational packages. A package records its coalition, objective,
region, assembly time, shared execution time, expiry, and deterministic member
roles. The implemented roles are strike, air cover, surface escort,
anti-submarine, land attack, generic escort, sustainment, and ground support.

The allocator now scores missing package roles as well as visible target classes.
This lets a strike pull air cover, screens, and logistics toward the same
objective instead of merely producing unrelated orders in the same region.
Package roles drive the domain order controllers: air cover becomes CAP,
anti-submarine becomes ASW, screens become escort, sustainment becomes resupply,
and ground support becomes reinforcement. Maritime/ground elements begin at
assembly, rotary-wing elements receive a longer lead, and fixed-wing elements a
shorter lead into the shared execution boundary.

Packages are canonical state, included in simulation deltas, snapshot hashing,
SQLite recovery, validation, and the diagnostic HTTP view. Fixed installations
and withdrawing groups cannot be package members; a group cannot belong to two
packages.

## Full 4K execution

The run used campaign ID `20260824000000000000000000000002`, seed
`19890911`, the 55-region/81-base/4,096-entity/919-group opening state, and forty
separate ten-quanta advances with unattended wall-clock simulation disabled.

| At campaign time 400 | Result |
| --- | ---: |
| Operational packages | 63 |
| Unique packaged groups | 418 |
| Package members, minimum / average / maximum | 2 / 6.6 / 13 |
| Packages with phased starts | 61 |
| Packages with at least three roles | 53 |
| Explicit strike packages | 4 |
| Strike packages with air cover | 4 |
| Active referenced orders | 747 |
| Fixed groups with active movement orders | 0 |
| Strategic objectives | 123 |
| Recent Operational outcomes | 490 |

The 418 member roles were 117 air cover, 60 anti-submarine, 99 escort, 61
ground support, 28 land attack, 4 strike, 16 surface escort, and 33
sustainment. The final state contained 29 defense packages, 5 interdiction
packages, and 29 force-projection packages; their respective membership was
192, 56, and 170 groups.

Using the same coalition-intelligence/weapon-capability metric as algorithm v17,
169 of 177 evaluated threat responses were matched (95.5%); eight were
mismatched and only one was armed with the wrong capability. This preserves the
v17 capability result while adding package composition. The evaluated
population is allocation-dependent, so absolute mismatches are reported with
the percentage.

Clean bootstrap took 8,429.2 ms. Forty advances averaged 622.6 ms, peaked at
1,631.6 ms, and ended at 498.3 ms. The average is 4.0% above the matched v17
598.6 ms result. After service stop, journal recovery at commit 5,932/time 400
took 3,411.5 ms (4,480.2 ms through the HTTP view), restored all 63 packages and
418 unique members, and reproduced canonical hash
`ee1f2633170001104404408889cd46702e516270d4491648b3f7dbd11575d09c`.

## Scale defect found

The first 4K package run failed at a non-director tick when a member crossed its
damage/recovery threshold. Recovery latching correctly marked it withdrawing,
but its package was only rebuilt on the slower director cadence, violating the
canonical no-withdrawing-members invariant. Package membership is now pruned on
every Operational tick after recovery latches refresh; packages below two valid
members are removed. The broad integration story also damages a package fighter
between director passes and proves that its package is reduced without breaking
replay or snapshot round-trip.

## Limits

The shared execution boundary is phase coordination, not a precise estimated
time of arrival. Route length, platform cruise performance, tanker offload,
magazine demand, runway launch/recovery slots, and threat-corridor deconfliction
do not yet feed package timing. Packages do not yet construct explicit SEAD or
electronic-attack roles from sensor/emitter relationships. Those are the next
Operational-planning depth gates before claiming human-quality strike planning.
