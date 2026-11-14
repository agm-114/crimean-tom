using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SeaPower;
using Task = System.Threading.Tasks.Task;

namespace Quickload;

[HarmonyPatch]
internal static class EncyclopediaWeapons
{
    private const double BudgetMilliseconds = EncyclopediaLoading.FrameBudgetMilliseconds;
    private static readonly Type managerType = AccessTools.TypeByName("SeaPower.EncyclopedicDataManager");
    private static readonly MethodInfo load = AccessTools.Method(managerType, "LoadWeapons");
    private static readonly MethodInfo setup = AccessTools.Method(managerType, "SetupWeaponDisplayObject");
    private static readonly MethodInfo getEntries = AccessTools.PropertyGetter(managerType, "WeaponMuseumEntries");
    private static readonly MethodBase moveNext = AccessTools.EnumeratorMoveNext(load);
    private static readonly MethodInfo clone = AccessTools.Method(typeof(object), "MemberwiseClone");
    private static List<string>? files;
    private static bool batching;
    private static bool sliced;
    private static StartupArchives? archives;
    private static Task<Prepared>? snapshots;

    internal static void Start(StartupArchives source)
    {
        archives = source;
        snapshots = Read(source);
    }

    internal static void Reset()
    {
        archives = null;
        snapshots = null;
    }

    private static string FileName(StartupArchives.InputIndex inputs, string displayContext) =>
        $"weapon-reference-{StartupArchives.Digest(inputs.WeaponInputs + displayContext)}.json.gz";
    private static Dictionary<string, Record>? ReadSegment(Stream stream) =>
        StartupArchives.ReadJson<Dictionary<string, Record>>(stream, EncyclopediaCache.ReferenceSerializer());

    private static async Task<Prepared> Read(StartupArchives source)
    {
        var inputs = await source.Inputs.ConfigureAwait(false);
        var records = await StartupSegments.Read(source, FileName(inputs, source.ReferenceDisplayContext), 1, ReadSegment).ConfigureAwait(false);
        return new(inputs, records);
    }

    private static MethodBase TargetMethod() => moveNext;

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> SplitLoop(IEnumerable<CodeInstruction> code)
    {
        var instructions = code.ToList();
        var getFiles = AccessTools.Method(typeof(FileManager), nameof(FileManager.GetFiles),
            [typeof(string), typeof(string), typeof(LayerKind)]);
        var enumerate = instructions.Where(instruction => instruction.Calls(getFiles)).ToArray();
        var clear = instructions.Where(instruction => instruction.operand is MethodInfo method
            && method.Name == "Clear" && method.DeclaringType?.GenericTypeArguments.Contains(typeof(MuseumWeaponEntry)) == true).ToArray();
        var finish = instructions.Where(instruction => instruction.Calls(setup)).ToArray();
        sliced = enumerate.Length == 1 && clear.Length == 1 && finish.Length == 1;
        if (!sliced) return instructions;
        Replace(enumerate[0], nameof(GetFiles));
        Replace(clear[0], nameof(Clear));
        Replace(finish[0], nameof(Setup));
        return instructions;
    }

    private static void Replace(CodeInstruction instruction, string method)
    {
        instruction.opcode = OpCodes.Call;
        instruction.operand = AccessTools.Method(typeof(EncyclopediaWeapons), method);
    }

    private static List<string> GetFiles(FileManager manager, string path, string pattern, LayerKind kind) =>
        files ?? manager.GetFiles(path, pattern, kind);

    private static void Clear(System.Collections.ObjectModel.Collection<MuseumWeaponEntry> entries)
    {
        if (!batching) entries.Clear();
    }

    private static void Setup(object manager, System.Collections.ObjectModel.ObservableCollection<MuseumEntry> entries)
    {
        if (!batching) setup.Invoke(manager, [entries]);
    }

    internal static IEnumerator Wrap(object manager, object display, IEnumerator original)
    {
        if (!sliced || !StartupLoading.DeferredEncyclopedia)
        {
            yield return original;
            yield break;
        }
        using var template = original as IDisposable;
        var entries = (IList)getEntries.Invoke(manager, null);
        entries.Clear();
        var source = archives;
        var prefetch = snapshots;
        while (prefetch is { IsCompleted: false }) yield return null;
        var available = prefetch is { IsCompletedSuccessfully: true } ? prefetch.Result : null;
        var captured = new Dictionary<string, Record>(StringComparer.OrdinalIgnoreCase);
        var managerFiles = Singleton<FileManager>.Instance;
        var ammunition = managerFiles.GetFiles("ammunition", "*.ini");
        var clock = Stopwatch.StartNew();
        var one = new List<string>(1) { "" };
        int restored = 0;
        foreach (string file in ammunition)
        {
            string? disk = managerFiles.GetFile(file);
            string? key = null;
            string? hash = null;
            if (source != null && available != null && disk != null && available.Inputs.Files.TryGetValue(StartupArchives.Normalize(disk), out var input)
                && !input.HasIncludes)
            {
                IniHandler.GetModifierFilePaths(disk, out var extend, out var prepend);
                if (extend.Count == 0 && prepend.Count == 0)
                {
                    key = source.PortablePath(disk);
                    hash = input.Hash;
                }
            }
            if (key != null && available!.Records.TryGetValue(key, out var saved) && saved.Hash == hash)
            {
                EncyclopediaCache.BindMissile(saved.Entry);
                entries.Add(saved.Entry);
                restored++;
            }
            else
            {
                int before = entries.Count;
                one[0] = file;
                // Other loads can use the native iterator between frames.
                var item = (IEnumerator)clone.Invoke(original, null);
                using var disposable = item as IDisposable;
                batching = true;
                files = one;
                try { item.MoveNext(); }
                finally
                {
                    batching = false;
                    files = null;
                }
                if (key != null && entries.Count == before + 1 && entries[before] is MuseumWeaponEntry entry)
                {
                    var copy = (MuseumWeaponEntry)clone.Invoke(entry, null);
                    copy.Nations = new(entry.Nations);
                    captured[key] = new(hash!, copy);
                }
            }
            if (clock.Elapsed.TotalMilliseconds < BudgetMilliseconds) continue;
            yield return null;
            clock.Restart();
        }
        if (source != null && available != null && captured.Count != 0)
            StartupSegments.Save(source, FileName(available.Inputs, source.ReferenceDisplayContext), 1, captured, pair => source.LayerFor(source.LocalPath(pair.Key)),
                ReadSegment, (stream, data) => StartupArchives.WriteJson(stream, data, EncyclopediaCache.ReferenceSerializer()));
        UnityEngine.Debug.Log($"[Quickload] Reused {restored:N0} Encyclopedia weapons from mod cache chunks.");
        setup.Invoke(manager, [display]);
        yield return null;
    }

    private record Record(string Hash, MuseumWeaponEntry Entry);
    private record Prepared(StartupArchives.InputIndex Inputs, Dictionary<string, Record> Records);
}
