# Full-catalog WW3++ expansion evidence — 2026-08-23

This is a current implementation checkpoint for the enlarged September 1989
campaign seed. It records deterministic seed coverage and local headless
bootstrap measurements; it is not evidence of live Sea Power frame rate or a
vanilla-versus-streamed tactical benchmark.

## Seed inventory

- 55 regions and 81 bases.
- 4,096 canonical entities, including 1,329 maritime, 491 fixed-wing air, 149
  rotary-wing, 399 logistics, 984 physical ground, 654 installation, and 90
  abstract land-formation entities.
- 919 groups and 741 opening orders, for 5,892 durable bootstrap commits.
- All 187 installed `land_units`, all 111 installed `aircraft`, and all 241
  usable installed vessel definitions are represented at least once.
- The enlarged theater includes defended airbases, layered GBAD and radar
  networks, strategic missile systems, formation-projected armor/SHORAD
  targets, helicopters, civilian traffic, and geographically distributed
  naval forces including China and Japan.

## Local bootstrap checkpoint

The original record-at-a-time path took 394.04 seconds, grew the SQLite files
to about 220 MiB, and was observed above 807 MiB working set / 833 MiB private
memory. It opened and validated 5,892 separate transactions and wrote a growing
full-state snapshot every 100 commits.

The first atomic-batch implementation completed the same 5,892 commits in
7.824 seconds with a 32.2 MiB database. The measured process rose from a 68.9
MiB baseline to a 656.9 MiB peak working set / 696.0 MiB peak private memory.
The durable head was 5,892 with canonical hash
`75d8a10efbdbaa7de5b8a67a1d06743b16e959870778f8f6247486971ba5d5b9`.

This eliminates the quadratic replay/snapshot and disk-growth problem. The
remaining high-water allocation was then traced to ASP.NET server GC creating
one managed heap per logical CPU: this machine exposes 24 logical processors.
Running the same importer with workstation GC completed in 8.009 seconds and
peaked at 162.8 MiB working set / 102.8 MiB private memory, with the same 32.3
MiB database and 5,892 durable commits. The service now defaults to workstation
GC for the local desktop topology; dedicated deployments can opt back into
server GC through the standard `DOTNET_gcServer=1` runtime override.

## Verification at this checkpoint

- `dotnet build src/CrimeanTom.Campaign.ServiceHost/CrimeanTom.Campaign.ServiceHost.csproj --no-restore`
  passed with zero warnings and zero errors after the service was stopped.
- `pnpm test:campaign` passed all 11 campaign integration tests.
- The persistence integration now injects a failure after a batch event insert,
  proves the head and journal remain empty, retries the whole batch, verifies
  the final snapshot/hash, and verifies idempotent acknowledgement retry.

Full protocol-suite and live tactical/vanilla performance evidence remain
separate completion gates.
