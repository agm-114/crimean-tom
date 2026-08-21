# Packaged SQLite / local-file profile

Crimean Tom's ordinary single-player and private co-op deployment is a single
campaign service process plus local files. It does not require Docker, an
external database, a broker, a cache, or object storage.

## Layout

The campaign service run directory contains:

- `campaign.db` — SQLite WAL journal (commands, events, heads, blob refs)
- `blobs/` — content-addressed checkpoint and payload files
- `service.token` — current-user-only loopback bearer token
- `backups/` — operator backups created by `POST /control/backups`

Restore writes a verified copy to a new storage directory via
`POST /control/restores`. It does not swap the live journal while the host is
running; operators stop the host, replace the run directory, and start again.

## Operator workflow

1. `POST /control/campaigns/{id}/drain` — stop admitting new Primary/Shadow load
2. `POST /control/campaigns/{id}/pause` — reject time advances until resume
3. `POST /control/backups` — online backup with integrity hashes
4. `POST /control/campaigns/{id}/resume` after the backup is verified

PostgreSQL and external object storage remain optional later adapters. They are
not part of this packaged profile.
