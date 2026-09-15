using System.ComponentModel;
using SeaPower;

namespace UNREP;

// One aircraft assignment survives repeated deck landings and planned transfers.
internal class HelicopterReplenishment(VertrepOperation operation, Helicopter helicopter) : INotifyPropertyChanged
{
    internal Helicopter Aircraft { get; } = helicopter;
    internal VertrepTransfer? Transfer { get; private set; }
    internal double Cargo { get; private set; }
    internal bool Released { get; private set; }
    internal bool InTransit { get; private set; }
    internal bool Waiting { get; private set; }
    private MovementLease? flight;
    private VertrepLanding? landing;
    private Vessel? nextShip;
    private bool leaving;
    private bool delivering;
    private bool ending;
    private bool returning;
    private double delivered;
    private string outcome = "Released";
    public string Name => Aircraft.getName();
    public string Supply => Transfer?.Item ?? "";
    public string Receiver => Transfer?.Receiver ?? "";
    public string Load => $"{Cargo:N0} kg";
    public string Delivered => $"{delivered:N0} kg";
    public string Status { get; private set; } = "Assigned";
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh() => PropertyChanged?.Invoke(this, new(""));
    internal void Cancel() => ending = true;

    internal void Update(double seconds)
    {
        InTransit = Waiting = false;
        if (Released) return;
        if (Aircraft == null || Aircraft.IsDestroyed)
        {
            Cargo = 0;
            Transfer = null;
            Release();
            Status = "Helicopter lost";
            return;
        }
        if (Aircraft._obp._currentFuelMass < Aircraft._obp._maxFuel * 0.2f)
        { ending = true; outcome = "Low fuel: released"; }
        if (flight == null) { flight = new(Aircraft); FlyTo(operation.Source); }
        if (leaving)
        {
            landing!.Update(seconds);
            if (Aircraft == null || Aircraft.IsDestroyed) { Cargo = 0; Transfer = null; Release(); Status = "Helicopter lost"; return; }
            InTransit = true;
            Status = landing.Status;
            if (!landing.Departed) return;
            landing.Dispose();
            landing = null;
            leaving = false;
            if (ending && Cargo == 0) { Release(); return; }
            FlyTo(nextShip ?? operation.Source);
            return;
        }
        if (!flight.Owned)
        {
            ending = true;
            outcome = "Flight orders replaced";
            Refund();
            FinishOrTakeOff();
            return;
        }
        if (ending && Cargo == 0) { FinishOrTakeOff(); return; }
        if (Cargo > 0 && (ending || Transfer!.Cancelled || !SupplyTransfers.Available(Transfer.Target)
            || Transfer.Target._taskforce != operation.Source._taskforce) && !returning)
        {
            returning = true;
            delivering = false;
            FlyTo(operation.Source);
            if (leaving) return;
        }
        Vessel ship = delivering ? Transfer!.Target : operation.Source;
        landing!.Update(seconds);
        if (landing.Departed) { outcome = landing.Status; Release(); return; }
        if (!landing.Landed)
        {
            InTransit = !landing.Status.StartsWith("Waiting", StringComparison.Ordinal);
            Waiting = !InTransit;
            Status = landing.Status + (delivering ? " at receiver" : " at supplier");
            return;
        }
        if (landing.ShouldClearDeck)
        {
            FlyTo(ship);
            Status = "Clearing deck for normal flight operations";
            return;
        }
        if (!ship.SupplyAllowed.Value) { Status = "Supply disabled"; return; }
        if (!delivering && !returning)
        {
            var next = operation.NextTransfer(Aircraft);
            Transfer = next;
            if (Transfer == null) { Waiting = true; Status = operation.WaitingStatus; return; }
        }
        if (seconds <= 0) return;
        Status = returning ? "Returning cargo" : delivering ? "Unloading" : "Loading";
        if (returning)
        {
            Refund();
            returning = false;
            if (ending) FinishOrTakeOff();
            return;
        }
        if (!delivering)
        {
            Cargo = Math.Min(SupplySettings.SlingCapacityKg, Transfer!.Available);
            Transfer.Stock.Change(-Cargo);
            delivering = true;
            FlyTo(Transfer.Target);
            return;
        }
        foreach (var destination in SupplyInventory.Read(Transfer!.Target)
            .Where(stock => stock.Key == Transfer.Stock.Key && stock.Transferable))
        {
            double part = Math.Min(Cargo, destination.Free);
            if (part <= 0) continue;
            destination.Change(part);
            Cargo -= part;
            Transfer.Delivered += part;
            delivered += part;
        }
        if (Cargo > 0) { Status = "Holding: receiver full"; return; }
        Transfer = null;
        delivering = false;
        FlyTo(operation.Source);
        Status = "Returning to supplier";
    }
    private void FlyTo(Vessel ship)
    {
        if (landing?.ControlsPose == true)
        {
            nextShip = ship;
            landing.TakeOff();
            leaving = true;
            return;
        }
        landing?.Dispose();
        flight!.Follow(ship, 0, 0, "VERTREP waiting station", height: 1.5f, leadNm: 0.025f);
        landing = new(Aircraft, ship, flight);
    }
    private void FinishOrTakeOff()
    {
        if (landing?.ControlsPose == true)
        {
            nextShip = null;
            landing.TakeOff();
            leaving = true;
            Status = "Taking off";
        }
        else Release();
    }
    private void Refund()
    {
        if (operation.Source != null && !operation.Source.IsDestroyed && Cargo > 0) Transfer!.Stock.Change(Cargo);
        Cargo = 0;
        Transfer = null;
    }
    internal void Release()
    {
        Refund();
        flight?.Restore();
        landing?.Dispose();
        landing = null;
        Released = true;
        Status = outcome;
    }
}
