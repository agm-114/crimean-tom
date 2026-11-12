using HarmonyLib;
using SeaPower;

namespace DeckTweaks.Preparation;

internal static class DeckFuel
{
    // Resolve when a deck task is created, after either plugin load order has completed.
    private static readonly Lazy<Func<PendingLaunchTask, double, double>?> Draw =
        new(() => Bind<Func<PendingLaunchTask, double, double>>("DrawForPreparation"));
    private static readonly Lazy<Func<FlightDeck, double>?> Stock =
        new(() => Bind<Func<FlightDeck, double>>("Available"));

    internal static double Available(FlightDeck deck) => Stock.Value?.Invoke(deck) ?? double.PositiveInfinity;

    internal static Func<double, double>? Attach(PendingLaunchTask task)
    {
        if (Draw.Value is not { } draw) return null;
        draw(task, 0);
        return kg => draw(task, kg);
    }

    private static T? Bind<T>(string name) where T : Delegate =>
        AccessTools.TypeByName("UNREP.AviationFuel")?.GetMethod(name)?.CreateDelegate(typeof(T)) as T;
}
