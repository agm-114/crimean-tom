using System.Runtime.CompilerServices;
using HarmonyLib;
using SeaPower;
using UnityEngine;

namespace DeckTweaks.Preparation;

[HarmonyPatch]
internal class TaskPreparation(PreparationBatch batch)
{
    private static readonly ConditionalWeakTable<PendingLaunchTask, TaskPreparation> Tasks = new();
    private static bool unavailable;
    private PreparationLoadout? abortReturnTarget;
    private Func<double, double>? drawFuel;
    internal PreparationBatch Batch { get; } = batch;
    internal StoreReservation Reservation { get; private set; } = new();
    internal static TaskPreparation? Find(PendingLaunchTask task) => Tasks.TryGetValue(task, out var preparation) ? preparation : null;
    internal static Loadout Selected(PendingLaunchTask task) => task.VehicleType.Loadouts[task._loadoutIndex];

    private static double WorkSpeed(PendingLaunchTask task)
    {
        FlightDeckTimingsMode mode = Singleton<OptionsManager>.Instance.FlightDeckTimingsMode;
        bool skipReadyUp = task._ltp._skipReadyUp && task._stateMachine.CurrentState is not AbortPreparation;
        return mode == FlightDeckTimingsMode.Cheater || skipReadyUp || !DM_TestTools._allowAircraftReadyUpTime ? double.PositiveInfinity
            : Globals._multipliers[mode] / PreparationCatalog.OperationTimeMultiplier(task);
    }

    internal static bool CompleteImmediately(PendingLaunchTask task) =>
        (task._ltp._skipReadyUp || !DM_TestTools._allowAircraftReadyUpTime)
        && double.IsPositiveInfinity(DeckFuel.Available(task._flightDeck));

    internal static PreparationLoadout? Plan(PendingLaunchTask task, Loadout loadout)
    {
        if (unavailable) return null;
        try { return PreparationCatalog.Loadout(task.VehicleType, loadout); }
        catch (Exception exception)
        {
            unavailable = true;
            Debug.LogError($"[Deck Tweaks] Preparation configuration failed; new tasks use native timing. {exception}");
            return null;
        }
    }

    internal static TaskPreparation Attach(PendingLaunchTask task, PreparationBatch batch, StoreReservation? reservation = null)
    {
        TaskPreparation preparation = new(batch) { Reservation = reservation ?? new(), drawFuel = DeckFuel.Attach(task) };
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

    internal void BeginAbort(PendingLaunchTask task)
    {
        if (abortReturnTarget != null) return;
        abortReturnTarget = Batch.Aircraft[0].Target;
        Batch.Retarget(new(0, [], abortReturnTarget.FuelCapacity, refuel: false));
        task._stateMachine.setState(new AbortPreparation(task, this));
        Sync(task);
    }

    internal void CancelAbort(PendingLaunchTask task)
    {
        if (abortReturnTarget is not { } target) return;
        abortReturnTarget = null;
        Batch.Retarget(target);
        task._stateMachine.SetStateByName(task.AssignedDeckSpots >= task.LaunchCount
            ? nameof(HandleAwaitGroundCrewTask) : nameof(HandleAwaitDeckSpaceTask));
        Sync(task);
    }

    internal void UpdateAbort(PendingLaunchTask task)
    {
        double speed = WorkSpeed(task);
        if (double.IsPositiveInfinity(speed)) Batch.Complete();
        else Batch.Advance(GameTime.deltaTime * speed, task.AssignedGroundCrew);
        if (Batch.ReadyCount == Batch.Aircraft.Count)
        {
            task._flightDeck.abortLaunchTask(task._uid, delayed: true);
            return;
        }
        UpdateReservation(task);
        Sync(task);
        task.Info = task.AssignedGroundCrew == 0 ? "Abort queued (awaiting crew)"
            : $"{task.AssignedGroundCrew} crew, abort complete in {AircraftPreparation.Duration(Batch.EstimatedSeconds(task.AssignedGroundCrew) / speed)}";
    }

    private void UpdateReservation(PendingLaunchTask task)
    {
        // The original loadout stays reserved so the task can resume without buying it again.
        StoreReservation next = StoreReservation.Excess(Batch, abortReturnTarget ?? Batch.Aircraft[0].Target);
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
        int crews = ActiveCrews(task);
        string complete = abortReturnTarget == null ? "Ready" : "Empty";
        bool noFuel = DeckFuel.Available(task._flightDeck) <= 0;
        return string.Join("\n", Batch.Aircraft.Select(aircraft => aircraft.Ready ? complete
            : aircraft.NeedsFuel && noFuel ? "Awaiting aviation fuel"
            : crews-- > 0 ? aircraft.Activity(PreparationCatalog.WeaponName) : waiting).GroupBy(activity => activity)
            .Select(group => $"{group.Count()}x {group.Key}"));
    }

    internal IEnumerable<PreparationLine> Describe(PendingLaunchTask task)
    {
        string waiting = task._stateMachine.CurrentState switch
        {
            AbortPreparation => "Awaiting crew to unload",
            HandleAwaitDeckSpaceTask => "Awaiting deck space",
            _ => "Awaiting crew",
        };
        double stock = DeckFuel.Available(task._flightDeck);
        double needed = Batch.Aircraft.Where(aircraft => aircraft.NeedsFuel)
            .Sum(aircraft => (1 - aircraft.Fuel) * aircraft.Target.FuelCapacity);
        if (needed > stock)
            return new[] { new PreparationLine($"Aviation fuel: {stock:N0} kg available, {needed:N0} kg needed for this batch.") }
                .Concat(DescribeAircraft(Batch, WorkSpeed(task), ActiveCrews(task), waiting));
        return DescribeAircraft(Batch, WorkSpeed(task), ActiveCrews(task), waiting);
    }

    private static int ActiveCrews(PendingLaunchTask task) =>
        task._stateMachine.CurrentState is HandleReadyUpTask or AbortPreparation ? Math.Max(0, task.AssignedGroundCrew) : 0;

    internal IEnumerable<PreparationLine> DescribeSwap(PendingLaunchTask task, PreparationLoadout target)
    {
        PreparationBatch preview = Batch.Preview(target);
        int crews = Math.Max(1, task.AssignedGroundCrew);
        double speed = WorkSpeed(task);
        yield return new($"Batch with {crews} crew", completion: $"Ready in {AircraftPreparation.Duration(preview.EstimatedSeconds(crews) / speed)}");
        yield return new("Excludes waiting for crew and deck space.");
        yield return new("Keeps current fuel and stores; includes unloading incompatible weapons.");
        yield return new("");
        foreach (PreparationLine line in DescribeAircraft(preview, speed, 0, "After swap")) yield return line;
    }

    private static IEnumerable<PreparationLine> DescribeAircraft(PreparationBatch batch, double speed, int crews, string waiting)
    {
        List<(List<int> Numbers, PreparationLine[] Lines)> groups = [];
        for (int index = 0; index < batch.Aircraft.Count; index++)
        {
            AircraftPreparation aircraft = batch.Aircraft[index];
            bool working = !aircraft.Ready && crews-- > 0;
            PreparationLine[] lines = [.. aircraft.Describe(speed, working, waiting, PreparationCatalog.WeaponName)];
            var group = groups.Find(group => group.Lines.SequenceEqual(lines));
            if (group.Numbers == null) groups.Add(([index + 1], lines));
            else group.Numbers.Add(index + 1);
        }
        for (int index = 0; index < groups.Count; index++)
        {
            if (index > 0) yield return new("");
            var (numbers, lines) = groups[index];
            string heading = numbers.Count == 1 ? numbers[0].ToString()
                : $"{string.Join(", ", numbers.Take(numbers.Count - 1))} & {numbers[^1]}";
            yield return new($"Aircraft {heading}");
            foreach (PreparationLine line in lines) yield return line;
        }
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
        if (CompleteImmediately(__instance)) batch.Complete();
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
            preparation.Batch.Complete(preparation.drawFuel);
        else preparation.Batch.Advance(GameTime.deltaTime * speed, Math.Max(0, task.AssignedGroundCrew), preparation.drawFuel);
        preparation.UpdateReservation(task);
        preparation.Sync(task);
        double seconds = preparation.Batch.EstimatedSeconds(task.AssignedGroundCrew) / speed;
        task.Info = preparation.Batch.Aircraft.Any(aircraft => aircraft.NeedsFuel) && DeckFuel.Available(task._flightDeck) <= 0
            ? "Awaiting aviation fuel" : !double.IsFinite(seconds) ? "Awaiting crew"
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
    private static bool AbortAll(FlightDeck __instance)
    {
        if (!__instance.FlightDeckTasks.OfType<PendingLaunchTask>().Any(task => Find(task) != null)) return true;
        foreach (PendingLaunchTask task in __instance.FlightDeckTasks.OfType<PendingLaunchTask>().ToArray())
            __instance.abortLaunchTask(task._uid);
        return false;
    }

    private static void Release(PendingLaunchTask task)
    {
        if (Find(task) is not { } preparation) return;
        preparation.Reservation.Replace(task._flightDeck, new());
        preparation.Reservation = new();
        HangarPreparation.Return(task, preparation.Batch);
        Tasks.Remove(task);
    }
}
