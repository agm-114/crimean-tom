using System.Runtime.CompilerServices;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace FlightDeckTweaks.Preparation;

[HarmonyPatch]
internal class TaskPreparation(PreparationBatch batch)
{
    private static readonly ConditionalWeakTable<PendingLaunchTask, TaskPreparation> Tasks = new();
    private static bool unavailable;
    internal PreparationBatch Batch { get; } = batch;
    internal StoreReservation Reservation { get; private set; } = new();
    internal static TaskPreparation? Find(PendingLaunchTask task) => Tasks.TryGetValue(task, out var preparation) ? preparation : null;
    internal static Loadout Selected(PendingLaunchTask task) => task.VehicleType.Loadouts[task._loadoutIndex];

    private static double WorkSpeed(PendingLaunchTask task)
    {
        FlightDeckTimingsMode mode = Singleton<OptionsManager>.Instance.FlightDeckTimingsMode;
        return mode == FlightDeckTimingsMode.Cheater || !DM_TestTools._allowAircraftReadyUpTime ? double.PositiveInfinity
            : Globals._multipliers[mode] / PreparationCatalog.OperationTimeMultiplier(task);
    }

    internal static PreparationLoadout? Plan(PendingLaunchTask task, Loadout loadout)
    {
        if (unavailable) return null;
        try { return PreparationCatalog.Loadout(task.VehicleType, loadout); }
        catch (Exception exception)
        {
            unavailable = true;
            Debug.LogError($"[Flight Deck Tweaks] Preparation configuration failed; new tasks use native timing. {exception}");
            return null;
        }
    }

    internal static TaskPreparation Attach(PendingLaunchTask task, PreparationBatch batch, StoreReservation? reservation = null)
    {
        TaskPreparation preparation = new(batch) { Reservation = reservation ?? new() };
        Tasks.Remove(task);
        Tasks.Add(task, preparation);
        preparation.Sync(task);
        return preparation;
    }

    internal void Transfer(PendingLaunchTask replacement, PreparationLoadout target)
    {
        Batch.Retarget(target);
        TaskPreparation preparation = Attach(replacement, Batch);
        preparation.UpdateReservation(replacement);
    }

    private void UpdateReservation(PendingLaunchTask task)
    {
        StoreReservation next = StoreReservation.Excess(Batch, Batch.Aircraft[0].Target);
        Reservation.Replace(task._flightDeck, next);
        Reservation = next;
    }

    private void Sync(PendingLaunchTask task)
    {
        task._duration = (float)(Batch.RemainingWork * PreparationCatalog.OperationTimeMultiplier(task));
        task.NotifyPropertyChanged(nameof(FlightDeckTask.FlightDeckTaskLabel));
    }

    internal string Label(PendingLaunchTask task, string waiting)
    {
        int crews = task._stateMachine.CurrentState is HandleReadyUpTask ? Math.Max(0, task.AssignedGroundCrew) : 0;
        return string.Join("\n", Batch.Aircraft.Select(aircraft => aircraft.Ready ? "Ready"
            : crews-- > 0 ? aircraft.Activity : waiting).GroupBy(activity => activity)
            .Select(group => $"{group.Count()}x {group.Key}"));
    }

    internal string Describe(PendingLaunchTask task)
    {
        int crews = task._stateMachine.CurrentState is HandleReadyUpTask ? Math.Max(0, task.AssignedGroundCrew) : 0;
        string waiting = task._stateMachine.CurrentState is HandleAwaitDeckSpaceTask ? "Awaiting deck space" : "Awaiting crew";
        double speed = WorkSpeed(task);
        return string.Join("\n\n", Batch.Aircraft.Select((aircraft, index) =>
            $"Aircraft {index + 1}\n{aircraft.Describe(speed, !aircraft.Ready && crews-- > 0, waiting, PreparationCatalog.WeaponName)}"));
    }

    [HarmonyPostfix, HarmonyPatch(typeof(PendingLaunchTask), MethodType.Constructor,
        [typeof(FlightDeck), typeof(VehicleTypeOnBoard), typeof(Loadout), typeof(Squadron), typeof(string), typeof(LaunchTaskParameters), typeof(float), typeof(bool)])]
    private static void Created(PendingLaunchTask __instance)
    {
        if (__instance.LaunchCount <= 0 || __instance._ltp._fromSave || __instance._ltp._fromScript
            || __instance._ltp._airStrike != null || __instance._ltp._civRoute != null
            || !__instance._flightDeck._baseObject.AcceptsOrdersFromPlayer) return;
        if (Plan(__instance, Selected(__instance)) is not { } target) return;
        PreparationBatch batch = new(__instance.LaunchCount, target);
        if (__instance._ltp._skipReadyUp || !DM_TestTools._allowAircraftReadyUpTime) batch.Complete();
        Attach(__instance, batch);
    }

    [HarmonyPrefix, HarmonyPatch(typeof(HandleReadyUpTask), nameof(HandleReadyUpTask.onUpdate))]
    private static bool UpdatePreparation(PendingLaunchTask ____flightDeckTask)
    {
        PendingLaunchTask task = ____flightDeckTask;
        if (Find(task) is not { } preparation) return true;
        double speed = WorkSpeed(task);
        task.UpdateAssignedGroundCrew();
        if (double.IsPositiveInfinity(speed))
            preparation.Batch.Complete();
        else preparation.Batch.Advance(GameTime.deltaTime * speed, Math.Max(0, task.AssignedGroundCrew));
        preparation.UpdateReservation(task);
        preparation.Sync(task);
        double seconds = preparation.Batch.EstimatedSeconds(task.AssignedGroundCrew) / speed;
        task.Info = double.IsInfinity(seconds) || double.IsNaN(seconds) ? "Awaiting crew"
            : $"{task.AssignedGroundCrew} crew, batch ready in {(int)(seconds / 60)}:{(int)(seconds % 60):00}";
        return false;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(HandleReadyUpTask), nameof(HandleReadyUpTask.IsFinished), MethodType.Getter)]
    private static void Finished(PendingLaunchTask ____flightDeckTask, ref bool __result)
    {
        if (Find(____flightDeckTask) is { } preparation) __result = preparation.Batch.ReadyCount == preparation.Batch.Aircraft.Count;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(PendingLaunchTask), nameof(PendingLaunchTask.LaunchCount), MethodType.Setter)]
    private static void Launched(PendingLaunchTask __instance)
    {
        if (Find(__instance) is not { } preparation) return;
        int removed = preparation.Batch.Aircraft.Count - __instance.LaunchCount;
        if (removed > 0) preparation.Batch.Aircraft.RemoveRange(0, removed);
        preparation.Sync(__instance);
    }

    [HarmonyPrefix, HarmonyPatch(typeof(FlightDeck), nameof(FlightDeck.abortLaunchTask))]
    private static void Abort(FlightDeck __instance, Guid uid)
    {
        if (__instance.FlightDeckTasks.OfType<PendingLaunchTask>().FirstOrDefault(task => task._uid == uid) is { } task)
            Release(task);
    }

    [HarmonyPrefix, HarmonyPatch(typeof(FlightDeck), nameof(FlightDeck.abortAllLaunchTasks))]
    private static void AbortAll(FlightDeck __instance)
    {
        foreach (PendingLaunchTask task in __instance.FlightDeckTasks.OfType<PendingLaunchTask>()) Release(task);
    }

    private static void Release(PendingLaunchTask task)
    {
        if (Find(task) is not { } preparation) return;
        preparation.Reservation.Replace(task._flightDeck, new());
        preparation.Reservation = new();
        Tasks.Remove(task);
    }
}
