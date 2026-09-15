using System.ComponentModel;
using SeaPower;

namespace UNREP;

internal class VertrepTransfer(VertrepReceiver receiver, SupplyStock stock) : INotifyPropertyChanged
{
    internal VertrepReceiver Plan { get; } = receiver;
    internal VertrepOperation Operation => Plan.Operation;
    internal Vessel Target => Plan.Target;
    internal SupplyStock Stock { get; } = stock;
    internal double Delivered { get; set; }
    public bool Enabled { get; set; } = true;
    internal bool Cancelled => !Plan.Queued || !Enabled;
    internal double Cargo => Operation.Helicopters.Where(helo => helo.Transfer == this).Sum(helo => helo.Cargo);
    internal double Available => !SupplyTransfers.Available(Target) ? 0
        : Math.Min(SupplyPolicy.Available(Operation.Source, Stock), Space);
    private double Space => Math.Max(0, SupplyInventory.Read(Target)
        .Where(item => item.Key == Stock.Key && item.Transferable).Sum(item => item.Free)
        - Operation.Transfers.Where(plan => plan.Target == Target && plan.Stock.Key == Stock.Key).Sum(plan => plan.Cargo));
    internal string Blocked => !SupplyTransfers.Available(Target) || Target._taskforce != Operation.Source._taskforce ? "Receiver unavailable"
        : !VertrepLanding.HasDeck(Target) ? "No helicopter landing deck"
        : !Operation.Source.SupplyAllowed.Value || !Target.SupplyAllowed.Value ? "Supply disabled"
        : !Stock.Transferable ? "Stock unavailable"
        : SupplyPolicy.Available(Operation.Source, Stock) < 0.001 ? "Minimum reached"
        : Space < 0.001 ? "Receiver full" : "";
    public int Priority => Plan.Supplies.IndexOf(this) + 1;
    public string Item => Stock.Name;
    public string Receiver => Target.getName();
    public string AvailableAmount => $"{(Enabled ? Available : 0):N0} kg";
    public string DeliveredAmount => $"{Delivered:N0} kg";
    public string AirborneAmount => $"{Cargo:N0} kg";
    public string Status => Cancelled && Cargo > 0 ? "Returning cargo" : !Enabled ? "Disabled"
        : Cargo > 0 ? "In progress" : Blocked is { Length: > 0 } reason ? reason : "Ready";
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh() => PropertyChanged?.Invoke(this, new(""));
}

internal class VertrepReceiver(VertrepOperation operation, Vessel target)
{
    internal VertrepOperation Operation { get; } = operation;
    internal Vessel Target { get; } = target;
    internal List<VertrepTransfer> Supplies { get; } = [];
    internal bool Queued { get; private set; }
    internal bool HasRun { get; private set; }
    internal double Cargo => Supplies.Sum(plan => plan.Cargo);
    internal bool InUse => Queued || Cargo > 0;
    internal string Status => !Queued ? Cargo > 0 ? "Returning cargo" : HasRun ? "Cancelled" : "Ready"
        : Cargo > 0 ? "In progress"
        : Supplies.Any(plan => plan.Enabled && plan.Blocked.Length == 0 && plan.Available > 0.001) ? "Queued"
        : Supplies.FirstOrDefault(plan => plan.Enabled)?.Blocked is { Length: > 0 } reason ? reason : "All supplies disabled";
    internal void SyncSupplies()
    {
        var keys = SupplyInventory.Read(Target).Where(stock => stock.CanChange).Select(stock => stock.Key).ToHashSet();
        foreach (var stock in SupplyInventory.Stacked(Operation.Source)
            .Where(stock => stock.Key.StartsWith("unrep/", StringComparison.Ordinal) && keys.Contains(stock.Key)))
            if (!Supplies.Any(plan => plan.Stock.Key == stock.Key)) Supplies.Add(new(this, stock));
    }
    internal void Queue()
    {
        Queued = HasRun = true;
        Supplies.ForEach(plan => plan.Delivered = 0);
    }
    internal void Cancel() => Queued = false;
    internal void MoveSupply(VertrepTransfer plan, int direction) => SupplyTransfers.Move(Supplies, plan, direction);
}

internal class VertrepOperation(Vessel source)
{
    private static readonly List<VertrepOperation> Operations = [];
    internal Vessel Source { get; } = source;
    internal List<VertrepReceiver> Receivers { get; } = [];
    internal IEnumerable<VertrepTransfer> Transfers => Receivers.SelectMany(receiver => receiver.Supplies);
    internal List<HelicopterReplenishment> Helicopters { get; } = [];
    internal static VertrepOperation For(Vessel ship)
    {
        var operation = Operations.FirstOrDefault(item => item.Source == ship);
        if (operation != null) return operation;
        operation = new(ship);
        Operations.Add(operation);
        return operation;
    }
    internal VertrepReceiver Plan(Vessel target)
    {
        var plan = Receivers.FirstOrDefault(item => item.Target == target);
        if (plan == null) { plan = new(this, target); Receivers.Add(plan); }
        plan.SyncSupplies();
        return plan;
    }
    internal static bool Uses(Vessel ship) => Operations.Any(op =>
        op.Source == ship && (op.Helicopters.Any(helo => !helo.Released) || op.Receivers.Any(plan => plan.InUse))
        || op.Receivers.Any(plan => plan.Target == ship && plan.InUse));
    internal static double AirborneCargo(Vessel ship, string key) => Operations.Where(op => op.Source == ship)
        .SelectMany(op => op.Helicopters).Where(helo => helo.Transfer?.Stock.Key == key).Sum(helo => helo.Cargo);
    internal static double IncomingCargo(Vessel ship, string key) => Operations.SelectMany(op => op.Helicopters)
        .Where(helo => helo.Transfer?.Target == ship && helo.Transfer.Stock.Key == key).Sum(helo => helo.Cargo);
    internal static bool Assigned(Helicopter aircraft) => Operations.SelectMany(op => op.Helicopters)
        .Any(helo => helo.Aircraft == aircraft && !helo.Released);
    internal bool CanAssign(Helicopter aircraft) => aircraft != null && aircraft._isInFlight && !aircraft.IsDestroyed
        && !aircraft.IsLanding && !aircraft._isLanding && VertrepLanding.Supports(Source, aircraft)
        && aircraft.AcceptsOrdersFromPlayer && aircraft._taskforce == Source._taskforce
        && aircraft._obp._currentFuelMass >= aircraft._obp._maxFuel * 0.2f && !Assigned(aircraft);
    internal string Assign(Helicopter aircraft)
    {
        if (!SupplyPolicy.For(Source).Vertrep) return "VERTREP disabled.";
        if (!CanAssign(aircraft)) return "Choose a free airborne helicopter compatible with this deck, with at least 20% fuel.";
        if (!SupplySettings.AllowConcurrentUnrepAndVertrep
            && SupplyTransfers.Jobs.Any(job => !job.Finished && (job.Source == Source || job.Target == Source)))
            return "Supplier busy with UNREP.";
        if (Operations.Any(op => op != this && op.Receivers.Any(plan => plan.Target == Source && plan.InUse)))
            return "Supplier busy with another VERTREP operation.";
        Helicopters.RemoveAll(helo => helo.Aircraft == aircraft && helo.Released);
        Helicopters.Add(new(this, aircraft));
        return "Helicopter assigned.";
    }
    internal string QueueBlocked(VertrepReceiver receiver)
    {
        var target = receiver.Target;
        if (!SupplyPolicy.For(Source).Vertrep) return "VERTREP disabled";
        if (!SupplyTransfers.Available(Source) || !SupplyTransfers.Available(target) || target == Source || target._taskforce != Source._taskforce)
            return "Receiver unavailable";
        if (receiver.Cargo > 0) return "Waiting for cargo return";
        if (!VertrepLanding.HasDeck(Source) || !VertrepLanding.HasDeck(target)) return "Both ships need helicopter landing decks";
        if ((!SupplySettings.AllowConcurrentUnrepAndVertrep && SupplyTransfers.Jobs.Any(job => !job.Finished
            && (job.Source == Source || job.Target == Source || job.Source == target || job.Target == target)))
            || Operations.Any(op => op != this && (op.Source == target && Uses(target)
                || op.Receivers.Any(plan => plan.InUse && (plan.Target == Source || plan.Target == target)))))
            return "Ship busy with another operation";
        return receiver.Supplies.Any(plan => plan.Enabled) ? "" : "Enable a supply";
    }
    internal string Queue(VertrepReceiver receiver)
    {
        if (receiver.Queued) return "Already queued";
        string blocked = QueueBlocked(receiver);
        if (blocked.Length > 0) return blocked;
        Receivers.Remove(receiver);
        Receivers.Add(receiver);
        receiver.Queue();
        return "Receiver queued";
    }
    internal void MoveReceiver(VertrepReceiver receiver, int direction)
    {
        var queue = Receivers.Where(plan => plan.Queued).ToList();
        int index = queue.IndexOf(receiver), next = index + direction;
        if (index < 0 || next < 0 || next >= queue.Count) return;
        int first = Receivers.IndexOf(receiver), second = Receivers.IndexOf(queue[next]);
        (Receivers[first], Receivers[second]) = (Receivers[second], Receivers[first]);
    }
    internal VertrepTransfer? NextTransfer(Helicopter aircraft) => Transfers.FirstOrDefault(plan => !plan.Cancelled
        && VertrepLanding.Supports(plan.Target, aircraft) && plan.Blocked.Length == 0 && plan.Available > 0.001);
    internal string WaitingStatus => Receivers.FirstOrDefault(plan => plan.Queued)?.Status ?? "Waiting for transfers";
    internal void Cancel()
    {
        Receivers.ForEach(plan => plan.Cancel());
        Helicopters.ForEach(helo => helo.Cancel());
    }
    internal static void Update(double seconds)
    {
        foreach (var operation in Operations)
        {
            if (!SupplyTransfers.Available(operation.Source))
            {
                operation.Cancel();
                operation.Helicopters.ForEach(helo => helo.Release());
                continue;
            }
            if (!SupplyPolicy.For(operation.Source).Vertrep) operation.Cancel();
            foreach (var helo in operation.Helicopters) helo.Update(seconds);
        }
        if (seconds > 0) VertrepLanding.Schedule();
    }
    internal static void Clear()
    {
        foreach (var operation in Operations) operation.Helicopters.ForEach(helo => helo.Release());
        Operations.Clear();
    }
}
