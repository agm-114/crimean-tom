using System.ComponentModel;
using SeaPower;
using DeckTweaks.Preparation;

namespace DeckTweaks;

public class TaskLoadout : INotifyPropertyChanged
{
    private readonly PendingLaunchTask task;
    private double nextStatusUpdate;
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool CanChange => !task._isCancelled && task.LaunchCount > 0
        && task._flightDeck._baseObject.AcceptsOrdersFromPlayer
        && task._flightDeck.FlightDeckTasks.Contains(task)
        && task._stateMachine.CurrentState is HandleAwaitDeckSpaceTask or HandleAwaitGroundCrewTask
            or HandleReadyUpTask or HandleAwaitSpawnTask;
    public Loadout[] Loadouts { get; }
    private static int CurrentYear => Singleton<SeaPower.Environment>.InstanceExists()
        ? Singleton<SeaPower.Environment>.Instance.Year : int.MaxValue;
    public string PreparationStatus => TaskPreparation.Find(task) is { } preparation
        ? preparation.Describe(task)
        : "Native preparation. Detailed fuel and stores are not tracked for this task.";
    public Loadout SelectedLoadout
    {
        get => task.VehicleType.Loadouts[task._loadoutIndex];
        set
        {
            if (value == null || value == SelectedLoadout) return;
            if (CanChange && Loadouts.Contains(value))
            {
                TaskPreparation? preparation = TaskPreparation.Find(task);
                PreparationLoadout? target = preparation == null ? null : TaskPreparation.Plan(task, value);
                if (preparation != null && target == null) return;
                if (CanAfford(value, preparation, target)) Change(value, preparation, target);
                else Globals._mainGameViewModel.RaiseAlert(task._flightDeck._baseObject,
                    Singleton<LanguageResourceHandler>.Instance.getText("FlightDeck", "UnableToLaunchNoAmmo"));
            }
            PropertyChanged?.Invoke(this, new(nameof(SelectedLoadout)));
        }
    }

    public TaskLoadout(PendingLaunchTask task)
    {
        this.task = task;
        Loadouts = task.VehicleType.Loadouts.Where(loadout => loadout == SelectedLoadout
            || loadout.ServiceEntryYear <= CurrentYear).ToArray();
        task._stateMachine.OnStateChanged += (_, _) =>
        {
            PropertyChanged?.Invoke(this, new(nameof(CanChange)));
            PropertyChanged?.Invoke(this, new(nameof(PreparationStatus)));
        };
        task.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(FlightDeckTask.FlightDeckTaskLabel) || GameTime.simulationTime < nextStatusUpdate) return;
            nextStatusUpdate = GameTime.simulationTime + 1;
            PropertyChanged?.Invoke(this, new(nameof(PreparationStatus)));
        };
    }

    internal string Preview(Loadout loadout)
    {
        if (loadout == SelectedLoadout) return PreparationStatus;
        if (!CanChange) return "This task can no longer change loadout.";
        if (TaskPreparation.Find(task) is not { } preparation)
            return "Swap estimate unavailable: this task uses native preparation.";
        try
        {
            PreparationLoadout target = PreparationCatalog.Loadout(task.VehicleType, loadout);
            string stock = CanAfford(loadout, preparation, target) ? "" : "Insufficient ammunition stock.\n";
            return $"Switch to {loadout.DisplayName}\n{stock}{preparation.DescribeSwap(task, target)}";
        }
        catch (Exception)
        {
            return "Swap estimate unavailable for this loadout.";
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

    private void Change(Loadout loadout, TaskPreparation? preparation, PreparationLoadout? target)
    {
        FlightDeck deck = task._flightDeck;
        Squadron squadron = task.VehicleType.Squadrons[task._squadronIndex];
        string callsign = squadron.Callsigns[task._callsignIndex];
        int index = deck.FlightDeckTasks.IndexOf(task);
        // Abort refunds the old weapons and aircraft; creation reserves the new loadout normally.
        HangarPreparation.ChangingLoadout = true;
        try
        {
            deck.abortLaunchTask(task._uid);
            task._ltp._launchCount = task.LaunchCount;
            task._ltp._skipReadyUp = false;
            PendingLaunchTask replacement = deck.createLaunchTask(task.VehicleType, loadout, squadron, callsign,
                task._ltp, allowLaunch: false, readyUpTime: loadout._readyUpDuration);
            if (replacement == null)
            {
                HangarPreparation.ChangingLoadout = false;
                if (preparation != null) HangarPreparation.Return(task, preparation.Batch);
                deck.UpdateLoadoutsAvailability();
                return;
            }
            if (preparation != null && target != null) preparation.Transfer(replacement, target);
            replacement._formation = task._formation;
            replacement.Launched = task.Launched;
            deck.FlightDeckTasks.Move(deck.FlightDeckTasks.IndexOf(replacement), index);
        }
        finally { HangarPreparation.ChangingLoadout = false; }
    }
}
