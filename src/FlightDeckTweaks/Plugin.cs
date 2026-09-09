using AnchorChain;
using HarmonyLib;
using UnityEngine;

namespace FlightDeckTweaks;

[ACPlugin("crimean-tom.flight-deck", "Flight Deck Tweaks", "0.1.0")]
public class Plugin : IAnchorChainMod
{
    public void TriggerEntryPoint()
    {
        new Harmony("crimean-tom.flight-deck").PatchAll(typeof(Plugin).Assembly);
        Debug.Log("[Flight Deck Tweaks] Loaded row loadout selection.");
    }
}
