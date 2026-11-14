using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace SeaPower.LoadingProfiler;

internal static class PointValueTiming
{
    private static readonly Dictionary<string, Sample> samples = [];
    private static bool active;
    private static string? storedHash;
    private static string? currentHash;
    private static int unitHits, unitMisses, ammoHits, ammoMisses;
    private static readonly ulong[] firstHashSteps = new ulong[4];
    private static int hashSteps;
    private static readonly MethodInfo appendHash = AccessTools.Method(typeof(PointValueCache), "Fnv1a");
    private static readonly Func<ulong, string, ulong> append = AccessTools.MethodDelegate<Func<ulong, string, ulong>>(appendHash);

    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(PointValueCalculator), "EnsureValues"),
            new HarmonyMethod(typeof(PointValueTiming), nameof(Begin)), finalizer: new(typeof(PointValueTiming), nameof(Finish)));
        (Type Type, string Method)[] targets =
        [
            (typeof(PointValueCache), "ComputeGlobalHash"), (typeof(PointValueCache), "HashIni"),
            (typeof(PointValueCache), "EnsureLoaded"), (typeof(PointValueCache), "Flush"),
            (typeof(PointValueCalculator), "Compute"), (typeof(PointValueAmmoPricer), "EnsureCatalog"),
            (typeof(PointValueAmmoPricer), "Compute"), (AccessTools.TypeByName("SeaPower.PointValueDump"), "Dump")
        ];
        foreach (var (type, name) in targets)
        {
            var method = AccessTools.GetDeclaredMethods(type).SingleOrDefault(item => item.Name == name);
            if (method != null) harmony.Patch(method, new HarmonyMethod(typeof(PointValueTiming), nameof(Start)), finalizer: new(typeof(PointValueTiming), nameof(End)));
        }
        foreach (string name in new[] { "ComputeGlobalHash", "TryGet", "TryGetAmmoPrice" })
            harmony.Patch(AccessTools.Method(typeof(PointValueCache), name), postfix: new(typeof(PointValueTiming), name));
        harmony.Patch(AccessTools.Method(typeof(PointValueCache), "ComputeGlobalHash"),
            transpiler: new(typeof(PointValueTiming), nameof(TraceHashInputs)));
    }

    private static void Begin()
    {
        samples.Clear();
        hashSteps = 0;
        currentHash = null;
        storedHash = null;
        unitHits = unitMisses = ammoHits = ammoMisses = 0;
        try
        {
            string path = Path.Combine(Application.persistentDataPath, "cache", "taskforce_pointvalues.json");
            if (File.Exists(path))
            {
                using var text = File.OpenText(path);
                using var reader = new JsonTextReader(text);
                while (reader.Read() && reader.Depth <= 1)
                {
                    if (reader.TokenType != JsonToken.PropertyName || (string?)reader.Value != "GlobalHash") continue;
                    storedHash = reader.ReadAsString();
                    break;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        active = true;
    }

    private static void Finish()
    {
        active = false;
        TraceLog.Write("point-value-pass", new
        {
            storedHash, currentHash, unitHits, unitMisses, ammoHits, ammoMisses,
            tuningAndSystemHashes = firstHashSteps.Take(Math.Min(hashSteps, firstHashSteps.Length))
                .Select(hash => hash.ToString("x16")).ToArray(),
            ammunitionHashInputs = Math.Max(0, hashSteps - firstHashSteps.Length),
            timings = samples.Select(pair => new
            {
                name = pair.Key, calls = pair.Value.Count, seconds = pair.Value.Ticks / (double)Stopwatch.Frequency
            }).ToArray()
        });
    }

    private static void Start(out long __state) => __state = active ? Stopwatch.GetTimestamp() : 0;

    private static void End(MethodBase __originalMethod, long __state)
    {
        if (__state == 0) return;
        string key = $"{__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}";
        if (!samples.TryGetValue(key, out var sample)) samples.Add(key, sample = new());
        sample.Count++;
        sample.Ticks += Stopwatch.GetTimestamp() - __state;
    }

    private static void ComputeGlobalHash(string __result)
    {
        if (active) currentHash = __result;
    }

    private static IEnumerable<CodeInstruction> TraceHashInputs(IEnumerable<CodeInstruction> code)
    {
        var trace = AccessTools.Method(typeof(PointValueTiming), nameof(AppendHash));
        foreach (var instruction in code)
        {
            if (instruction.Calls(appendHash)) instruction.operand = trace;
            yield return instruction;
        }
    }

    private static ulong AppendHash(ulong previous, string value)
    {
        ulong hash = append(previous, value);
        if (active && hashSteps++ < firstHashSteps.Length) firstHashSteps[hashSteps - 1] = hash;
        return hash;
    }

    private static void TryGet(bool __result)
    {
        if (!active) return;
        if (__result) unitHits++;
        else unitMisses++;
    }

    private static void TryGetAmmoPrice(bool __result)
    {
        if (!active) return;
        if (__result) ammoHits++;
        else ammoMisses++;
    }

    private class Sample
    {
        internal int Count;
        internal long Ticks;
    }
}
