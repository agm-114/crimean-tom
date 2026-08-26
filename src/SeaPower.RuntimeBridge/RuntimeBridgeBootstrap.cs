using UnityEngine;

namespace SeaPowerRuntimeBridge;

internal static class RuntimeBridgeBootstrap
{
    private const string RuntimeObjectName = "SeaPower.RuntimeBridge";

    public static void EnsureCreated()
    {
        if (UnityEngine.Object.FindAnyObjectByType<RuntimeBridgeBehaviour>() != null)
        {
            return;
        }

        var runtimeObject = new GameObject(RuntimeObjectName)
        {
            hideFlags = HideFlags.HideAndDontSave,
        };

        UnityEngine.Object.DontDestroyOnLoad(runtimeObject);
        runtimeObject.AddComponent<RuntimeBridgeBehaviour>();
    }
}
