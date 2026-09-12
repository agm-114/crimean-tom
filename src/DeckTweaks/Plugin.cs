using AnchorChain;
using HarmonyLib;
using UnityEngine;

namespace DeckTweaks;

[ACPlugin("crimean-tom.deck-tweaks", "Deck Tweaks", "0.1.0")]
public class Plugin : IAnchorChainMod
{
    public void TriggerEntryPoint()
    {
        new Harmony("crimean-tom.deck-tweaks").PatchAll(typeof(Plugin).Assembly);
        Debug.Log("[Deck Tweaks] Loaded loadout selection and stateful aircraft preparation.");
    }
}
