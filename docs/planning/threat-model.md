# Threat model

Trust boundary: a private operator machine or LAN of trusted player and Sea
Power processes. Untrusted public internet clients are out of scope.

## Surfaces

| Surface | Trust | Failure mode | Mitigation in this tree |
| --- | --- | --- | --- |
| Unity Runtime Bridge named pipe | Same Windows user | Malformed frames, hung commands | Length-prefixed frames, session fencing, command deadlines, execution-thread guard |
| Companion loopback HTTP | Bearer token, current-user ACL | Stolen token, malformed local requests | Token file ACL and bearer authentication |
| Campaign gRPC companion stream | Bearer token | Stale epoch, duplicate frames, incompatible checkpoint | Epoch fences, idempotency keys, checkpoint hash validation |
| Campaign operator HTTP | Bearer token | Pause/drain/backup abuse by anyone with the token | Token is the operator secret; pause/drain are intentional privileged controls; content type alone never authenticates the HTTP listener |
| SQLite journal and blob store | Local disk | Partial write, corrupt blob, restore to live path | WAL + CAS writer epoch, content hashes, restore to a new directory only |
| Player projection HTTP | Same-site process-lifetime cookie scoped to `/player`; coalition stored in the local player profile | CSRF or truth leakage into filtered view | Cookie cannot authorize operator routes; `CampaignProjectionBuilder` omits non-coalition entities and classified sentinels |
| Player campaign orders | Session + persisted coalition | Foreign-force retasking, stale writes, replayed or altered requests | Service derives coalition authority from the player profile, validates group ownership and expected revision, and binds each idempotency key to one payload; unsupported order kinds reject |
| Player command envelopes | Session + coalition | Forged prediction IDs, firing via projection | Presentation and control-transfer envelopes do not commit weapon effects; unsupported kinds reject |
| Update / catalog / runtime fingerprint | Operator-supplied game build | Silent desync after a Sea Power patch | Companion rejects preparation/activation when current fingerprints differ from those pinned into the assignment; campaign-global admission is not claimed |
| Logs and screenshots | Local run directory | Secrets in Player.log or PNG HUD | Screenshots stay on disk/HTTP image route, not the 256 KiB pipe |

## Non-goals

- Independent adjudication of a hostile player who owns a full-truth process.
- Timing/size side-channel resistance for public matchmaking.
- Supply-chain signing of every NuGet restore on a player PC.
- Treating SignalR, if added later, as a source of committed campaign history.

## Fail-closed compatibility

When the current runtime or catalog fingerprint disagrees with the requirement
pinned into an assignment, the companion rejects preparation and activation.
The companion may remain connected for authorized commands and filtered
presentation. Campaign data stays in the journal; operators restore from
`POST /control/backups` after a failed update rather than mutating an
incompatible live head. Campaign-global build admission remains future work.
