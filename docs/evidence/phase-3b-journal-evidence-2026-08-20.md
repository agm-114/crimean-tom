# Phase 3B durable-journal evidence

Date: 20 August 2026

## Current disposition

Phase 3B is in progress. This checkpoint implements the fenced transactional
journal. It does not claim the Phase 3B snapshot, blob, backup, or crash-recovery
work is complete.

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

## Broad integration evidence

The existing single campaign story now crosses both the pure kernel and a real
temporary SQLite database. It:

1. creates and initializes a WAL campaign database;
2. acquires coordinator A's writer epoch and rejects coordinator B while it is live;
3. transactionally appends all fifteen multi-region theater commands/events;
4. proves the durable head matches the in-memory head;
5. proves an exact duplicate returns the original event;
6. proves changed content under the same idempotency identity is rejected;
7. reads and hash-checks the full ordered event log;
8. replays from the metadata origin to the exact Phase 3A canonical hash;
9. reacquires under coordinator A, incrementing the epoch and fencing its old handle; and
10. releases the replacement so coordinator B can acquire a still-newer epoch.

The temporary database, WAL, and shared-memory files are removed after the broad
story closes all connections.

Verification command:

```powershell
dotnet test tests/CrimeanTom.Campaign.IntegrationTests/CrimeanTom.Campaign.IntegrationTests.csproj --configuration Debug
```

## Remaining work

- canonical snapshot decode, validation, and replay from the newest valid snapshot;
- content-addressed adjacent checkpoint/catalog blobs and corruption handling;
- crash injection at validation, append, snapshot, and acknowledgement boundaries;
- integrity checks plus atomic database-and-blob backup/export and restore;
- process-level two-coordinator and kill/restart evidence for the full Phase 3 gate.
