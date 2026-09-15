using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace Spacewar;

// Only the sky's appearance changes. Native weather, illumination and sensor conditions stay physical.
[HarmonyPatch(typeof(DynamicSky), nameof(DynamicSky.OnLateUpdate))]
internal static class OrbitalAtmosphere
{
    private static Material? material;
    private static float tile, transition, cloudBlend, fogDensity;
    private static bool applied;

    [HarmonyPrefix]
    internal static void Restore()
    {
        if (!applied) return;
        if (material != null)
        {
            material.SetFloat("_Tile", tile);
            material.SetFloat("_Transition", transition);
            material.SetFloat("_LayerBlend", cloudBlend);
        }
        RenderSettings.fogDensity = fogDensity;
        applied = false;
        material = null;
    }

    [HarmonyPostfix]
    private static void Apply(Material ____skyboxMaterial)
    {
        if (!Singleton<RenderPosition>.InstanceExists()) return;
        var view = Singleton<RenderPosition>.Instance;
        if (!view.IsCameraAttachedToTarget || SpacecraftProxy.For(view.SelectedObject) == null || ____skyboxMaterial == null) return;
        material = ____skyboxMaterial;
        tile = material.GetFloat("_Tile");
        transition = material.GetFloat("_Transition");
        cloudBlend = material.GetFloat("_LayerBlend");
        fogDensity = RenderSettings.fogDensity;
        applied = true;
        material.SetFloat("_Tile", 0); // Native clear-night atlas gives an orbital dark sky.
        material.SetFloat("_Transition", 0);
        material.SetFloat("_LayerBlend", 0);
        RenderSettings.fogDensity = fogDensity * 0.03f;
    }
}
