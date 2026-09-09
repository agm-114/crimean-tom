# Flight Deck Tweaks

Independent AnchorChain mod. Adds a Loadout dropdown to pending-launch rows in the
Flight Operations table. Changing it restarts preparation using the new loadout's
normal preparation time and ammunition cost. Aircraft count and callsign stay the
same. State includes the aircraft count and activity, such as `1x Launch Prep` or
`1x Awaiting Crew`. Info only shows crew, deck-space allocation and the preparation
timer. Loadout appears only in its dropdown column, including while waiting for
crew or deck space. The compact labels are currently English. The mod does not
depend on Crimean Tom.

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
or `AnchorChainDll` when using another installation. Restart Sea Power after
deploying an updated DLL. Startup loading was confirmed, but the dropdown and
Info-column changes still need an in-game check.
