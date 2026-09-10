# Workshop icons

Run in PowerShell 7 on Windows:

```powershell
./scripts/create-mod-icons.ps1
./scripts/create-mod-icons.ps1 -Mod deck-tweaks
./scripts/create-mod-icons.ps1 -Mod quickload -Size 512
```

Default output is two 1024 × 1024 JPEGs in `artifacts/mod-icons/`:

- `deck-tweaks.jpg`
- `quickload.jpg`

Photos are downloaded once into `sources/` below the output directory and checked
against the SHA-256 values in `mod-icons.sources.json` on every run. The manifest
records the original file pages, credits and public-domain status. Review an
upstream change before replacing a hash.

The script centre-crops photos above a charcoal title band with a red divider.
It uses the Windows Bahnschrift font. It creates no AI artwork, edits no mod
descriptions, and does not build, deploy, launch or upload either mod.
Generated files are ignored by Git through `artifacts/`.
