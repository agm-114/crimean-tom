using AnchorChain;
using HarmonyLib;
using UnityEngine;

namespace SeaPower.GameBridge;

[ACPlugin("crimean-tom.game-bridge", "Sea Power Game Bridge", "0.2.2")]
public class Plugin : IAnchorChainMod
{
    public void TriggerEntryPoint()
    {
        string directory = Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!;
        if (!File.Exists(Path.Combine(directory, "enabled.txt"))) return;
        Harmony harmony = new("crimean-tom.game-bridge");
        harmony.CreateClassProcessor(typeof(PlayerBriefing)).Patch();
        harmony.CreateClassProcessor(typeof(PlayerCombat)).Patch();
        GameObject host = new("Sea Power Game Bridge");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<GameBridge>();
    }
}
