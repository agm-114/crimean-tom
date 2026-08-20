# Phase 3B durable-journal evidence

Date: 20 August 2026

## Current disposition

Phase 3B is in progress. This checkpoint implements the fenced transactional
journal and validated canonical snapshots. It does not claim the Phase 3B blob,
backup, integrity, or crash-recovery work is complete.

## Implemented durable boundary

`CrimeanTom.Campaign.Service` owns a local SQLite store using WAL mode,
`synchronous=FULL`, foreign keys, strict tables, a pinned schema version, and the
.NET 8 servicing line of `Microsoft.Data.Sqlite`.

Campaign creation stores and subsequently compares the schema version,
algorithm version, seed, catalog fingerprint, name, and campaign ID. An unknown
database schema is rejected before the store applies its own schema.

Writer admission is one atomic conditional update. It increments a durable
epoch and succeeds only when the campaign has no writer, the old lease expired,
or the same coordinator deliberately reacquires and fences its prior handle.
Every append transaction checks campaign ID, coordinator ID, epoch, and database
lease expiry both before work and in the final durable-head compare-and-swap.

One immediate SQLite write transaction commits all of the following or none:

- strict command JSON and its SHA-256 hash;
- stable command ID and a separately typed idempotency key;
- strict event JSON and its SHA-256 hash;
- global commit position and event ID;
- typed subject ID and new subject revision; and
- the campaign durable head.

Command, event, subject, effective time, schema, algorithm, expected revision,
and causal-command relationships are checked before append. Exact retries return
the original stored event with `Duplicate=true`. Reuse of either the command ID
or idempotency key with different bytes fails as `idempotency-conflict`.

Ordered event reads verify each stored event content hash before deserialization.

## Snapshot and recovery boundary

The canonical serializer now has a strict inverse. It rejects unknown fields,
missing required fields, malformed or non-canonical IDs, invalid enums,
duplicate resource keys, and any state that fails cross-entity domain
invariants. Re-serializing a decoded snapshot is byte-identical.

Database schema migration 1 to 2 adds content-hashed snapshots transactionally.
A snapshot write requires the live writer epoch and the current durable head.
Before storage, the service independently recovers that head from the newest
existing snapshot plus hash-verified journal events and requires the supplied
snapshot hash to match. A retry at the same position is idempotent only when the
hash and bytes are identical.

Recovery selects the newest snapshot at or before the durable head, verifies its
content hash and canonical identity, validates the complete state graph, then
applies a contiguous hash-verified journal tail. A gap, corrupt snapshot/event,
metadata mismatch, or final-head mismatch fails recovery rather than silently
falling back to a different timeline.

## Broad integration evidence

The existing single campaign story now crosses both the pure kernel and a real
temporary SQLite database. It:

1. creates and initializes a WAL campaign database;
2. acquires coordinator A's writer epoch and rejects coordinator B while it is live;
3. transactionally appends all fifteen multi-region theater commands/events;
4. proves the durable head matches the in-memory head;
5. proves an exact duplicate returns the original event;
6. proves changed content under the same idempotency identity is rejected;
7. writes an idempotent, replay-validated snapshot at commit position 10;
8. reads and hash-checks the full ordered event log;
9. replays independently from the metadata origin to the exact Phase 3A canonical hash;
10. recovers from snapshot 10 plus five journal events to the same bytes and hash;
11. reacquires under coordinator A, incrementing the epoch and fencing its old handle; and
12. releases the replacement so coordinator B can acquire a still-newer epoch.

The temporary database, WAL, and shared-memory files are removed after the broad
story closes all connections.

Verification command:

```powershell
dotnet test tests/CrimeanTom.Campaign.IntegrationTests/CrimeanTom.Campaign.IntegrationTests.csproj --configuration Debug
```

## Remaining work

- content-addressed adjacent checkpoint/catalog blobs and corruption handling;
- crash injection at validation, append, snapshot, and acknowledgement boundaries;
- integrity checks plus atomic database-and-blob backup/export and restore;
- process-level two-coordinator and kill/restart evidence for the full Phase 3 gate.
