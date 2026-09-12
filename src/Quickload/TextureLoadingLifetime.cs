using System.Collections;
using HarmonyLib;
using SeaPower;

namespace Quickload;

[HarmonyPatch(typeof(TextureManager), "loadVegetationDistributionTextures")]
internal static class TextureLoadingLifetime
{
    internal static bool Quitting;
    private static void Postfix(ref IEnumerator __result) => __result = UntilShutdown(__result);
    private static IEnumerator UntilShutdown(IEnumerator original)
    {
        try
        {
            while (!Quitting && original.MoveNext()) yield return original.Current;
        }
        finally { (original as IDisposable)?.Dispose(); }
    }
}
