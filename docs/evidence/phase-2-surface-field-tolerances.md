# Phase 2 surface field tolerances

This artifact defines the promotion-critical comparison contract for the first
allow-listed surface family, `usn_ff_knox_84` / `Variant13` / `Default`. The
runtime asserts these values after materialization, update, and checkpoint
restore before it reports success.

| Field | Requested unit | Runtime representation | Tolerance | Promotion critical | Live observation and rationale |
| --- | --- | --- | ---: | --- | --- |
| External entity ID | string | `_campaignTag` | exact | yes | Exact in materialize, retained restore, and scene-reload reconstruction. |
| Unit/variant/loadout | catalog IDs | object parameters | exact | yes | Exact Knox tuple in every live readback; any other tuple fails closed. |
| Task force | scoped ID | native `Taskforce` | exact | yes | Exact `taskforce:Taskforce1`; only the player task force is admitted. |
| Latitude | degrees | `GeoPosition` double | `1e-9` degrees | yes | Exact to printed double precision in live create, update, and restore. |
| Longitude | degrees | `GeoPosition` double | `1e-9` degrees | yes | Exact to printed double precision in live create, update, and restore. |
| Heading | degrees | Unity float | `0.001` degrees | yes | Requested 147.125 and 211.25 degrees read back exactly; tolerance covers only float conversion. |
| Telegraph | native integer | native integer | exact | yes | Requested zero/one read back exactly. |
| Speed | knots | native float | `0.001` knots | yes on restore | Zero and 5.4-knot values read back exactly within float precision. Materialization/update derive speed from native telegraph; checkpoint restore applies the captured value. |
| Disabled state | boolean | native `ObjectBase.Disabled` | exact | yes | Restore must finish enabled and non-destroyed. |
| Destroyed state | boolean | native destruction flag | exact | yes | Any destroyed readback fails the operation. |
| Native game ID | integer | scenario-local `UniqueID` | not compared across scenes | no | Retained restore preserved ID 355. Scene reload happened to allocate 355 again, but the durable identity is the external entity ID. |

Damage, fuel, magazine, sensor, formation, and hosted-aircraft application are
not silently tolerated. They remain unsupported fields and block promotion or
handoff whenever their loss would matter.
