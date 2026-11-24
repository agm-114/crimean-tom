# Crimean Tom Workshop bundle

Crimean Tom installs Amphibs and Deck Tweaks together. The Workshop listing
links to the standalone mods for details. Enable the bundle or the standalone
mods in Sea Power's Mod Manager. Both options require Controls & Settings 0.3.0
or newer and an Anchor Chain loader such as Keel. [Crimean Tom on Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3838295187).

Use the release packages for both components as inputs. Their `_info.ini`
files must declare the same Sea Power version. For a release of one component,
use its new package and the other component's matching published package.

With Sea Power running and the Game Bridge enabled, release a component and
refresh the bundle with one command:

```powershell
./scripts/publish-crimean-tom.ps1 -AmphibsPackage C:/Mods/Amphibs -DeckTweaksPackage C:/Mods/DeckTweaks -Update DeckTweaks -Publish
```

Use `-Update Amphibs` for Amphibs or `-Update Both` to release both. The script
previews each listing before submitting the updates. Use `-Preview` in place of
`-Publish` to review the release. Add `-AmphibsPreviews` or
`-DeckTweaksPreviews` to upload new gallery images to that component. Use
`-Update Bundle` to refresh only Crimean Tom. Run without `-Publish` or
`-Preview` to package locally under `artifacts/workshop/`.
