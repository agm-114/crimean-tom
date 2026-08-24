# Threat model

Trust boundary: a private operator machine or LAN of trusted player and Sea
Power processes. Untrusted public internet clients are out of scope.

## Surfaces

| Surface | Trust | Failure mode | Mitigation in this tree |
| --- | --- | --- | --- |
| Unity Runtime Bridge named pipe | Same Windows user | Malformed frames, hung commands | Length-prefixed frames, session fencing, command deadlines, execution-thread guard |
| Companion loopback HTTP | Bearer token, current-user ACL | Stolen token, CSRF from local browsers | Token file ACL; monitor cookies authenticate only GET/HEAD and cannot authorize operator writes |
| Campaign gRPC companion stream | Bearer token | Stale epoch, duplicate frames, incompatible checkpoint | Epoch fences, idempotency keys, checkpoint hash validation |
| Campaign operator HTTP | Bearer token | Pause/drain/backup abuse by anyone with the token | Token is the operator secret; pause/drain are intentional privileged controls |
| SQLite journal and blob store | Local disk | Partial write, corrupt blob, restore to live path | WAL + CAS writer epoch, content hashes, restore to a new directory only |
| Player projection HTTP | Bearer token plus coalition id | Truth leakage into filtered view | `CampaignProjectionBuilder` omits non-coalition entities and classified sentinels |
| Player command envelopes | Session + coalition | Forged prediction IDs, firing via projection | Envelopes never commit kernel events; unsupported kinds reject |
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
