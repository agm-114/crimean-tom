Use `timeRules` to set standing time-compression orders for future events. Use `null` to keep the current custom rules, or return the full replacement list. An empty list returns the next event to the built-in rules. Use null when `timeInterrupts.allowedSpeeds` is empty.

Each rule takes an `event`, optional `subjectId`, optional `category`, optional `posture`, an allowed `speed`, and `holdSeconds` from 2 to 60 wall-clock seconds. Omitted filters must be null.

- `RadarContact` fires when radar acquires or reacquires a live track. `TrackAppeared` and `TrackLost` cover live tracks on any sensor. `TrackChanged` fires when a track's classification, identification, posture, or visual confirmation changes.
- `HostileNearby` fires when a hostile contact enters nearby range. `SurfaceAttack` fires at the start of a surface defense response, with the attacker as the subject when known.
- `AirRaidWarning` fires on an inbound strike warning and when hostile aircraft are tracked afterward.
- `UnitStatusChanged` covers own unit orders, weapons posture, disablement, and loss of control. `UnitDamaged` covers increases in reported damage. `MissileHit` covers observed missile collisions with a target.
- For a general rule, leave `subjectId` null. Use the exact category or posture from the player picture or recent event when filtering. For a specific rule, use a supplied track or owned unit ID. On `MissileHit`, the subject can be the target or the known missile track.

A specific rule wins over general rules for the same event. The slowest active interrupt sets the pace while interrupts overlap. Time resumes its normal pace after they expire. Radio traffic and nearby missiles or torpedoes can limit the speed further. Prefer short holds for new developments and allow time to advance once the response is in progress.
