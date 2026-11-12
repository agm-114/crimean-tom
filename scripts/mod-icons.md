# Workshop icons

Run in PowerShell 7 on Windows:

```powershell
./scripts/create-mod-icons.ps1
./scripts/create-mod-icons.ps1 -Mod deck-tweaks
./scripts/create-mod-icons.ps1 -Mod quickload -Size 512
./scripts/create-mod-icons.ps1 -Mod boat-ops
./scripts/create-mod-icons.ps1 -Mod amphibs
./scripts/create-mod-icons.ps1 -Mod unrep
./scripts/create-mod-icons.ps1 -Mod ooda
./scripts/create-mod-icons.ps1 -Mod observer
```

Default output is seven 1024 × 1024 JPEGs in `artifacts/mod-icons/`:

- `deck-tweaks.jpg`
- `quickload.jpg`
- `boat-ops.jpg`
- `amphibs.jpg`
- `unrep.jpg`
- `ooda.jpg`
- `observer.jpg`

Each render is then copied to `thumbnail.jpg` in the mod folder named by
`modDirectory` in the manifest, beside that mod's `_info.ini`. The mod builds
deploy that copy when it is present, so the installed mod folder is ready to
upload as a Workshop item. Run this script before packaging a mod. Sea Power
itself never reads the file; its mod manager lists a name and description only.

Photos are downloaded once into `sources/` below the output directory and checked
against the SHA-256 values in `mod-icons.sources.json` on every run. The manifest
records the original file pages, credits and public-domain status. Review an
upstream change before replacing a hash.

The script fits the photo above a charcoal title band with a red divider.
The optional `crop` in the manifest selects a rectangle in source pixels.
It uses the Windows Bahnschrift font. It creates no AI artwork, edits no mod
descriptions, and does not build, deploy, launch or upload the mods.
Git ignores the renders, cached photos, and generated thumbnails. OODA and
Observer use pinned source photos and fixed crops in the manifest.
