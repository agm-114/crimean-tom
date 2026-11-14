# Shared gameplay settings

The mod project supplies references to Noesis, Seapower-Scripts,
UnityEngine.CoreModule and Harmony. Add the source package to the project.
It brings in the SharedUi sources through its NuGet dependency.

```xml
<PackageReference Include="SeaPower.SharedSettings" Version="[0.1.0]" PrivateAssets="all" />
```

Use a NuGet source containing both packages. For builds from this checkout, use
the direct imports in place of the package reference.

```xml
<Import Project="../SeaPower.SharedUi/SeaPower.SharedUi.props" />
<Import Project="../SeaPower.SharedSettings/SeaPower.SharedSettings.props" />
```

Create a `SettingsValues` with the mod's shipped defaults, pass it to
`ModSettings`, then call `GameplaySettings.Register` with the mod's Harmony
instance, tab title, settings and an editor builder. The source compiles into
each consuming mod. Their registered tabs share one Gameplay view.

Editors read from `SettingsDraft.Values` and write through `SettingsDraft.Set`
or `Remove`. Wrap editable controls with `SettingsDraft.Editor` so default
mode keeps them read-only. Read active settings through `ModSettings.Current`. Subscribe
to `Changed` if the mod caches values that need to refresh on Accept.

The library starts in default mode and stores custom values and the selected
mode in `CrimeanTom/Settings/<name>.ini` under Unity's persistent data directory.
Checking **Use mod defaults** restores the shipped values and keeps the previous
custom values available. In custom mode, **Reset to mod defaults** replaces the
draft with shipped values and leaves editing enabled. **Accept** saves the reset.
**Back** and **Revert** discard the draft.
