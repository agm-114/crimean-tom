using AnchorChain;
using HarmonyLib;
using UnityEngine;
using DeckTweaks.Preparation;

namespace DeckTweaks;

[ACPlugin("crimean-tom.deck-tweaks", "Deck Tweaks", "0.1.3", after: new[] { Controls.Plugin.Id })]
[ACDependency(Controls.Plugin.Id, "0.3.0", null)]
public class Plugin : IAnchorChainMod
{
    public void TriggerEntryPoint()
    {
        var harmony = new Harmony("crimean-tom.deck-tweaks");
        DeckSettings.Register();
        DeckSettings.Settings.Changed += PreparationCatalog.ClearWeaponRates;
        harmony.PatchAll(typeof(Plugin).Assembly);
        Debug.Log("[Deck Tweaks] Loaded loadout selection and stateful aircraft preparation.");
    }
}
