# Local friendly render-proxy POC — 2026-08-23

## Scope

This proof used Sea Power `0.8.2-23444+s358`, the local ServiceHost, companion,
and Runtime Bridge on campaign `ffd5483045f14f0b9d872c09165c4f9a`. It tested
the presentation-only path for friendly campaign ships alongside tactical
ownership; it did not test remote trust boundaries or non-Knox unit families.

## Observed result

- One campaign ship was an active Primary and appeared in the owned-resource
  registry. Six other deployed same-coalition Knox ships appeared as render
  proxies: Miller, Kirk, Cook, Ouellet, Ainsworth, and Reasoner.
- Every proxy had the campaign latitude and longitude to six decimal places,
  zero native speed, zero proxy contacts, disabled sensors and collisions, and
  no owned-resource or tactical-coupling entry.
- With Sea Power unpaused for ten seconds, campaign time advanced from 6370 to
  6470 quanta while all six proxy speeds remained zero. Sea Power simulation
  time also advanced.
- A typed camera-focus command selected Ainsworth and showed the Knox model at
  `0.0 kts`, confirming that the retained native presentation object was visible.
- Scheduling Ainsworth into the active tactical session produced two live
  Primary assignments and two owned entities. The proxy set fell from six to
  five, Ainsworth existed exactly once as an owned entity, and the total number
  of campaign-tagged ships remained seven.

## Qualification

This proves the narrow local POC invariant: friendly campaign ships can be
visible without being tactically simulated, and promotion replaces the proxy
without duplication. The current companion derives friendly visibility from
the authenticated full diagnostic campaign view and supports only the exact
`usn_ff_knox_84` / `Variant43` / `Default` materialization profile. Coalition
contact projection, additional unit families, lifecycle persistence, and a
finished player flow remain follow-on work.
