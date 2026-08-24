# Phase 2 exit-gate matrix

Pinned game build: `0.8.2-23444+s358`.

This matrix closes Phase 2 for the first and only supported entity family,
`usn_ff_knox_84` / `Variant43` / `Default`. It does not promote formation,
aircraft, submarine, land, active-weapon, or currently unsupported surface
state families.

| P2 exit condition | Working evidence | Disposition |
| --- | --- | --- |
| Materialize → inspect → checkpoint → remove from active simulation → rematerialize with stable identity and supported fields within tolerance | Live create/readback, exact checkpoint restore, native suspension, scene-reload reconstruction, and [surface tolerances](phase-2-surface-field-tolerances.md) | Pass. "Remove" means retained native suspension, not memory reclamation. |
| Duplicate create/update/delete commands are idempotent | Create, update, suspend, enable, restore, and detached-recovery release all returned exact replay results without duplicate objects | pass |
| Stale revision/epoch and unsupported catalog fail closed | HTTP 422 `stale-revision`, HTTP 422 `stale-authority-epoch`, companion rejection of a Ticonderoga tuple, and plugin rejection of an unowned scoped ID | pass |
| Injected failure leaves source state or one recoverable owned Unity object | Eleven live injection boundaries: materialize 3, update 3, suspension 2, restore 3. Update/suspension/restore retained their source revision and exact canonical hash; materialize failures produced one tagged suspended recovery object each | pass |
| Cleanup cannot affect scenario/user objects | Failed construction left all 18 scenario units active and zero fault objects in the world reader. Registry release required the exact owned record, assignment, epoch, revision, detached Unity state, recovery flag, and absence of a checkpoint; a foreign release failed | pass |
| Unsupported loss is explicit and blocks unsafe promotion | Every checkpoint/materialization reports damage, fuel, magazine, sensor, formation, and hosted-aircraft application as unsupported. The current `Variant43` Knox exposes no positive native fuel capacity, so its captured fraction is null and the guarded write is not promoted. Runtime authority remains false. | pass for the narrow family; expansion remains separately gated |
| Suspension and deeper removal meet separate safety/resource claims | Native suspension removes active registrations and ECS updates while the resource snapshot continues counting retained objects. Native destruction/pooling inspection rejected deeper quiet removal, and the architecture makes no memory-reclamation claim | pass under the documented narrowed lifecycle |
| Synchronous cost and tolerances are published for scheduling | Observed fresh constructors/rematerializers took 102,216–159,178 µs in the live fixtures; update and retained lifecycle operations were sub-millisecond to low-millisecond. The resource endpoint reports active, retained-suspended, detached, recovery-required, and retained-constructed counts with `memoryReclamationSupported=false` | pass |

## Failure and cleanup run

Materialize failures injected after the ownership tag, supported-state apply,
and registry add returned structured `mutation-injected-failure` responses.
They produced game IDs 355, 445, and 535, zero active world matches, and three
`recovery-required-suspended` resource rows while the scenario remained at 18
units.

On a normal owned unit, three update failures and two suspension failures left
revision 1 and canonical world SHA-256
`89494f3674f47bbf431a063baea63f5137d6f39167489f13b4c50776da135ee1`
unchanged. Three restore failures left revision 3 in `suspended-retained` state;
the succeeding restore returned the same canonical hash and original supported
state at revision 4.

A final isolated cleanup run injected a materialization failure, observed one
retained recovery object and no active owned match, then exited through typed
UI automation. Unity scene unload changed the resource row to
`detached-after-scene-loss` with retained-constructed count zero. The fenced
release removed only that registry row, an exact duplicate returned
`idempotentReplay=true`, a foreign release failed, and all resource counts
returned to zero.

## Gate result

Exit gate P2 is complete for the narrow Knox surface contract. Phase 3 may use
this adapter as a fenced projection target, but unsupported fields and all new
entity families remain promotion blockers until they pass their own gates.
