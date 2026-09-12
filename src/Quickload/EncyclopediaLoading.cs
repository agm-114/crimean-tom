using System.Collections;
using System.Diagnostics;
using HarmonyLib;

namespace Quickload;

[HarmonyPatch]
internal static class EncyclopediaLoading
{
    internal static bool Pending { get; private set; }
    private static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(
        AccessTools.TypeByName("SeaPower.EncyclopedicDataManager"), "LoadUnitsFromMetadata");
    private static void Postfix(ref IEnumerator __result) => __result = Batch(__result);

    private static IEnumerator Batch(IEnumerator original)
    {
        Pending = true;
        Stopwatch budget = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                object? current;
                try
                {
                    if (!original.MoveNext()) break;
                    current = original.Current;
                }
                catch (Exception error) { Quickload.Instance.Fail(error); break; }
                // Native code yields once per entry. Process small batches rather than
                // spending hundreds of frames populating the encyclopedia.
                if (current == null && budget.Elapsed.TotalMilliseconds < 4) continue;
                yield return current;
                budget.Restart();
            }
        }
        finally { Pending = false; (original as IDisposable)?.Dispose(); }
    }
}
