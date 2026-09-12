using System.ComponentModel;
using SeaPower;
using UnityEngine;

namespace UNREP;

internal class UnrepSupply(SupplyTransfer job, SupplyStock stock) : INotifyPropertyChanged
{
    internal SupplyStock Stock { get; } = stock;
    internal double Delivered { get; set; }
    public bool Enabled { get; set; } = true;
    public int Priority => job.Supplies.IndexOf(this) + 1;
    public string Item => Stock.Name;
    public string Amount => $"{(Enabled ? Available : 0):N0} {Stock.Unit}";
    public string DeliveredAmount => $"{Delivered:N0} {Stock.Unit}";
    internal double Space => SupplyInventory.Read(job.Target).Where(item => item.Key == Stock.Key && item.Transferable).Sum(item => item.Free);
    internal double Available => Math.Min(SupplyPolicy.Available(job.Source, Stock), Space);
    internal bool Runnable => Enabled && Stock.Transferable && Available >= (Stock.Whole ? 1 : 0.001);
    public string Status => !Enabled ? "Disabled" : !Stock.Transferable ? "Unavailable"
        : SupplyPolicy.Available(job.Source, Stock) < (Stock.Whole ? 1 : 0.001) ? "Minimum reached"
        : Space < (Stock.Whole ? 1 : 0.001) ? "Receiver full"
        : job.Current == this ? "Transferring" : "Ready";
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Refresh() => PropertyChanged?.Invoke(this, new(""));
}

// One station visit serves the receiver's ordered supply plan.
internal class SupplyTransfer(Vessel source, Vessel target)
{
    internal Vessel Source { get; } = source;
    internal Vessel Target { get; } = target;
    internal List<UnrepSupply> Supplies { get; } = [];
    internal AlongsideStation? Station { get; private set; }
    internal UnrepSupply? Current { get; private set; }
    internal bool Finished { get; private set; } = true;
    internal bool HasRun { get; private set; }
    private bool ending;
    private string outcome = "Complete";
    private double credit;
    public string State { get; private set; } = "Ready";
    internal void SyncSupplies()
    {
        var keys = SupplyInventory.Read(Target).Where(stock => stock.CanChange).Select(stock => stock.Key).ToHashSet();
        var stocks = SupplyInventory.Read(Source).Where(stock => stock.CanChange && keys.Contains(stock.Key))
            .GroupBy(stock => stock.Key).Select(group => SupplyStock.Stack(group.ToArray()));
        foreach (var stock in stocks)
            if (!Supplies.Any(plan => plan.Stock.Key == stock.Key)) Supplies.Add(new(this, stock));
    }
    internal void Queue()
    {
        Finished = ending = false;
        HasRun = true;
        Station = null;
        Current = null;
        credit = 0;
        outcome = "Complete";
        Supplies.ForEach(plan => plan.Delivered = 0);
        State = "Queued";
    }
    internal void Cancel()
    {
        if (Finished) return;
        ending = true;
        outcome = "Cancelled";
        if (Station == null) { Finished = true; State = outcome; }
    }
    internal void Release() { Station?.Release(); Current = null; Finished = true; }
    internal void MoveSupply(UnrepSupply supply, int direction) => SupplyTransfers.Move(Supplies, supply, direction);
    internal void Update(double seconds)
    {
        if (Finished) return;
        if (!SupplyPolicy.For(Source).Unrep) Cancel();
        if (Finished) return;
        if (!SupplyTransfers.Available(Source) || !SupplyTransfers.Available(Target) || Source._taskforce != Target._taskforce)
        { Release(); State = "Ship unavailable"; return; }
        if (!ending && !Supplies.Any(plan => plan.Runnable)) { ending = true; outcome = "Complete"; }
        Station ??= new(this);
        bool onStation = Station.Update(ending);
        if (Station.Released) { Release(); State = ending ? outcome : Station.State; return; }
        if (!onStation || ending) { Current = null; credit = 0; State = Station.State; return; }
        string blocked = SupplyTransfers.Blocked(Source, Target);
        if (blocked.Length > 0) { Current = null; credit = 0; State = blocked; return; }
        var plan = Supplies.FirstOrDefault(item => item.Runnable);
        if (plan == null) { ending = true; Current = null; return; }
        if (Current != plan) credit = 0;
        Current = plan;
        var stock = plan.Stock;
        double rate = stock.Unit == "kg" ? SupplySettings.KgPerSecond
            : stock.Unit == "points" ? SupplySettings.PointsPerSecond : SupplySettings.ItemsPerSecond;
        credit += seconds * rate;
        double moved = Math.Min(credit, plan.Available);
        if (stock.Whole) moved = Math.Floor(moved);
        foreach (var destination in SupplyInventory.Read(Target).Where(item => item.Key == stock.Key && item.Transferable))
        {
            double part = Math.Min(moved, destination.Free);
            if (stock.Whole) part = Math.Floor(part);
            if (part <= 0) continue;
            stock.Change(-part);
            destination.Change(part);
            plan.Delivered += part;
            credit -= part;
            moved -= part;
        }
        if (!plan.Runnable) { Current = null; credit = 0; }
        State = $"Transferring {stock.Name}";
    }
}

internal static class SupplyTransfers
{
    internal static readonly List<SupplyTransfer> Jobs = [];
    private static float previous = float.NaN;
    private static ObjectsManager? world;
    internal static bool Available(Vessel? ship) => ship != null && ship._obp != null && !ship.IsDestroyed
        && ship.IsPlayerUnit && ship.AcceptsOrdersFromPlayer;
    internal static string Blocked(Vessel source, Vessel target)
    {
        if (!source.SupplyAllowed.Value || !target.SupplyAllowed.Value) return "Supply disabled";
        if (Vector3.Distance(source.transform.position, target.transform.position) > SupplySettings.RangeNm * 27.559496)
            return "Waiting for range";
        if (Math.Abs(source._velocityInKnots) > SupplySettings.MaxSpeed || Math.Abs(target._velocityInKnots) > SupplySettings.MaxSpeed)
            return "Waiting for speed";
        return "";
    }
    internal static SupplyTransfer Plan(Vessel source, Vessel target)
    {
        var plan = Jobs.FirstOrDefault(job => job.Source == source && job.Target == target);
        if (plan == null) { plan = new(source, target); Jobs.Add(plan); }
        plan.SyncSupplies();
        return plan;
    }
    internal static string QueueBlocked(SupplyTransfer job)
    {
        if (!Available(job.Source) || !Available(job.Target) || job.Source == job.Target || job.Source._taskforce != job.Target._taskforce)
            return "Ship unavailable";
        if (!SupplyPolicy.For(job.Source).Unrep) return "UNREP disabled";
        if (job.Source.IsFormationWingman) return "Supplier must lead formation";
        if (!job.Source.SupplyAllowed.Value || !job.Target.SupplyAllowed.Value) return "Supply disabled";
        if (VertrepOperation.Uses(job.Source) || VertrepOperation.Uses(job.Target)) return "Ship busy with VERTREP";
        if (Jobs.Any(other => other != job && !other.Finished
            && (other.Target == job.Source || other.Source == job.Target || other.Target == job.Target))) return "Ship busy with UNREP";
        return job.Supplies.Any(plan => plan.Runnable) ? "" : "No enabled transfers available";
    }
    internal static string Queue(SupplyTransfer job)
    {
        if (!job.Finished) return "Already queued";
        string blocked = QueueBlocked(job);
        if (blocked.Length > 0) return blocked;
        Jobs.Remove(job);
        Jobs.Add(job);
        job.Queue();
        return "Ship queued";
    }
    internal static void Move<T>(List<T> items, T item, int direction)
    {
        int index = items.IndexOf(item), next = index + direction;
        if (index < 0 || next < 0 || next >= items.Count) return;
        (items[index], items[next]) = (items[next], items[index]);
    }
    internal static void MoveShip(SupplyTransfer job, int direction)
    {
        var queue = Jobs.Where(item => item.Source == job.Source && !item.Finished).ToList();
        int index = queue.IndexOf(job), next = index + direction;
        if (index < 0 || next < 0 || next >= queue.Count) return;
        int first = Jobs.IndexOf(job), second = Jobs.IndexOf(queue[next]);
        (Jobs[first], Jobs[second]) = (Jobs[second], Jobs[first]);
    }
    internal static void Update()
    {
        var current = Singleton<ObjectsManager>.InstanceExists() ? Singleton<ObjectsManager>.Instance : null;
        float now = GameTime.missionSessionTime;
        if (world != current || current == null || !float.IsNaN(previous) && now < previous)
        { foreach (var job in Jobs) job.Release(); Jobs.Clear(); VertrepOperation.Clear(); SupplySpeedLimits.Update(); world = current; previous = now; return; }
        double seconds = float.IsNaN(previous) ? 0 : Math.Max(0, now - previous);
        previous = now;
        foreach (var job in Jobs) job.Update(seconds);
        VertrepOperation.Update(seconds);
        SupplySpeedLimits.Update();
    }
}
