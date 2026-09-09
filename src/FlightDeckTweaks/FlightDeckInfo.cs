using HarmonyLib;
using SeaPower;

namespace FlightDeckTweaks;

[HarmonyPatch]
internal static class FlightDeckInfo
{
    [HarmonyPostfix, HarmonyPatch(typeof(LanguageResourceHandler), nameof(LanguageResourceHandler.getText))]
    private static void FormatInfo(string section, string key, ref string __result)
    {
        if (section != "FlightDeck") return;
        __result = key switch
        {
            "ReadyUpStateInfo" => "${groundCrewCount} crew, batch ready in ${minutes}:${seconds}",
            "AwaitDeckSpaceStateInfo" => "${deckSpotCount} deck spots assigned",
            "AwaitGroundCrewStateInfo" or "ReadyUpCompleteInfo" => "",
            _ => __result,
        };
    }

    [HarmonyPostfix, HarmonyPatch(typeof(FlightDeckTask), nameof(FlightDeckTask.FlightDeckTaskLabel), MethodType.Getter)]
    private static void FormatState(FlightDeckTask __instance, ref string __result)
    {
        int count = __instance is PendingLaunchTask task ? task.LaunchCount : 1;
        string state = __instance._stateMachine?.CurrentState switch
        {
            HandleAwaitDeckSpaceTask => "Awaiting Deck Space",
            HandleAwaitGroundCrewTask => "Awaiting Crew",
            HandleReadyUpTask => "Launch Prep",
            HandleAwaitSpawnTask => "Ready to Launch",
            HandlePendingLaunchTask => "Launching",
            _ => __result,
        };
        __result = $"{count}x {state}";
        if (__instance is PendingLaunchTask pending && pending._stateMachine?.CurrentState
            is HandleReadyUpTask or HandleAwaitGroundCrewTask or HandleAwaitDeckSpaceTask)
        {
            int ready = EstimateReadyCount(count, pending._duration,
                pending.VehicleType.Loadouts[pending._loadoutIndex]._readyUpDuration);
            if (ready > 0)
                __result = ready == count ? $"{ready}x Ready" : $"{count - ready}x {state}\n{ready}x Ready";
        }
    }

    // Display completed aircraft-equivalents of work. Native preparation and launching stay batch-based.
    internal static int EstimateReadyCount(int count, float remainingWork, float workPerAircraft) =>
        workPerAircraft > 0
            ? count - (int)Math.Clamp(Math.Ceiling(Math.Max(0, remainingWork) / workPerAircraft), 0, count)
            : 0;

    [HarmonyPostfix, HarmonyPatch(typeof(HandleReadyUpTask), nameof(HandleReadyUpTask.onUpdate))]
    private static void RefreshProgress(PendingLaunchTask ____flightDeckTask) =>
        ____flightDeckTask.NotifyPropertyChanged(nameof(FlightDeckTask.FlightDeckTaskLabel));

    [HarmonyPostfix, HarmonyPatch(typeof(PendingLaunchTask), nameof(PendingLaunchTask.LaunchCount), MethodType.Setter)]
    private static void RefreshCount(PendingLaunchTask __instance) =>
        __instance.NotifyPropertyChanged(nameof(FlightDeckTask.FlightDeckTaskLabel));
}
