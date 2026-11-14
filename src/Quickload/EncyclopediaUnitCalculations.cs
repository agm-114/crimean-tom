using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using SeaPower;
using Seapower;
using Task = System.Threading.Tasks.Task;

namespace Quickload;

[HarmonyPatch]
internal static class EncyclopediaUnitCalculations
{
    private const int Format = 1;
    private const string FileName = "unit-reference-calculations.json.gz";
    private static readonly Type manager = AccessTools.TypeByName("SeaPower.EncyclopedicDataManager");
    private static readonly MethodInfo weaponEntries = AccessTools.PropertyGetter(manager, "WeaponMuseumEntries");
    private static readonly Dictionary<string, string> output = new(StringComparer.Ordinal);
    private static StartupArchives? archives;
    private static Task<Dictionary<string, string>>? prefetch;
    private static Dictionary<string, string>? cached;
    private static Dictionary<string, MuseumWeaponEntry>? indexed;
    private static readonly Dictionary<string, string?> owners = new(StringComparer.Ordinal);
    private static int entryCount;
    private static int sharedHits;

    internal static void Start(StartupArchives source)
    {
        archives = source;
        prefetch = StartupSegments.Read(source, FileName, Format, Read);
    }

    internal static void Finish()
    {
        if (archives is { } source && output.Count > 0)
        {
            var records = new Dictionary<string, string>(output, StringComparer.Ordinal);
            StartupSegments.Save(source, FileName, Format, records, pair => pair.Key.Split('/')[0], Read,
                (stream, data) => StartupArchives.WriteJson(stream, data, JsonSerializer.CreateDefault()));
        }
        if (sharedHits > 0) UnityEngine.Debug.Log($"[Quickload] Reused {sharedHits:N0} unit reference calculations from mod cache chunks.");
        output.Clear();
        cached = null;
        prefetch = null;
        archives = null;
        indexed = null;
        owners.Clear();
        entryCount = 0;
        sharedHits = 0;
    }

    private static Dictionary<string, string>? Read(Stream stream) =>
        StartupArchives.ReadJson<Dictionary<string, string>>(stream, JsonSerializer.CreateDefault());

    private static IEnumerable<MethodBase> TargetMethods() =>
        new[] { "BuildAmmunitionText", "BuildDispersionText" }.Select(name => AccessTools.Method(manager, name));

    private static bool Prefix(MethodBase __originalMethod, object? __instance, object[] __args, out string? __state, ref string __result)
    {
        __state = null;
        if (archives is not { } source || !StartupLoading.DeferredEncyclopedia) return true;
        cached ??= prefetch is { IsCompletedSuccessfully: true } loaded ? loaded.Result : null;
        if (__instance == null) return true;
        if (__args[0] is not IEnumerable<string> references) return true;
        var entries = (ICollection<MuseumWeaponEntry>)weaponEntries.Invoke(__instance, null);
        UpdateIndex(entries);
        var inputs = new List<string>();
        string? layer = null;
        foreach (string reference in references)
        {
            indexed!.TryGetValue(reference, out var entry);
            layer ??= Owner(source, reference);
            inputs.Add(reference);
            inputs.Add(entry?.DisplayName ?? "");
            inputs.Add(entry?.GunDispersionMrad.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "");
        }
        if (inputs.Count == 0 || layer == null) return true;
        inputs.Add(Singleton<LanguageResourceHandler>.Instance.getText("MeasurmentUnits", "Milliradian"));
        inputs.Add(System.Globalization.CultureInfo.CurrentCulture.Name);
        string key = layer + "/" + StartupArchives.Digest(__originalMethod.Name + "\0" + string.Join("\0", inputs));
        if (output.TryGetValue(key, out string live))
        {
            __result = live;
            return false;
        }
        if (cached?.TryGetValue(key, out live) == true)
        {
            __result = live;
            sharedHits++;
            return false;
        }
        __state = key;
        return true;
    }

    private static void Postfix(string? __state, string __result)
    {
        if (__state != null) output.TryAdd(__state, __result);
    }

    [HarmonyPatch]
    private static class AntiAirRange
    {
        private static MethodBase TargetMethod() => AccessTools.Method(manager, "BuildAaRange");

        private static bool Prefix(object __instance, IEnumerable<string> __0, out string? __state, ref Length __result)
        {
            __state = null;
            if (archives is not { } source || !StartupLoading.DeferredEncyclopedia) return true;
            var entries = (ICollection<MuseumWeaponEntry>)weaponEntries.Invoke(__instance, null);
            UpdateIndex(entries);
            var inputs = new List<string>();
            string? layer = null;
            foreach (string reference in __0)
            {
                indexed!.TryGetValue(reference, out var entry);
                layer ??= Owner(source, reference);
                inputs.Add(reference);
                inputs.Add(entry?.CanAttackAir == true ? entry.GunAaRange.InMetres.ToString("R", System.Globalization.CultureInfo.InvariantCulture) : "");
            }
            if (layer == null || inputs.Count == 0) return true;
            string key = layer + "/" + StartupArchives.Digest("BuildAaRange\0" + string.Join("\0", inputs));
            cached ??= prefetch is { IsCompletedSuccessfully: true } loaded ? loaded.Result : null;
            if (output.TryGetValue(key, out var raw) || cached?.TryGetValue(key, out raw) == true)
            {
                if (float.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float range))
                {
                    if (!output.ContainsKey(key)) sharedHits++;
                    __result = Length.FromMetres(range);
                    return false;
                }
            }
            __state = key;
            return true;
        }

        private static void Postfix(string? __state, Length __result)
        {
            if (__state != null) output.TryAdd(__state, __result.InMetres.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static string? Owner(StartupArchives source, string reference)
    {
        if (owners.TryGetValue(reference, out var owner)) return owner;
        string? path = Singleton<FileManager>.Instance.GetFile("ammunition/" + reference + ".ini");
        owner = path == null ? null : source.LayerFor(path);
        owners[reference] = owner;
        return owner;
    }

    private static void UpdateIndex(ICollection<MuseumWeaponEntry> entries)
    {
        if (indexed != null && entryCount == entries.Count) return;
        indexed = new(StringComparer.Ordinal);
        foreach (var entry in entries)
            if (entry.IniRef != null) indexed.TryAdd(entry.IniRef, entry);
        entryCount = entries.Count;
    }
}
