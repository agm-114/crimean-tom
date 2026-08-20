# Phase 1 live evidence — 2026-08-20

Pinned game build: `0.8.2-23444+s358`.

## Catalog and compatibility

- Catalog SHA-256: `01d4650c0f0ddeb6a106b3e2ded8c79d27ebbcad51319e56470f45d7fa3183ea`.
- Mod-set SHA-256: `ee8a4019d038dbebf013ffa91ee35ff4d9bdebc7d8835bfdfabd3b480b3683bb`.
- Winning catalog files: 1,521.
- Normalized units: 548; ammunition: 184; sensors: 480.
- Active VFS override diagnostics: zero identical overrides and zero conflicting
  overrides. The route still records winner/shadowed source and hashes when an
  overlap exists.

## Canonical world and extraction budget

The Red Sea Breakout intro scenario was started and its briefing advanced using
typed bridge actions. A controlled experimental pause froze simulation time at
`2.25089955329895` seconds. Two independent reads 300 ms apart reported:

- schema 5 canonical SHA-256
  `34346cb0b98d3871d2493d182277d1077f342946945f58168060d2de4f9e8ce3`;
- the same simulation time and canonical hash;
- 39 observed objects, 18 units, and one active weapon;
- stable identity `ready` and zero duplicate stable IDs.

Schema 6 subsequently added order-target and complete waypoint-chain capture.
The same representative scenario exposed 24 ordered waypoints across 11 of its
18 units, including Yorktown and opposing P6, Osa, Petya, and Krivak routes.
The whole-world order SHA-256 was
`d9cdd7467949690c43f0bdb3ac5ab6403422ae896e8e1e83ae24b33d3edf5be1`.

Schema 7 added the player-authorized contact projection. After 11.71 seconds of
simulation, a controlled pause exposed 12 visible player plotting-table tracks:
2 own-force and 10 foreign, comprising 11 detected positions and one estimated
position. The foreign set retained unknown, hostile, and neutral relationships;
known P6 and Petya tracks carried player-derived classification while an unknown
track remained unclassified. Forty-six distinct current sensor sources summarize
206 valid observations without exposing true target references or coordinates.
Ten paused captures produced one contact hash
`d72fb0d0a63014b8671497f7d15af79e67a07d4582b0a02dcad71bf7e08a8730`
and one canonical hash
`5f4d16752d402603b4d48ceb7ae6c0816ab3c5a61cc822c4896e70f3f6536deb`.
Warm full-world capture median was 2,293 us; the ten-sample range was 2,204 to
19,750 us, with the maximum representing the first contact-inclusive read.

## Player event surface and rate

Schema 8 added the player event-log projection with bounded recent alerts and
intelligence, native scenario timestamps, priority, text, and exposed object
links. During a 20-second Red Sea observation, native initialization produced a
single burst of 16 player alerts between simulation seconds 2.73 and 4.74; the
count then remained 16 through second 17.01. This demonstrates bursty rather
than per-frame event delivery and gives the companion a source for rate/batching
measurements without scraping the UI. The burst included track creation and
hostile/neutral classification events.

After experimental pause, five reads reported one simulation time, one event
hash `cb181f8d3df81cd72d728b20bde89ba458f9f36eb13042fc6fa64ed975552027`,
and one canonical hash
`739ec05a732430d3f0c4434bf984ac05121d26bb0c0a942527d3d22ada536adf`.
Median contact- and event-inclusive full-world capture was 2,265 us.

## Representative facility, submarine, and reload identity

The Red Sea capture contained five exact `LandUnit` records: an observation
post, two Silkworm launchers, a Cross Slot radar site, and a coastal-artillery
battery. Their task force, folder, runtime type, unit type, variant, lifecycle,
and session identity were all populated.

After a full game-process restart, the typed scenario seam loaded NATO's
`Sub Duel Denmark Strait 1985`. Its six-unit world contained two submarines:
Baltimore SSN-704 (`usn_ssn_los_angeles`, Variant17) and K-316
(`wp_ssn_alfa`, Variant2), with depth, heading, speed, state, task force, and
loadout populated. Stable identity remained `ready` with zero duplicates.

The restart also supplied a direct stale-mapping counterexample: `unit:8`
identified Yorktown in Red Sea but K-316 in the submarine scenario. This is the
intended behavior of `game-unique-id` provenance and proves those IDs are scoped
to `loaded-scene`; consumers must discard them at unload/reload. Campaign tags,
when authored, remain the only cross-load stable identity source.

Twenty additional paused full-world reads measured 1,015 µs minimum, 1,068 µs
median, 1,136 µs p95, and 1,182 µs maximum on the Unity main thread. A first
post-transition read took 8,998 µs and remains a separate cold-path observation.

## Typed port-maintenance projection

External fixture revision 1042 materialized both the allowed roster and authored
current-force session input. `player_task_force_roster.generated.ini` had SHA-256
`4d1feeb86dbe0566ffb2123ed27dfda7ebdb94b417745b6f60fc108c04066c3f`.
The generated `campaign.ini` SHA-256 was
`71d8f7f79b958bc99e68d3d382b6979fd49d97eac939ff26c43d2a5ea7c4242d`.
The typed campaign action loaded the disposable port shell and the typed builder
action exposed 14 variants across ship, submarine, and air categories.

The external fleet materialized into two native maintenance rows with the exact
external IDs `unit:blue:knox-1` and `unit:blue:sturgeon-1`. Native Task Force
Manager state reported rearm available, no current damage, repair `not-needed`,
and nine ammunition summaries (five Knox and four Sturgeon). Thus repair, rearm,
and magazine state are now typed observations rather than placeholder warnings.

The typed draft edit staged, but did not confirm, one Perry reinforcement. Sea
Power also staged its two bundled SH-2F helicopters. Import validation reported:

- status `valid-uncommitted`, `committable=true`, `committed=false`;
- authority `external-campaign-service`;
- total cost 217, remaining budget 283, total quantity 3, capacity 16;
- transaction SHA-256
  `faa4c3e9cd0a1bbf8b46ae84d3f27e7c78e943f6d9aef8edd5edd32983c7497b`;
- zero warnings; native maintenance state remained attached to the validation.

Sea Power's temporary pending-unit IDs change every process. The transaction
document now replaces them with deterministic draft slots before hashing. Two
complete game-process runs staged equivalent Perry/SH-2F drafts and produced the
same transaction SHA-256
`faa4c3e9cd0a1bbf8b46ae84d3f27e7c78e943f6d9aef8edd5edd32983c7497b`.

No native campaign result or tactical outcome enters external authority.

## Load acceleration

The first launch populated 1,533 version-1 persistent file-hash entries: zero
hits and 1,533 misses. A consecutive no-build launch reported 1,533 hits and zero
misses while producing the exact same catalog and mod-set hashes. The cache is
atomic and fail-open; `CRIMEAN_TOM_DISABLE_LOAD_CACHE=1` disables reuse and
`CRIMEAN_TOM_REFRESH_LOAD_CACHE=1` forces a refresh for a process.

## Still-open Phase 1 gates

- Publish native active-weapon save/load, tactical-bubble, time-rebase,
  default-nondeterminism, and multi-instance evidence. Source inspection found
  the public save/reload path, but the pinned runtime currently reports native
  sensor and flight-deck save/load disabled, so no round-trip claim is made.
- Complete simulation-critical RNG/update call-site classification.
- Replace the temporary maintenance mission context once its minimum UI
  dependencies are proven.

The final broad main-menu control run passed after schema 8 on a fresh game
process. It covered authenticated companion/game transport, thread guards,
capabilities, world read, all normalized catalogs, override diagnostics, and
campaign read while preserving the same catalog and mod-set identities.
