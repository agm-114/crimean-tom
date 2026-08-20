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

## Native active-weapon save/load

The pinned `SaveLoadManager.SaveGame` / `UnloadMissionAndReload` path now has a
controlled, authenticated experimental route. The companion waits for the
asynchronous native save write to stabilize before reporting its hash. The
launch route is limited to a paused player task force and uses three public,
fingerprinted native launch surfaces: a loaded `WeaponContainer`, a magazine
reload through `WeaponContainer.LaunchFromMagazine`, or an aircraft
`WeaponSystemHardpoint`. It does not construct or register a weapon directly.

| Family | Scenario and native object | Save SHA-256 | Round-trip and lifecycle evidence |
| --- | --- | --- | --- |
| Missile | Red Sea Breakout; Yorktown `usn_rgm-84d`, `weapon:10` | `9ce76cf4c20f35a1339830e4c9921f5908110ee8c642f863822d1b2f15369525` | Native save wrote one `Taskforce1Weapon` with platform/system, position, velocity, stage, launch time, and travel state. Reload restored the missile. Unpause advanced `Launch` to `ToBearing` to `MaintainHeight`; the blind-fire probe completed/disappeared near simulation second 37. A separate targeted RIM-66G round trip is described below. |
| Torpedo | Sub Duel Pacific Shield 1971; Scamp `usn_mk37_mod2`, `weapon:9` | `ba2c638e8c37e1312573a0da781da78c662a3e02b2f24829adc895b393c6bcda` | Magazine count changed 14 to 13, native save wrote one weapon with `UnderwaterLaunch`, platform `1`, and launcher `7`, and reload restored the same ID/type/platform/stage. Unpause advanced it to `UnderwaterToBearing` at 24 knots with increasing distance. |
| Bomb | Caron at Grenada 1983; A-7E CBU-100/Rockeye, `weapon:257` | `b18d072de1472e63c1f0fd8623aedf75d8fcd0b3034cc2d61c3dd30981a7066e` | Hardpoint store count changed 4 to 3, native save wrote one weapon with `DropFromAircraft`, platform `254`, and `WeaponSystemHardpoint5`, and reload restored the same count and active identity. Unpause advanced it to `TerminalApproach` while descending from about 10,007 to 9,102 feet. |

The native file reported `NumberOfTaskforce1Weapons=1` in all three blind-fire
cases. Whole-world canonical hashes differ across reload. One concrete
native fidelity defect is now isolated: Scamp's unrelated Mk-16 inventory read
15 before the Mk-37 save and 9 after reload, even though the launched Mk-37 and
its 13-round remainder restored. Active-weapon persistence is therefore proven
for these blind-fire representatives, but the native save is not suitable as
authoritative external campaign state.

The typed launch request now also accepts a foreign observed unit ID. In a
separate Red Sea run, Yorktown launched RIM-66G `weapon:354` at P6
`unit:89`. The pre-save weapon observation reported semi-active radar homing and
`intendedTargetId=unit:89`. Native save SHA-256
`f3a5df8526e0fb21c700782ba1233763b22b53656b826c4de0692a33c9c62e41`
contained `TargetObject=Taskforce2Vessel1`, the target geo-position, platform
`8`, and the launcher reference. Reload restored the same weapon ID, target ID,
launch time, and `Launch` stage at the exact saved simulation time.

After unpause the restored missile progressed through `ToBearing`,
`MaintainHeight`, `MaintainLoftAlt`, and `TerminalApproach`. At simulation second
23.267 the weapon disappeared and the P6 reported destroyed with 34.704 damage
points (0.2413 of its captured total-damage capacity). This proves target-link,
guidance-lifecycle, and final native outcome continuity for one targeted missile.
It does not prove equivalence for torpedo homing, bomb targeting, countermeasures,
or every damage subsystem, and the unrelated Mk-16 defect still prevents using
the native save as authoritative campaign state.

The typed tactical-exit workflow was also exercised after native reload. A
null relative mission path originally caused the result snapshot to fail after
Sea Power had already opened its debrief flow. Null-safe path normalization now
returns the scheduled postcondition and completes the public
`MissionDebriefViewModel` exit/confirm sequence without a menu click.

## Tactical spatial envelope

Schema 9 adds read-only spatial diagnostics to make the native tactical envelope
measurable without inferring it from the map UI. In a fully loaded Red Sea
Breakout capture, 40 observed objects occupied a maximum horizontal radius of
302.204 km and a maximum pair separation of 375.867 km (`unit:326` to
`unit:331`). Three objects were beyond the configured floating-origin rebase
threshold of 1,000 Unity units, or 67.2 km. The camera was only 5.09 km from the
current origin, so the distant objects were not an artifact of moving the view
to them.

The terrain manager reported 50 loaded and 50 finalized chunks. Each chunk is
approximately 22.224 km wide, the configured view distance is four chunks, and
the farthest loaded chunk center was 311.928 km from the current center tile.
Those chunks are a discontiguous set around active areas, not continuous terrain
covering the full object-to-object diameter.

Both extreme units continued to simulate while off-camera. Across 5.03 seconds
of unpaused simulation, their kinematics hashes and latitude/longitude changed;
the E-2C changed from about 238.3 to 236.5 knots and the MiG-21 from about 618.6
to 612.3 knots. The measured pair separation fell from 373.770 to 371.848 km.
This is positive evidence that one process can advance widely separated units,
but it is not yet proof of a continuous 300+ km physics bubble. A distant
sensor/weapon engagement crossing active terrain, including collision, effects,
guidance, and final damage, remains the explicit follow-up blocker. Spatial
diagnostics are observational and are intentionally excluded from the canonical
world hash.

## Controlled tick, RNG, and update-order prototype

Schema 10 exposes the result of an authenticated, explicitly experimental
controlled-tick run. The runtime fingerprints and patches the pinned
`GameTime.OnUpdate`, `GameUpdater.update`, and `GameFixedUpdater.fixedUpdate`
methods, refuses the command unless the mission is fully loaded and paused, and
automatically returns to pause after the requested game-update chain. Normal
runtime behavior is unchanged while the experiment is inactive.

A 30-tick run at a requested step of `0.03333333507180214` seconds advanced
logical simulation time exactly from `0.5166133642196655` to
`1.5166133642196655`. It completed 30 requested ticks and 30 `GameUpdater`
updates, observed 15 Unity fixed-physics updates, and returned paused. A
300-tick run advanced exactly from `20.6146297454834` to
`30.6146297454834`, with 300 game updates and 150 fixed updates. The experiment
seeded five known process-global sources: `Globals._rnd`,
`Globals._unityRandom`, `AI._aiRandom`, `SystemCompartment._svRandom`, and
`UnityEngine.Random`.

This is a useful partial control seam, not a deterministic runtime. Equal
300-tick windows subsequently observed both 150 and 151 fixed updates because
Unity still schedules physics from its frame cadence. Per-instance random
owners, job merge order, coroutine scheduling, and direct Unity time readers
also remain uncontrolled. The adapter reports those limitations in every
result, stays `ControlledExperimental`, and cannot receive campaign authority.
The published result therefore blocks convergence and hot-replica promotion
until fixed physics, indirect RNG ownership, jobs, and coroutine ordering are
controlled or proven immaterial.

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

## Default divergence baseline

Three fresh Sea Power processes loaded the same Red Sea Breakout mission through
the typed scenario and briefing actions. No deterministic hooks changed game
behavior. Each run was polled through the authenticated world route and paused
on the first main-thread command after simulation time crossed 20 seconds.

| Run | First observed >=20 s | Paused time | Objects / contacts / alerts |
| --- | ---: | ---: | --- |
| 1 | 20.028200 | 20.061533 | 44 / 11 / 15 |
| 2 | 20.026928 | 20.060263 | 43 / 12 / 16 |
| 3 | 20.016735 | 20.033403 | 43 / 12 / 16 |

All three runs had the same identity hash
`d4b81af247e3555522ab3b68a5a297a4621ec1a659761ff9f2e73dd793b23706`,
damage hash
`51343735eca12b97b8665b26d7a21d2718b4b1b43fbf767577bb81f2bc3defba`,
and authored-order hash
`d9cdd7467949690c43f0bdb3ac5ab6403422ae896e8e1e83ae24b33d3edf5be1`.
Every run had a distinct canonical, kinematics, logistics, active-weapons, and
contacts hash. Run 1 also had a distinct player-event hash; runs 2 and 3 shared
the 16-alert event projection.

This is the default baseline, not a statistical determinism claim. The pause
boundary itself varied by 28.13 ms because `GameTime.OnUpdate` advances from the
render-frame delta and the command executes on a later frame. That is already a
material failure for exact replay: the same typed load and nominal logical-time
boundary produced different object/contact/event counts and subsystem state.
Later controlled-clock measurements must separate frame-boundary drift from RNG
and update-order divergence rather than treating these hashes as interchangeable.

## Multi-instance isolation experiment

Crimean Tom's companion-side isolation works concurrently. While the primary
companion served `127.0.0.1:5137` and `CrimeanTom.Game.v1`, a second real
companion served `127.0.0.1:5138` and
`CrimeanTom.Game.v1.instance-b` from a separate run directory. Both control
hosts were healthy, their 43-character control tokens differed, and the primary
game remained connected only to the primary pipe.

The runtime bridge now resolves and advertises an instance ID, pipe name,
native persistent-data path, and bridge-owned experimental save directory. Its
probe save can be placed in a per-instance directory instead of Sea Power's
default `Saves` directory. This is a working isolation seam for control traffic
and Crimean-Tom-owned probe files, not a claim that the game is isolated.

The supported Steam launch path blocked the paired-game experiment. Requesting
app ID `1286220` a second time and waiting 12 seconds left exactly one Sea Power
process with the same PID. Source inspection found 43 direct
`Application.persistentDataPath` references across 20 game files, covering
native saves, `last_save`, mission/campaign status, cache/export paths,
`usersettings.ini`, `Player.log`, `ScriptEngine.log`, and Sentry archives. Unity
`PlayerPrefs` also uses the shared current-user registry key. The experimental
save route still causes native `SaveGame` to update shared `last_save`, even
when the save body itself is redirected.

Therefore simultaneous same-user replicas are **not valid evidence** on this
installation. The explicit blockers are Steam's single-process launch behavior,
shared native persistent data and preferences, and shared native logs. D1 paired
tests require a supported per-process data/profile root (or isolated Windows
users/VMs) and a launch path proven to create two independent game processes.

## Phase 1 gate disposition and carried blockers

- Expand targeted guidance/final-outcome coverage beyond the proven RIM-66G
  missile when a later portable-field gate needs torpedo, bomb, or
  countermeasure equivalence. Default nondeterminism and multi-instance evidence
  are published; paired same-user runs fail closed
  on Steam and shared native-profile blockers. The tactical-envelope experiment
  is published with its continuous-physics blocker. Blind-fire
  native missile, torpedo, and bomb round trips are now proven, with the Mk-16
  inventory discrepancy explicitly blocking any stronger full-state claim.
- The D0 structural audit publishes a negative time-rebase result and a
  deterministic compiled manifest with method ownership/hashes. Its 368
  callback/coroutine unknowns explicitly block hot-replica promotion until
  runtime ownership and scheduling are classified. The controlled-tick
  prototype provides exact logical time but also demonstrates 150/151 fixed-
  physics updates across equal windows, so update convergence remains blocked.
- The temporary maintenance mission remains a fenced disposable UI container.
  Replacing it with a repository-owned non-resolving mission is later content
  hardening, not a campaign-authority or Phase 1 read-contract dependency.

The final broad main-menu control run passed after schema 10 on a fresh game
process. It covered authenticated companion/game transport, thread guards,
capabilities, world read, all normalized catalogs, override diagnostics, and
campaign read while preserving the same catalog and mod-set identities.

The P1 exit gate is complete. Representative entities and reload invalidation,
quiescent canonical extraction, catalog provenance, measured extraction budget,
portable-field classification, typed UI seams, determinism experiments, and the
uncommitted port draft all have pinned live evidence above. The listed
determinism, native-save, tactical-envelope, and multi-instance limitations are
fail-closed inputs to later promotion/handoff gates rather than unreported P1
work.
