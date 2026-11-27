using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SeaPower;
using Debug = UnityEngine.Debug;

namespace Quickload;

[HarmonyPatch]
internal static class DeferredPointValues
{
    private const double FrameBudgetMilliseconds = 8;
    private delegate void ApplyPrice(string folder, string name, SimpleObjectParameters unit, ref int computed, ref int cached);
    private static readonly ApplyPrice apply = AccessTools.MethodDelegate<ApplyPrice>(
        AccessTools.Method(typeof(PointValueCalculator), "ApplyValues"));
    private static readonly Action<UnitMetadataProvider> mirror = AccessTools.MethodDelegate<Action<UnitMetadataProvider>>(
        AccessTools.Method(typeof(UnitMetadataProvider), "MirrorLoadoutUnitCosts"));
    private static readonly HashSet<SimpleObjectParameters> applied = AccessTools.StaticFieldRefAccess<HashSet<SimpleObjectParameters>>(
        typeof(PointValueCalculator), "_applied");
    private static readonly MethodInfo dump = AccessTools.Method(typeof(PointValueDump), "Dump");
    private static readonly MethodInfo dumpAmmo = AccessTools.Method(typeof(PointValueAmmoPricer), "DumpCsv");
    private static readonly FieldInfo computedAmmo = AccessTools.Field(typeof(PointValueAmmoPricer), "_computed");
    private static UnitMetadataProvider? pending;
    private static bool taking;
    internal static bool Running => pending != null;

    internal static void Reset()
    {
        pending = null;
        taking = false;
        PreparedPriceHash.Clear();
        PointValueDisplay.Reset();
    }

    internal static IEnumerator LoadUnitMetadata(IEnumerator native)
    {
        taking = true;
        try { yield return native; }
        finally { taking = false; }
    }

    private static MethodBase TargetMethod() => AccessTools.Method(typeof(UnitMetadataProvider)
        .GetNestedTypes(BindingFlags.NonPublic).Single(type => type.Name.StartsWith("<<OnStart>g__LoadUnitMetadata|", StringComparison.Ordinal)), "MoveNext");

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Defer(IEnumerable<CodeInstruction> code)
    {
        var instructions = code.ToList();
        var ensure = instructions.Where(i => i.Calls(AccessTools.Method(typeof(PointValueCalculator), nameof(PointValueCalculator.EnsureValues)))).ToArray();
        var sync = instructions.Where(i => i.Calls(AccessTools.Method(typeof(UnitMetadataProvider), "MirrorLoadoutUnitCosts"))).ToArray();
        if (ensure.Length != 1 || sync.Length != 1) return instructions;
        ensure[0].operand = AccessTools.Method(typeof(DeferredPointValues), nameof(Queue));
        sync[0].opcode = OpCodes.Call;
        sync[0].operand = AccessTools.Method(typeof(DeferredPointValues), nameof(MirrorWhenReady));
        return instructions;
    }

    private static void Queue(Dictionary<(string, string), (int, SimpleObjectParameters)> units)
    {
        if (taking) pending = Singleton<UnitMetadataProvider>.Instance;
        else PointValueCalculator.EnsureValues(units);
    }

    private static void MirrorWhenReady(UnitMetadataProvider provider)
    {
        if (pending != provider) mirror(provider);
    }

    internal static IEnumerator Recalculate()
    {
        if (pending is not { } provider) yield break;
        var total = Stopwatch.StartNew();
        PointValueTuning.EnsureLoaded();
        PointValueRoleScorers.AaWeaponDebug.Clear();
        PointValueRoleScorers.AirLoadoutDebug.Clear();
        PointValueMissileKinematics.ClearMemo();
        int computed = 0, cached = 0;
        int ammoBefore = (int)computedAmmo.GetValue(null);
        var clock = Stopwatch.StartNew();
        TimeSpan frameWait = TimeSpan.Zero;
        TimeSpan setupTime = total.Elapsed;
        foreach (var (key, (_, unit)) in provider.UnitData)
        {
            if (unit?._ini == null || unit.IsLoadoutUnit || key.Item1 == "biologic" || !applied.Add(unit)) continue;
            try { apply(key.Item1, key.Item2, unit, ref computed, ref cached); }
            catch (Exception error) { Debug.LogWarning($"[Quickload] Could not price {key.Item1}/{key.Item2}: {error.Message}"); }
            if (clock.Elapsed.TotalMilliseconds < FrameBudgetMilliseconds) continue;
            TimeSpan yielded = total.Elapsed;
            yield return null;
            frameWait += total.Elapsed - yielded;
            clock.Restart();
        }
        TimeSpan unitTime = total.Elapsed - setupTime - frameWait;
        foreach (string file in Singleton<FileManager>.Instance.GetFiles("ammunition", "*.ini"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name.Length == 0 || name.Equals("damage", StringComparison.OrdinalIgnoreCase)) continue;
            try { PointValueAmmoPricer.GetDetail(name); }
            catch (Exception error) { Debug.LogWarning($"[Quickload] Could not price ammunition {name}: {error.Message}"); }
            if (clock.Elapsed.TotalMilliseconds < FrameBudgetMilliseconds) continue;
            TimeSpan yielded = total.Elapsed;
            yield return null;
            frameWait += total.Elapsed - yielded;
            clock.Restart();
        }
        TimeSpan ammoTime = total.Elapsed - setupTime - unitTime - frameWait;
        mirror(provider);
        PointValueCache.Flush();
        pending = null;
        PointValueDisplay.Publish(provider.UnitData);
        Debug.Log($"[Quickload] Reused {PreparedPriceHash.Clear()} verified unit price hashes.");
        TimeSpan publishTime = total.Elapsed - setupTime - unitTime - ammoTime - frameWait;
        Debug.Log($"[Quickload] Pricing breakdown (ms): setup {setupTime.TotalMilliseconds:F0}, units {unitTime.TotalMilliseconds:F0}, ammo {ammoTime.TotalMilliseconds:F0}, publish and flush {publishTime.TotalMilliseconds:F0}, between frames {frameWait.TotalMilliseconds:F0}.");
        Debug.Log($"[Quickload] Point values ready ({computed} recalculated, {cached} from cache).");
        if ((int)computedAmmo.GetValue(null) > ammoBefore) dumpAmmo.Invoke(null, null);
        if (computed > 0) dump.Invoke(null, [provider.UnitData]);
    }
}
