using AnchorChain;
using HarmonyLib;
using SeaPower.SharedSettings;
using SeaPower.SharedUi;
using UnityEngine;

namespace Observer;

[ACPlugin("crimean-tom.observer", "Observer", "0.1.0")]
public class Plugin : IAnchorChainMod
{
    private const string DebugSection = "Debug";
    private const string Spectator = "Spectator";
    private static ModSettings settings = null!;

    internal static bool SpectatorEnabled => settings.Current.Get(DebugSection, Spectator) != 0;

    public void TriggerEntryPoint()
    {
        var harmony = new Harmony("crimean-tom.observer");
        var defaults = new SettingsValues();
        defaults.Set(DebugSection, Spectator, 0);
        settings = new ModSettings("Observer", defaults);
        GameplaySettings.Register(harmony, "Observer", settings, (panel, draft) =>
        {
            panel.Children.Add(draft.Editor(NativeUi.Check("Watch scenarios", draft.Values.Get(DebugSection, Spectator) != 0,
                enabled => draft.Set(DebugSection, Spectator, enabled ? 1 : 0))));
            panel.Children.Add(NativeUi.Text("Watch all sides on the tactical map. Applies when starting a new standalone scenario."));
        });
        harmony.PatchAll(typeof(Plugin).Assembly);
        Debug.Log("[Observer] Loaded.");
    }
}
