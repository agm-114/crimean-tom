using System.Collections;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SeaPower;

namespace Quickload;

[HarmonyPatch]
internal static class EncyclopediaSensors
{
    private static readonly Type manager = EncyclopediaLoading.ManagerType;
    private static readonly MethodInfo getEntries = AccessTools.PropertyGetter(manager, "SensorMuseumEntries");
    private static readonly MethodInfo setup = AccessTools.Method(manager, "SetupSensorDisplayObject");
    private static readonly PropertyInfo[] radarStats = typeof(MuseumRadarEntry).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(property => property.CanWrite).ToArray();
    private static HashSet<string>? selected;
    internal static bool Available { get; private set; }

    private static readonly MethodInfo load = AccessTools.Method(manager, "LoadSensors");

    private static MethodBase TargetMethod() => AccessTools.EnumeratorMoveNext(load);
    internal static IEnumerator Load(object instance, object display) => (IEnumerator)load.Invoke(instance, [display]);

    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> FilterSensorList(IEnumerable<CodeInstruction> code)
    {
        var instructions = code.ToList();
        var enumerate = AccessTools.Method(typeof(IniHandler), nameof(IniHandler.getNamesOfAllSections));
        var calls = instructions.Where(instruction => instruction.Calls(enumerate)).ToArray();
        if (calls.Length != 3) return instructions;
        calls[^1].opcode = OpCodes.Call;
        calls[^1].operand = AccessTools.Method(typeof(EncyclopediaSensors), nameof(Sections));
        Available = true;
        return instructions;
    }

    private static List<string> Sections(IniHandler ini) => selected == null ? ini.getNamesOfAllSections()
        : ini.getNamesOfAllSections().Where(selected.Contains).ToList();

    internal static bool TryRefreshRadars(object instance, object display, IEnumerator native,
        MuseumSensorEntry[] previous, HashSet<string> changed)
    {
        using var disposable = native as IDisposable;
        var entries = (ObservableCollection<MuseumSensorEntry>)getEntries.Invoke(instance, null);
        selected = changed;
        try { native.MoveNext(); }
        finally { selected = null; }
        var rebuilt = entries.ToDictionary(entry => entry.IniRef, StringComparer.Ordinal);
        if (rebuilt.Count != changed.Count || rebuilt.Values.Any(entry => entry is not MuseumRadarEntry)) return false;
        // Unit pages and weapon mounts can hold references to the original sensor objects.
        entries.Clear();
        foreach (var entry in previous)
        {
            if (rebuilt.TryGetValue(entry.IniRef, out var updated))
            {
                foreach (var property in radarStats) property.SetValue(entry, property.GetValue(updated));
                entry.ModSource = updated.ModSource;
            }
            entries.Add(entry);
        }
        setup.Invoke(instance, [display]);
        return true;
    }
}
