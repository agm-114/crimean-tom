using System.Runtime.CompilerServices;
using HarmonyLib;
using SeaPower;

namespace DeckTweaks.Preparation;

[HarmonyPatch]
internal class RecoveryFlight
{
    private const string SaveKey = "FDTRecoveryFlight";
    private static readonly ConditionalWeakTable<ObjectBase, RecoveryFlight> Aircraft = new();
    private static readonly ConditionalWeakTable<UnitFormation, RecoveryFlight> Formations = new();
    private Guid id;
    private UnitFormation? formation;

    internal static Guid Identify(ObjectBase unit)
    {
        if (Aircraft.TryGetValue(unit, out var flight)) return flight.id;
        if (unit.Formation is { } group && group.Stations.Count > 1)
            return Formations.GetValue(group, _ => new() { id = Guid.NewGuid() }).id;
        return Guid.Empty;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(Order), nameof(Order.setOrder))]
    private static void RememberFormation(Order.Type type, ObjectBase ____baseUnit)
    {
        ObjectBase unit = ____baseUnit;
        if (!unit.IsAirUnit || !unit.AcceptsOrdersFromPlayer || !unit.IsControllable) return;
        if (type != Order.Type.ReturnToBase)
        {
            Aircraft.Remove(unit);
            return;
        }
        if (unit.Formation is not { } group || group.Stations.Count <= 1) return;
        // Return-to-base detaches wingmen. Remember the flight before the first aircraft peels off.
        Guid id = Formations.GetValue(group, _ => new() { id = Guid.NewGuid() }).id;
        foreach (var station in group.Stations)
        {
            if (station.UnitObject is not { } member || member == null || !member.IsAirUnit) continue;
            RecoveryFlight flight = Aircraft.GetOrCreateValue(member);
            flight.id = id;
            flight.formation = group;
        }
    }

    [HarmonyPostfix, HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.Formation), MethodType.Setter)]
    private static void FormationChanged(ObjectBase __instance, UnitFormation value)
    {
        if (!Aircraft.TryGetValue(__instance, out var flight) || value == flight.formation) return;
        if (value != null || __instance.CurrentOrder?.Value?.OrderType != Order.Type.ReturnToBase)
            Aircraft.Remove(__instance);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.SaveStateToFile))]
    private static void Save(ObjectBase __instance, IniHandler ini, string sectionName, bool isCampaignStateSave)
    {
        if (!isCampaignStateSave && Aircraft.TryGetValue(__instance, out var flight) && flight.id != Guid.Empty)
            ini.writeValue(sectionName, SaveKey, flight.id.ToString("N"));
    }

    [HarmonyPostfix, HarmonyPatch(typeof(ObjectBase), nameof(ObjectBase.LoadStateFromFile))]
    private static void Load(ObjectBase __instance, IniHandler ini, string sectionName, bool isCampaignStateSave)
    {
        if (!isCampaignStateSave && ini.Data.TryGetValue(sectionName, out var section)
            && section.TryGetValue(SaveKey, out string value) && Guid.TryParseExact(value, "N", out Guid id))
            Aircraft.GetOrCreateValue(__instance).id = id;
    }
}
