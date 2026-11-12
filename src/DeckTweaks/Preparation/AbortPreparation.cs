using HarmonyLib;
using SeaPower;
using SeapowerUI.ViewModels;

namespace DeckTweaks.Preparation;

[HarmonyPatch]
internal class AbortPreparation(PendingLaunchTask task, TaskPreparation preparation) : IState
{
    private readonly ContextMenuItem abortCommand = task.Commands[0];
    public int Priority { get; set; }

    public void onEnter()
    {
        task._flightDeck._flightDeckTasksToPrioritise.RemoveAll(item => item == task);
        task.AssignedGroundCrew = 0;
        task.Commands.Clear();
        task.Commands.Add(new ContextMenuItem("Cancel Abort", null, new DelegateCommand(_ => preparation.CancelAbort(task))));
        task.FlightDeckTaskLabel = "Aborting";
        task.Info = "Abort queued";
    }

    public void onExit()
    {
        task.AssignedGroundCrew = 0;
        task.Commands.Clear();
        task.Commands.Add(abortCommand);
        task.Info = "";
    }

    public void onUpdate()
    {
        int available = task._flightDeck.GroundCrewCount;
        bool earlier = true;
        foreach (PendingLaunchTask pending in task._flightDeck.FlightDeckTasks.OfType<PendingLaunchTask>())
        {
            if (pending == task) earlier = false;
            else if (pending._stateMachine.CurrentState is HandleAwaitGroundCrewTask or HandleReadyUpTask)
                available -= pending.LaunchCount;
            else if (earlier && pending._stateMachine.CurrentState is AbortPreparation && TaskPreparation.Find(pending) is { } other)
                available -= other.Batch.Aircraft.Count - other.Batch.ReadyCount;
        }
        task.AssignedGroundCrew = Math.Clamp(available, 0, preparation.Batch.Aircraft.Count - preparation.Batch.ReadyCount);
        preparation.UpdateAbort(task);
    }

    public void onFixedUpdate() { }

    [HarmonyPrefix, HarmonyPatch(typeof(PendingLaunchTask), "AbortLaunchFunc")]
    private static bool Begin(PendingLaunchTask __instance)
    {
        if (__instance._stateMachine.CurrentState is AbortPreparation) return false;
        if (TaskPreparation.Find(__instance) is not { } preparation || __instance._isCancelled || __instance.LaunchCount <= 0
            || __instance._stateMachine.CurrentState is not (HandleAwaitDeckSpaceTask or HandleAwaitGroundCrewTask
                or HandleReadyUpTask or HandleAwaitSpawnTask)) return true;
        preparation.BeginAbort(__instance);
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(PendingLaunchTask), nameof(PendingLaunchTask.AllowLaunchFunc))]
    private static bool AllowLaunch(PendingLaunchTask __instance) => CanUseTaskActions(__instance);

    [HarmonyPrefix, HarmonyPatch(typeof(PendingLaunchTask), nameof(PendingLaunchTask.ToggleLaunchAllowedFunc))]
    private static bool ToggleLaunch(PendingLaunchTask __instance) => CanUseTaskActions(__instance);

    [HarmonyPrefix, HarmonyPatch(typeof(PendingLaunchTask), nameof(PendingLaunchTask.PrioritiseTaskFunc))]
    private static bool Prioritise(PendingLaunchTask __instance) => CanUseTaskActions(__instance);

    private static bool CanUseTaskActions(PendingLaunchTask task) => task._stateMachine.CurrentState is not AbortPreparation;
}
