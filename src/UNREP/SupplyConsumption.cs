using System.Runtime.CompilerServices;
using System.Reflection;
using HarmonyLib;
using SeaPower;

namespace UNREP;

[HarmonyPatch]
internal class SupplyConsumption(Vessel ship)
{
    private static readonly ConditionalWeakTable<Vessel, SupplyConsumption> Ships = new();
    private readonly List<SupplyBin> bins = SupplyInventory.Additional(ship);
    private readonly double fuelStandard = SupplyTuning.Value(ship, "FuelStandard");
    private readonly double foodUse = SupplyTuning.Value(ship, "FoodUse");
    private readonly double waterProduction = SupplyTuning.Value(ship, "WaterProduction");
    private readonly double partsUse = SupplyTuning.Value(ship, "SparePartsUse");
    private readonly double partsPerTeam = SupplyTuning.Value(ship, "SparePartsPerDCTeam");
    private int repairTeams;

    internal static SupplyConsumption For(Vessel ship) => Ships.GetValue(ship, unit => new(unit));
    private static bool Operating(Vessel ship) => ship._canUpdate && !ship.IsDestroyed && !ship.Disabled
        && ship._compartments is { _isSinking: false } && !Globals._inMuseum;

    internal double Rate(SupplyBin bin)
    {
        if (!bin.Enabled || !Operating(ship)) return 0;
        double rate = bin.Key switch
        {
            "Fuel" => -fuelStandard * FuelTelegraph.Load(ship),
            "Food" => -foodUse,
            "Water" => waterProduction,
            "SpareParts" => -partsUse - repairTeams * partsPerTeam,
            _ => 0
        };
        return rate < 0 && bin.Amount <= 0 || rate > 0 && bin.Amount >= bin.Capacity ? 0 : rate;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(Vessel), nameof(Vessel.OnFixedUpdate))]
    private static void BeginTick(Vessel __instance)
    {
        if (Operating(__instance) && !GameTime.IsPaused()) For(__instance).repairTeams = 0;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(Vessel), nameof(Vessel.OnFixedUpdate))]
    private static void EndTick(Vessel __instance)
    {
        if (!Operating(__instance) || GameTime.IsPaused() || GameTime.fixedDeltaTime <= 0) return;
        var consumption = For(__instance);
        double hours = GameTime.fixedDeltaTime / 3600d;
        foreach (var bin in consumption.bins) bin.Change(consumption.Rate(bin) * hours);
    }

    [HarmonyPatch]
    private static class DamageControl
    {
        private static IEnumerable<MethodBase> TargetMethods() => new[]
        {
            "RepairFloodingRate", "RepairFire", "RepairFlooding", "RepairStructure", "RepairSystems"
        }.Select(name => AccessTools.DeclaredMethod(typeof(Compartments), name));

        // Native DC short-circuits after the first successful action in each compartment.
        // Observe it without changing repair speed or requiring any stock to be available.
        private static void Postfix(Compartments __instance, int index, bool __result)
        {
            if (__result && __instance._baseObject is Vessel ship && Operating(ship) && !GameTime.IsPaused())
                For(ship).repairTeams += Math.Max(0, __instance.DamageControlTeamsNumbers[index]);
        }
    }
}
