using SeaPower;
using UnityEngine;

namespace UNREP;

internal class SupplyTransfer(Vessel source, Vessel target, SupplyStock stock, double amount, Helicopter? helicopter = null)
{
    internal AlongsideStation? Station { get; private set; }
    internal HelicopterReplenishment? Airlift { get; private set; }
    internal Helicopter? Helicopter { get; } = helicopter;
    private bool ending;
    private string outcome = "Complete";
    internal Vessel Source { get; } = source;
    internal Vessel Target { get; } = target;
    internal SupplyStock Stock { get; } = stock;
    internal double Requested { get; } = amount;
    internal double Delivered { get; private set; }
    internal bool Finished { get; private set; }
    private double credit;
    public string Route => $"{Source.getName()} → {Target.getName()}";
    public string Item => Stock.Name;
    public string Progress => $"{Delivered:N0} / {Requested:N0} {Stock.Unit}";
    public string State { get; private set; } = "Queued";
    internal double Rate => Stock.Unit == "kg" ? SupplySettings.KgPerSecond
        : Stock.Unit == "points" ? SupplySettings.PointsPerSecond : SupplySettings.ItemsPerSecond;
    internal void Cancel() { if (Finished) return; ending = true; outcome = "Cancelled"; }
    internal void Release() { Station?.Release(); Airlift?.Release(); Finished = true; }

    internal void Update(double seconds)
    {
        if (Finished) return;
        if (!SupplyTransfers.Available(Source) || !SupplyTransfers.Available(Target) || Source._taskforce != Target._taskforce)
        { Release(); State = "Ship unavailable"; return; }
        if (Helicopter != null && Airlift == null) Airlift = new(this, Helicopter);
        if (Helicopter == null && Station == null) Station = new(this);
        if (Airlift != null)
        {
            Delivered += Airlift.Update(seconds, ending, Requested - Delivered);
            if (Requested - Delivered < 0.001) ending = true;
            State = Airlift.State;
            if (Airlift.Released) { Finished = true; State = ending ? outcome : Airlift.State; }
            return;
        }
        bool onStation = Station!.Update(ending);
        if (Station.Released) { Finished = true; State = ending ? outcome : Station.State; return; }
        if (!onStation || ending) { State = Station.State; return; }
        string blocked = SupplyTransfers.Blocked(Source, Target);
        if (blocked.Length > 0) { State = blocked; return; }
        if (!Stock.Transferable) { State = "Source unavailable"; return; }
        var destinations = SupplyInventory.Read(Target).Where(item => item.Key == Stock.Key && item.Transferable).ToArray();
        double space = destinations.Sum(item => item.Free);
        if (Stock.Current < (Stock.Whole ? 1 : 0.001)) { outcome = "Source empty"; ending = true; return; }
        if (space < (Stock.Whole ? 1 : 0.001)) { outcome = "Destination full / incompatible"; ending = true; return; }
        credit += seconds * Rate;
        double moved = Math.Min(Math.Min(credit, Requested - Delivered), Math.Min(Stock.Current, space));
        if (Stock.Whole) moved = Math.Floor(moved);
        foreach (var destination in destinations)
        {
            double part = Math.Min(moved, destination.Free);
            if (Stock.Whole) part = Math.Floor(part);
            if (part <= 0) continue;
            Stock.Change(-part);
            destination.Change(part);
            Delivered += part;
            credit -= part;
            moved -= part;
        }
        ending = Requested - Delivered < 0.001;
        State = ending ? "Transfer complete; breaking away" : $"Transferring · {TimeSpan.FromSeconds(Math.Min((Requested - Delivered) / Rate, 1e9)):g} remaining";
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
        if (!source.SupplyAllowed.Value || !target.SupplyAllowed.Value) return "Supply disabled in standing orders";
        if (Vector3.Distance(source.transform.position, target.transform.position) > SupplySettings.RangeNm * 27.559496)
            return "Waiting for range";
        if (Math.Abs(source._velocityInKnots) > SupplySettings.MaxSpeed || Math.Abs(target._velocityInKnots) > SupplySettings.MaxSpeed)
            return "Waiting for speed";
        return "";
    }

    internal static string Start(Vessel source, Vessel target, SupplyStock stock, double amount, Helicopter? helicopter = null)
    {
        if (!Available(source) || !Available(target) || source == target || source._taskforce != target._taskforce)
            return "Choose another controlled ship in the same taskforce.";
        if (!stock.Transferable || double.IsNaN(amount) || double.IsInfinity(amount) || amount <= 0 || amount > 1e9
            || stock.Whole && amount != Math.Floor(amount))
            return "Enter a valid quantity for a transferable stock.";
        if (helicopter == null && source.IsFormationWingman) return "Use an independent ship or formation leader as supplier.";
        if (helicopter != null && (!stock.Key.StartsWith("unrep/", StringComparison.Ordinal) || stock.Unit != "kg"))
            return "Helicopter replenishment supports food, water and spare parts, not bulk fuel or ammunition.";
        if (helicopter != null && (!helicopter._isInFlight || helicopter.IsDestroyed || !helicopter.AcceptsOrdersFromPlayer
            || helicopter._taskforce != source._taskforce || helicopter._obp._currentFuelMass < helicopter._obp._maxFuel * 0.2f))
            return "Choose an airborne controlled helicopter with at least 20% fuel.";
        if (Jobs.Any(job => !job.Finished && (job.Target == source || job.Source == target || job.Target == target
            || (helicopter != null || job.Helicopter != null) && job.Source == source
            || helicopter != null && job.Helicopter == helicopter)))
            return "Ship or helicopter already assigned to another transfer.";
        double free = SupplyInventory.Read(target).Where(item => item.Key == stock.Key && item.Transferable).Sum(item => item.Free);
        if (amount > stock.Current || amount > free) return "Quantity exceeds available stock or destination capacity.";
        Jobs.RemoveAll(job => job.Finished);
        Jobs.Add(new(source, target, stock, amount, helicopter));
        return helicopter == null ? "UNREP queued: rendezvous, take station, transfer, break away. Receiver movement is now automatic."
            : "Helicopter shuttle assigned. Previous flight orders resume when finished.";
    }

    internal static void Update()
    {
        var current = Singleton<ObjectsManager>.InstanceExists() ? Singleton<ObjectsManager>.Instance : null;
        float now = GameTime.missionSessionTime;
        if (world != current || current == null || !float.IsNaN(previous) && now < previous)
        { foreach (var job in Jobs) job.Release(); Jobs.Clear(); SupplySpeedLimits.Update(); world = current; previous = now; return; }
        double seconds = float.IsNaN(previous) ? 0 : Math.Max(0, now - previous);
        previous = now;
        foreach (var job in Jobs) job.Update(seconds);
        SupplySpeedLimits.Update();
    }
}
