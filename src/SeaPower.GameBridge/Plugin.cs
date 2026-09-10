using AnchorChain;
using UnityEngine;

namespace SeaPower.GameBridge;

[ACPlugin("crimean-tom.game-bridge", "Sea Power Game Bridge", "0.1.0")]
public class Plugin : IAnchorChainMod
{
    public void TriggerEntryPoint()
    {
        string directory = Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!;
        if (!File.Exists(Path.Combine(directory, "enabled.txt"))) return;
        GameObject host = new("Sea Power Game Bridge");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<GameBridge>();
    }
}
