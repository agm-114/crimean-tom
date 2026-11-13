using System.ComponentModel;
using SeaPower;
using DeckTweaks.Preparation;
using UnityEngine;

namespace DeckTweaks;

public class TaskLoadout : INotifyPropertyChanged
{
    private readonly PendingLaunchTask task;
    private double nextStatusUpdate;
    private float nextStatusRefreshTime;
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool CanChange => !task._isCancelled && task.LaunchCount > 0
        && task._flightDeck._baseObject.AcceptsOrdersFromPlayer
        && !task._flightDeck.LaunchesDisabled && !task._flightDeck.DisabledByMission
        && task._flightDeck.FlightDeckTasks.Contains(task)
        && task._stateMachine.CurrentState is HandleAwaitDeckSpaceTask or HandleAwaitGroundCrewTask
            or HandleReadyUpTask or HandleAwaitSpawnTask;
    public Loadout[] Loadouts { get; }
    internal bool TimingExpanded { get; set; }
    private static int CurrentYear => Singleton<SeaPower.Environment>.InstanceExists()
        ? Singleton<SeaPower.Environment>.Instance.Year : int.MaxValue;
    internal IEnumerable<PreparationLine> PreparationStatus => TaskPreparation.Find(task) is { } preparation
        ? preparation.Describe(task)
        : [new("Native preparation. Detailed fuel and stores are not tracked for this task.")];
    public Loadout SelectedLoadout
    {
        get => task.VehicleType.Loadouts[task._loadoutIndex];
        set
        {
            if (value == null || value == SelectedLoadout) return;
            TryChange(value);
            PropertyChanged?.Invoke(this, new(nameof(SelectedLoadout)));
        }
    }

    public TaskLoadout(PendingLaunchTask task) : this(task, observe: true) { }

    private TaskLoadout(PendingLaunchTask task, bool observe)
    {
        this.task = task;
        Loadouts = task.VehicleType.Loadouts.Where(loadout => loadout == SelectedLoadout
            || loadout.ServiceEntryYear <= CurrentYear).ToArray();
        if (!observe) return;
        task._stateMachine.OnStateChanged += (_, _) =>
        {
            PropertyChanged?.Invoke(this, new(nameof(CanChange)));
            PropertyChanged?.Invoke(this, new(nameof(PreparationStatus)));
        };
        task.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(FlightDeckTask.FlightDeckTaskLabel) || GameTime.simulationTime < nextStatusUpdate) return;
            float now = Time.realtimeSinceStartup;
            if (now < nextStatusRefreshTime) return;
            nextStatusUpdate = GameTime.simulationTime + 1;
            nextStatusRefreshTime = now + 0.25f;
            PropertyChanged?.Invoke(this, new(nameof(PreparationStatus)));
        };
    }

    public static PendingLaunchTask? ChangePreparedLoadout(PendingLaunchTask task, Loadout loadout) =>
        new TaskLoadout(task, observe: false).TryChange(loadout);

    private PendingLaunchTask? TryChange(Loadout loadout)
    {
        if (loadout == SelectedLoadout) return task;
        if (!CanChange || !Loadouts.Contains(loadout)) return null;
        TaskPreparation? preparation = TaskPreparation.Find(task);
        PreparationLoadout? target = preparation == null ? null : TaskPreparation.Plan(task, loadout);
        if (preparation != null && target == null) return null;
        if (CanAfford(loadout, preparation, target)) return Change(loadout, preparation, target);
        Globals._mainGameViewModel.RaiseAlert(task._flightDeck._baseObject,
            Singleton<LanguageResourceHandler>.Instance.getText("FlightDeck", "UnableToLaunchNoAmmo"));
        return null;
    }

    internal IEnumerable<PreparationLine> Preview(Loadout loadout)
    {
        if (loadout == SelectedLoadout) return PreparationStatus;
        if (!CanChange) return [new("This task can no longer change loadout.")];
        if (TaskPreparation.Find(task) is not { } preparation)
            return [new("Swap estimate unavailable: this task uses native preparation.")];
        try
        {
            PreparationLoadout target = PreparationCatalog.Loadout(task.VehicleType, loadout);
            List<PreparationLine> lines = [new($"Switch to {loadout.DisplayName}")];
            if (!CanAfford(loadout, preparation, target)) lines.Add(new("Insufficient ammunition stock."));
            lines.AddRange(preparation.DescribeSwap(task, target));
            return lines;
        }
        catch (Exception)
        {
            return [new("Swap estimate unavailable for this loadout.")];
        }
    }

    private bool CanAfford(Loadout loadout, TaskPreparation? preparation, PreparationLoadout? target)
    {
        if (preparation != null && target != null)
            return StoreReservation.Excess(preparation.Batch, target).CanReplace(task._flightDeck,
                preparation.Reservation, SelectedLoadout, loadout, task.LaunchCount);
        if (Globals._cheatFlightDeckAmmo) return true;
        FlightDeck deck = task._flightDeck;
        int count = task.LaunchCount;
        if (deck._currentAmmo + SelectedLoadout._ammoPoints * count < loadout._ammoPoints * count) return false;
        return loadout._accountableAmmunition.All(ammo =>
        {
            deck._accountableAmmunition.TryGetValue(ammo.Key, out int available);
            SelectedLoadout._accountableAmmunition.TryGetValue(ammo.Key, out int reserved);
            return available + reserved * count >= ammo.Value * count;
        });
    }

    private PendingLaunchTask? Change(Loadout loadout, TaskPreparation? preparation, PreparationLoadout? target)
    {
        FlightDeck deck = task._flightDeck;
        Squadron squadron = task.VehicleType.Squadrons[task._squadronIndex];
        string callsign = squadron.Callsigns[task._callsignIndex];
        int index = deck.FlightDeckTasks.IndexOf(task);
        int count = task.LaunchCount;
        bool allowLaunch = task.launchAllowed;
        Loadout original = SelectedLoadout;
        PreparationLoadout? originalTarget = preparation?.Batch.Aircraft[0].Target;
        List<GroundCargoManifestEntry> cargo = [.. task._ltp._groundCargo];
        // Abort refunds the old weapons and aircraft; creation reserves the new loadout normally.
        HangarPreparation.ChangingLoadout = true;
        try
        {
            deck.abortLaunchTask(task._uid);
            task._ltp._launchCount = count;
            task._ltp._skipReadyUp = false;
            task._ltp._groundCargo = cargo;
            PendingLaunchTask replacement = deck.createLaunchTask(task.VehicleType, loadout, squadron, callsign,
                task._ltp, allowLaunch: false, readyUpTime: loadout._readyUpDuration);
            if (replacement == null)
            {
                replacement = deck.createLaunchTask(task.VehicleType, original, squadron, callsign,
                    task._ltp, allowLaunch, readyUpTime: original._readyUpDuration);
                target = originalTarget;
            }
            if (replacement == null)
            {
                HangarPreparation.ChangingLoadout = false;
                if (preparation != null) HangarPreparation.Return(task, preparation.Batch);
                deck.UpdateLoadoutsAvailability();
                return null;
            }
            if (preparation != null && target != null) preparation.Transfer(replacement, target);
            replacement._formation = task._formation;
            replacement.Launched = task.Launched;
            deck.FlightDeckTasks.Move(deck.FlightDeckTasks.IndexOf(replacement), index);
            return replacement.VehicleType.Loadouts[replacement._loadoutIndex] == loadout ? replacement : null;
        }
        finally { HangarPreparation.ChangingLoadout = false; }
    }
}
