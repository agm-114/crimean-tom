using System.Runtime.CompilerServices;
using HarmonyLib;
using SeaPower;

namespace UNREP;

// Optional API for Deck Tweaks. Only UNREP owns and persists the ship's stock.
[HarmonyPatch]
public static class AviationFuel
{
    private static bool active;
    private static readonly ConditionalWeakTable<PendingLaunchTask, object> DeckPreparations = new();
    private static readonly ConditionalWeakTable<PendingLaunchTask, NativeFuelCharge> NativeCharges = new();

    internal static void Activate() => active = true;

    public static double Available(FlightDeck deck) => Tank(deck)?.Amount ?? double.PositiveInfinity;

    public static double DrawForPreparation(PendingLaunchTask task, double requestedKg)
    {
        if (!double.IsFinite(requestedKg) || requestedKg < 0) return 0;
        DeckPreparations.GetValue(task, _ => new());
        return Tank(task._flightDeck) is { } tank ? Draw(tank, requestedKg) : requestedKg;
    }

    private static SupplyBin? Tank(FlightDeck deck) => active && deck._baseObject is Vessel ship
        && ship.AcceptsOrdersFromPlayer && SupplyTuning.ReserveEnabled("AviationFuel")
        ? SupplyInventory.Additional(ship).FirstOrDefault(bin => bin.IsAviationFuel) : null;

    private static double Draw(SupplyBin tank, double requestedKg)
    {
        double delivered = Math.Min(tank.Amount, requestedKg);
        tank.Change(-delivered);
        return delivered;
    }

    // The native task creates one aircraft per update and fires Launched only after creation succeeds.
    [HarmonyPrefix, HarmonyPatch(typeof(HandlePendingLaunchTask), nameof(HandlePendingLaunchTask.onUpdate))]
    private static bool CanLaunch(PendingLaunchTask ____launchTask)
    {
        var task = ____launchTask;
        if (task.LaunchCount <= 0 || task._ltp._fromScript || task._ltp._airStrike != null || task._ltp._civRoute != null
            || DeckPreparations.TryGetValue(task, out _) || Tank(task._flightDeck) is not { } tank) return true;

        var charge = NativeCharges.GetValue(task, pending => new(pending));
        if (tank.Amount >= charge.RequiredKg) return true;
        task.Info = "Awaiting aviation fuel";
        return false;
    }

    private class NativeFuelCharge
    {
        internal double RequiredKg { get; }

        internal NativeFuelCharge(PendingLaunchTask task)
        {
            var ini = task.VehicleType.Loadouts[task._loadoutIndex].OwnerIni;
            bool helicopter = task.VehicleType._type == VehicleType.Helicopter;
            float fallback = helicopter ? 1000 : 0;
            double fuel = ini?.readValue(helicopter ? "Physics" : "Performance", "MaxFuel", fallback) ?? fallback;
            RequiredKg = double.IsFinite(fuel) && fuel >= 0 ? fuel : fallback;
            task.Launched += (_, _) =>
            {
                if (!DeckPreparations.TryGetValue(task, out _) && Tank(task._flightDeck) is { } tank)
                    Draw(tank, RequiredKg);
            };
        }
    }
}
