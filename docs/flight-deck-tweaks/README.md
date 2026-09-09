# Flight Deck Tweaks

Empty, independent AnchorChain mod. Its entry point does nothing. No patches,
configuration or game content are included, and it does not depend on Crimean Tom.

Source: [src/FlightDeckTweaks](../../src/FlightDeckTweaks). See the
[repository README](../../README.md) for shared build prerequisites and AnchorChain
setup. Run the commands below from the repository root.

Build without deploying:

```powershell
dotnet build src/FlightDeckTweaks/FlightDeckTweaks.csproj
```

Explicitly deploy only this mod:

```powershell
dotnet build src/FlightDeckTweaks/FlightDeckTweaks.csproj -p:DeployToGame=true
```

Deployment copies `FlightDeckTweaks.dll` into the game's
`Sea Power_Data/StreamingAssets/FlightDeckTweaks` directory. Override `SeaPowerDir`
or `AnchorChainDll` when using another installation. Runtime loading has not been
smoke-tested yet.
