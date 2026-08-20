# Phase 3B durable-journal evidence

Date: 20 August 2026

## Current disposition

Phase 3B is in progress. This checkpoint implements the fenced transactional
journal, validated canonical snapshots, content-addressed blobs, and full-store
integrity verification. Atomic backup and restore are also implemented;
crash-boundary recovery and the independent process-level writer proof remain
incomplete.

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

## Content-addressed blobs and integrity

Database schema migration 2 to 3 adds immutable blob metadata and fenced logical
references for tactical checkpoints and normalized catalogs. Bytes are written
to an adjacent `sha256/<prefix>/<hash>` tree through a same-directory temporary
file, flushed to disk, atomically renamed, and re-hashed. A transaction may
reference only a blob at or before the current durable head and requires the
live campaign writer epoch. An interruption before the reference commit can
leave only a safe unreferenced object; the database cannot commit a reference
before the file is durable.

The integrity pass runs SQLite `integrity_check`, recovers every campaign to its
head, reconciles command/event/head counts, reconstructs the exact latest
subject-revision table from the journal, validates every snapshot, and verifies
the length and SHA-256 content of every referenced external blob.

## Atomic backup and restore

Backup uses SQLite's online backup API to create a consistent database image in
a private staging directory. It reads the exact blob-reference set from that
copied image, copies and re-hashes only those immutable objects, runs the full
cross-store integrity check on the staged bundle, and writes a strict manifest
containing the database hash, campaign heads/hashes, and blob hashes/lengths.
Renaming the complete staging directory is the only publication point.

Restore refuses an existing destination, verifies the strict manifest and every
byte before staging, copies the database and blobs durably, increments all
writer epochs while clearing owners/expiries, re-runs full integrity and campaign
head comparison, and publishes the restored storage with one directory rename.
Thus a coordinator authorized before the backup cannot write to the restored
timeline.

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
11. atomically writes, reads, and retries a checkpoint blob while rejecting changed
    bytes under the same logical reference;
12. passes the full cross-store integrity check;
13. creates a staged database-plus-blob backup and validates its campaign hash;
14. restores the bundle, reads the restored checkpoint, and recovers the same canonical head;
15. proves the source writer handle is fenced in the restored database;
16. reacquires under coordinator A, incrementing the source epoch and fencing its old handle; and
17. releases the replacement so coordinator B can acquire a still-newer source epoch.

The temporary database, WAL, and shared-memory files are removed after the broad
story closes all connections.

Verification command:

```powershell
dotnet test tests/CrimeanTom.Campaign.IntegrationTests/CrimeanTom.Campaign.IntegrationTests.csproj --configuration Debug
```

## Remaining work

- crash injection at validation, append, snapshot, and acknowledgement boundaries;
- process-level two-coordinator and kill/restart evidence for the full Phase 3 gate.
