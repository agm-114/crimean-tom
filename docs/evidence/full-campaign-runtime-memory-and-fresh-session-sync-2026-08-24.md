# Full-campaign runtime memory and fresh-session sync proof

## Scope

This record separates Sea Power's native mission-load cost from Crimean Tom's
campaign and render-proxy cost, and qualifies a brand-new player session against
the complete 4,096-entity WW3++ seed. Measurements were taken locally on 24
August 2026 against Sea Power `0.8.2-23444+s358`. Process private/working-set
figures came from Windows process counters; Unity allocations and asset counts
came from the typed Runtime Bridge memory diagnostic.

## Memory isolation

Each mission measurement used a fresh Sea Power process.

| State | Process private | Working set | Unity allocated / reserved | Relevant runtime state |
| --- | ---: | ---: | ---: | --- |
| Main menu | 2.363 GiB | 1.938 GiB | 1.004 / 1.522 GiB | No mission or proxies |
| Crimean Tom GIUK proving mission, campaign disconnected | 11.72 GiB | 5.06 GiB | 3.370 / 4.156 GiB | 4 native units, 50 terrain chunks, zero proxies |
| Stock `Intro/01 Red Sea Breakout 1988` | 12.861 GiB | 5.654 GiB | 3.481 / 4.266 GiB | 19 native objects, same 50 terrain chunks |

The large mission-load allocation is therefore not created by the Crimean Tom
campaign emulator or proxy system. Both stock and proving missions load the
same dominant native terrain/render assets: 1,158 textures totalling about
3.370 GiB, including unnamed texture arrays/render textures of roughly
179-358 MiB, 23 MiB splat maps, and a 51.84 MiB low-resolution terrain texture.
The stock mission used slightly more memory than the disconnected Crimean Tom
mission.

Connecting an already-advanced full campaign at head 9,330 added about 14.4 MiB
of process-private memory and 0.6 MiB of reported Unity allocation while
projecting 1,827 friendly records. It created no proxies because all twelve
eligible Knox entities in that durable campaign were already destroyed.

On a fresh full campaign, twelve presentation-only Knox proxies added about
154 MiB of process-private memory and 146 MiB of reported Unity allocation,
approximately 12 MiB per proxy. That cost is meaningful but bounded and is
separate from the roughly 12 GiB native mission baseline. The runtime memory
endpoint is an explicit diagnostic and should not be polled continuously because
enumerating Unity assets itself allocates temporary managed objects.

## Fresh 4K campaign sync

The clean proving campaign used ID `20260824000000000000000000000001` and
bootstrapped in 9.004 seconds:

- 55 regions, 81 bases, 4,096 entities, 919 groups, and 741 orders;
- 1,329 maritime, 491 fixed-wing, 149 rotary-wing, and 1,728 land entities;
- 4,006 tactically eligible entities and 5,892 initial journal commits;
- 1,827 NATO-friendly projection records and twelve deployed Knox proxy
  candidates.

A brand-new companion advertised the NATO coalition ID in its authenticated
hello and heartbeat. Before any assignment history existed, the service returned
the correct coalition projection at head 5,892 and the runtime created all
twelve visible proxies. The world remained four native units/six native objects;
every proxy reported `isSimulated=false`, no native simulation object, and
`visual-model-and-map-annotation` representation.

Two defects were found and corrected during this proof:

1. Replacing the durable companion cursor could fail transiently on Windows
   with `UnauthorizedAccessException` during the 5,892-event replay. Atomic
   replacement now retries a bounded 25 times without acknowledging an
   unpersisted cursor.
2. A new player session had no coalition until assignment history happened to
   infer one. The authenticated companion contract now carries an explicit,
   canonical player coalition in hello and heartbeats; the service validates it,
   rejects operational-worker namespace mixing, and rejects identity changes.

After resuming from the safely retained partial cursor, the companion reached
head 5,892 without another replacement failure. Targeted campaign integration
tests (11/11) and Operational worker integration tests (2/2) passed after the
changes. The 51-second broad companion story also passed and asserts the
coalition-filtered projection before creating an assignment. Its first run
correctly exposed a stale fixture assumption: selecting one safe member is now
allowed to stream as a singleton. The batch portion now selects both members,
preserving its intended proof of atomic group activation and later hard-coupling
retention.

## Interpretation

The evidence supports the intended architecture: thousands of campaign entities
can remain external while a small bounded presentation set exists in Unity. It
does not support treating twelve Knox proxies as general catalog coverage or as
a final memory budget. Broader, cheaper proxy profiles and a long-running memory
soak remain necessary before the 4K campaign is a shipping workload.
