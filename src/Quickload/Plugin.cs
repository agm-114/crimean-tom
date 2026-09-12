using AnchorChain;
using UnityEngine;

namespace Quickload;

[ACPlugin(Quickload.Id, "Quickload", "0.1.0")]
public class Plugin : IAnchorChainMod
{
    public void TriggerEntryPoint()
    {
        GameObject host = new("Quickload");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<Quickload>();
    }
}
