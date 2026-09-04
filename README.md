# Crimean Tom

Crimean Tom currently consists of one lightweight native Task Force Mode entry for Sea Power.

Selecting `Crimean Tom Operations` from `CAMPAIGNS` starts Sea Power's own commander onboarding and task-force builder. The repository contains no campaign server, web interface, background simulation, runtime streaming, custom map, or generated artwork. The native game owns campaign UI, saves, force persistence, deployment, battles, debriefs, repair, and rearm.

The one stock-backed mission reference is temporary launch scaffolding. Gameplay and campaign structure will be designed after the native entry is proven.

The DLL only registers the mod through AnchorChain and writes a load marker. The entry itself lives in `content/campaigns/crimean-tom-operations`.

Build and deploy with:

```powershell
dotnet test CrimeanTom.slnx --configuration Release
```

The retired implementation remains available on `codex/archive-server-simulation-2026-09-04`.
