using AnchorChain;
using UnityEngine;

namespace SeaPower.UiHelper;

[ACPlugin("crimean-tom.ui-helper", "Sea Power UI Helper", "0.1.0")]
public class Plugin : IAnchorChainMod
{
    public void TriggerEntryPoint()
    {
        string directory = Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!;
        if (!File.Exists(Path.Combine(directory, "enabled.txt"))) return;
        GameObject host = new("Sea Power UI Helper");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<UiBridge>();
    }
}
