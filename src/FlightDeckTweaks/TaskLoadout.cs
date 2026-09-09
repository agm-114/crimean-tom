using System.ComponentModel;
using SeaPower;
using FlightDeckTweaks.Preparation;

namespace FlightDeckTweaks;

public class TaskLoadout : INotifyPropertyChanged
{
    private readonly PendingLaunchTask task;
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool CanChange => !task._isCancelled && task.LaunchCount > 0
        && task._flightDeck._baseObject.AcceptsOrdersFromPlayer
        && task._flightDeck.FlightDeckTasks.Contains(task)
        && task._stateMachine.CurrentState is HandleAwaitDeckSpaceTask or HandleAwaitGroundCrewTask
            or HandleReadyUpTask or HandleAwaitSpawnTask;
    public Loadout[] Loadouts => task.VehicleType.Loadouts.Where(loadout => loadout == SelectedLoadout
        || loadout.ServiceEntryYear <= CurrentYear).ToArray();
    private static int CurrentYear => Singleton<SeaPower.Environment>.InstanceExists()
        ? Singleton<SeaPower.Environment>.Instance.Year : int.MaxValue;
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
        task._stateMachine.OnStateChanged += (_, _) => PropertyChanged?.Invoke(this, new(nameof(CanChange)));
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
        deck.abortLaunchTask(task._uid);
        task._ltp._launchCount = task.LaunchCount;
        task._ltp._skipReadyUp = false;
        PendingLaunchTask replacement = deck.createLaunchTask(task.VehicleType, loadout, squadron, callsign,
            task._ltp, allowLaunch: false, readyUpTime: loadout._readyUpDuration);
        if (preparation != null && target != null) preparation.Transfer(replacement, target);
        replacement._formation = task._formation;
        replacement.Launched = task.Launched;
        deck.FlightDeckTasks.Move(deck.FlightDeckTasks.IndexOf(replacement), index);
    }
}
