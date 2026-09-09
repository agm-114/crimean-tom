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
            "ReadyUpStateInfo" => "${groundCrewCount} crew, ${minutes}:${seconds} remaining",
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
    }

    [HarmonyPostfix, HarmonyPatch(typeof(PendingLaunchTask), nameof(PendingLaunchTask.LaunchCount), MethodType.Setter)]
    private static void RefreshCount(PendingLaunchTask __instance) =>
        __instance.NotifyPropertyChanged(nameof(FlightDeckTask.FlightDeckTaskLabel));
}
