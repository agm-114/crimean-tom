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
    private readonly double fuelFull = SupplySettings.HourlyRate(ship, "FuelFull");
    private readonly double foodUse = SupplySettings.HourlyRate(ship, "FoodUse");
    private readonly double waterProduction = SupplySettings.HourlyRate(ship, "WaterProduction");
    private readonly double partsUse = SupplySettings.HourlyRate(ship, "SparePartsUse");
    private readonly double partsPerTeam = SupplySettings.HourlyRate(ship, "SparePartsPerDCTeam");
    private int repairTeams;

    internal static SupplyConsumption For(Vessel ship) => Ships.GetValue(ship, unit => new(unit));
    private static bool Operating(Vessel ship) => ship._canUpdate && !ship.IsDestroyed && !ship.Disabled
        && ship._compartments is { _isSinking: false } && !Globals._inMuseum;

    internal double Rate(SupplyBin bin)
    {
        if (!Operating(ship)) return 0;
        double rate = bin.Key switch
        {
            "Fuel" => -fuelFull * FuelLoad(),
            "Food" => -foodUse,
            "Water" => waterProduction,
            "SpareParts" => -partsUse - repairTeams * partsPerTeam,
            _ => 0
        };
        return rate < 0 && bin.Amount <= 0 || rate > 0 && bin.Amount >= bin.Capacity ? 0 : rate;
    }

    private double FuelLoad()
    {
        var command = ship.SpeedCommand.Value;
        double speed = command?.GetDemandedSpeedInKnots(ship) ?? 0;
        if (double.IsNaN(speed) || double.IsInfinity(speed) || speed == 0) return 0;
        // Engine orders matter even before the hull reaches the requested speed.
        if (command is TelegraphSpeed telegraph)
        {
            if (telegraph.telegraph is -1 or 4) return 1;
            if (telegraph.telegraph == 5) return 2;
        }
        var speeds = ship.VP._telegraphVelocities;
        double reverse = Math.Abs(speeds is { Length: > 0 } ? speeds[0] : ship.MaxBackwardSpeedInKnots);
        double flank = speeds is { Length: > 6 } ? speeds[6] : ship.MaxForwardSpeedInKnots;
        double full = speeds is { Length: > 5 } ? speeds[5] : flank * 0.8;
        return FuelLoad(speed, reverse, full, flank);
    }

    internal static double FuelLoad(double speed, double reverse, double full, double flank)
    {
        if (speed < 0) return reverse > 0 ? Math.Clamp(-speed / reverse, 0, 1) : 0;
        if (full <= 0) return 0;
        if (speed <= full || flank <= full) return Math.Clamp(speed / full, 0, 1);
        return 1 + Math.Clamp((speed - full) / (flank - full), 0, 1);
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
