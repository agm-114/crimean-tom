# UNREP

Independent AnchorChain mod for Sea Power `0.8.2-23810+s366`.
Adds a Supplies tab to Unit Status for player-controlled surface ships.
The page is a placeholder. It does not track supplies or transfer fuel or stores.

```powershell
./scripts/create-mod-icons.ps1 -Mod unrep
dotnet build src/UNREP/UNREP.csproj
```

Builds deploy `UNREP.dll`, `_info.ini` and the generated thumbnail to
`Sea Power_Data/StreamingAssets/UNREP`. Enable UNREP in Mod Manager and restart
the game. For compilation without deployment, use `-p:DeployToGame=false`.

The icon uses [Rentz and Ranger Unrep](https://commons.wikimedia.org/wiki/File:Rentz_and_Ranger_Unrep.jpg),
a U.S. Navy photograph by PH3 Layman. Its download URL and SHA-256 are recorded
in `scripts/mod-icons.sources.json`; the icon script downloads and crops it.
