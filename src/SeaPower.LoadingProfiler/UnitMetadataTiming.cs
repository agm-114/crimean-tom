using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace SeaPower.LoadingProfiler;

internal static class UnitMetadataTiming
{
    private static readonly string[] phases =
    [
        "Find unit files", "Preload system libraries", "Parse units on workers",
        "Finalize units", "Find unreferenced ammunition", "Parse ammunition on workers",
        "Validate metadata", "Apply point values", "Publish metadata"
    ];
    private static int phase = -1;
    private static long started;
    private static int startFrame;
    private static bool boundariesInstalled;

    internal static void Install(Harmony harmony)
    {
        var iterator = typeof(UnitMetadataProvider).GetNestedTypes(BindingFlags.NonPublic)
            .SingleOrDefault(type => type.Name.StartsWith("<<OnStart>g__LoadUnitMetadata|", StringComparison.Ordinal));
        var moveNext = iterator == null ? null : AccessTools.Method(iterator, "MoveNext");
        if (moveNext == null)
        {
            TraceLog.Write("missing-hook", new { name = "UnitMetadataProvider.LoadUnitMetadata.MoveNext" });
            return;
        }
        harmony.Patch(moveNext, new HarmonyMethod(typeof(UnitMetadataTiming), nameof(Begin)),
            transpiler: new HarmonyMethod(typeof(UnitMetadataTiming), nameof(AddBoundaries)),
            finalizer: new HarmonyMethod(typeof(UnitMetadataTiming), nameof(End)));
        UnitWorkerTiming.Install(harmony);
    }

    private static IEnumerable<CodeInstruction> AddBoundaries(IEnumerable<CodeInstruction> code)
    {
        boundariesInstalled = false;
        var instructions = code.ToList();
        int[] Find(string type, string method) => instructions
            .Select((instruction, index) => (instruction, index))
            .Where(item => item.instruction.operand is MethodInfo target && target.DeclaringType?.Name == type
                && target.Name == method && (item.instruction.opcode == OpCodes.Call || item.instruction.opcode == OpCodes.Callvirt))
            .Select(item => item.index).ToArray();

        var tasks = Find("Task", "Run").Concat(Find("UnitDispatch", "RunUnitWorker"))
            .Concat(Find("UnitDispatch", "RunAmmoWorker")).OrderBy(index => index).ToArray();
        var completed = Find("Task", "get_IsFaulted");
        var libraries = Find("SystemLibraries", "PreloadAll");
        var ammo = Find("UnitMetadataProvider", "CollectUnreferencedAmmunition");
        var points = Find("PointValueCalculator", "EnsureValues");
        var publish = Find("UnitMetadataProvider", "MirrorLoadoutUnitCosts");
        if (tasks.Length != 2 || completed.Length != 2 || libraries.Length != 1 || ammo.Length != 1
            || points.Length != 1 || publish.Length != 1)
        {
            TraceLog.Write("missing-hook", new { name = "UnitMetadataProvider.LoadUnitMetadata phase boundaries" });
            return instructions;
        }

        int[] boundaries = [libraries[0], tasks[0], completed[0], ammo[0], tasks[1], completed[1], points[0], publish[0]];
        if (!boundaries.SequenceEqual(boundaries.Distinct().OrderBy(index => index)))
        {
            TraceLog.Write("missing-hook", new { name = "UnitMetadataProvider.LoadUnitMetadata phase order" });
            return instructions;
        }

        var change = AccessTools.Method(typeof(UnitMetadataTiming), nameof(Change));
        for (int index = boundaries.Length - 1; index >= 0; index--)
        {
            int offset = boundaries[index];
            var marker = new CodeInstruction(OpCodes.Ldc_I4, index + 1);
            marker.MoveLabelsFrom(instructions[offset]);
            instructions.InsertRange(offset, [marker, new CodeInstruction(OpCodes.Call, change)]);
        }
        boundariesInstalled = true;
        TraceLog.Write("install-hook", new { name = "UnitMetadataProvider.LoadUnitMetadata phase timings" });
        return instructions;
    }

    private static void Begin()
    {
        if (boundariesInstalled && phase < 0) Change(0);
    }

    private static void End(bool __result, Exception? __exception)
    {
        if (phase >= 0 && (!__result || __exception != null)) Change(-1);
    }

    private static void Change(int next)
    {
        long now = Stopwatch.GetTimestamp();
        int frame = Time.frameCount;
        if (phase >= 0)
            TraceLog.Write("unit-metadata-phase", new
            {
                name = phases[phase],
                seconds = (now - started) / (double)Stopwatch.Frequency,
                frames = frame - startFrame
            });
        if (phase == 2) UnitWorkerTiming.Finish();
        if (next == 2) UnitWorkerTiming.Start();
        phase = next;
        started = Stopwatch.GetTimestamp();
        startFrame = frame;
    }
}
