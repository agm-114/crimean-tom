using SeaPower;

namespace UNREP;

// Real airborne helicopter, finite sling load, pickup and delivery at the ships.
internal class HelicopterReplenishment(SupplyTransfer job, Helicopter helicopter)
{
    internal Helicopter Aircraft { get; } = helicopter;
    internal double Cargo { get; private set; }
    internal bool Released { get; private set; }
    internal string State { get; private set; } = "Flying to pickup";
    private MovementLease? flight;
    private bool delivering;
    private double handling;

    internal double Update(double seconds, bool ending, double remaining)
    {
        if (Released) return 0;
        if (Aircraft == null || Aircraft.IsDestroyed)
        { Cargo = 0; Released = true; State = "Helicopter lost"; return 0; }
        bool lowFuel = Aircraft._obp._currentFuelMass < Aircraft._obp._maxFuel * 0.2f;
        ending |= lowFuel;
        if (lowFuel && Cargo == 0) { Release(); State = "Low fuel: helicopter released"; return 0; }
        if (flight == null)
        {
            flight = new(Aircraft);
            FlyTo(job.Source);
        }
        if (!flight.Owned)
        {
            // Do not erase player orders or a native emergency/return-to-base order.
            Refund(); flight.Restore(); Released = true; State = "Helicopter order replaced; cargo returned"; return 0;
        }
        if (ending && delivering) { delivering = false; handling = 0; FlyTo(job.Source); }
        if (ending && Cargo == 0) { Release(); return 0; }
        Vessel ship = delivering ? job.Target : job.Source;
        float gap = flight.Distance;
        float stationSpeed = Math.Max(0, ship._velocityInKnots);
        flight.Speed(gap < SupplySettings.HoverToleranceNm ? stationSpeed : Math.Min(70, stationSpeed + gap * 60));
        if (gap > SupplySettings.HoverToleranceNm || Math.Abs(Aircraft._velocityInKnots - ship._velocityInKnots) > 3)
        { handling = 0; State = delivering ? "Flying to delivery" : ending ? "Returning cargo" : "Flying to pickup"; return 0; }
        if (!ship.SupplyAllowed.Value) { State = "Supply disabled in standing orders"; return 0; }
        handling += seconds;
        State = delivering ? "Unloading sling load" : "Loading sling load";
        if (handling < SupplySettings.SlingHandlingSeconds) return 0;
        handling = 0;
        if (!delivering)
        {
            if (ending) { Refund(); Release(); return 0; }
            if (!job.Stock.Transferable) { Release(); State = "Source storage unavailable"; return 0; }
            double space = SupplyInventory.Read(job.Target).Where(stock => stock.Key == job.Stock.Key && stock.Transferable).Sum(stock => stock.Free);
            Cargo = Math.Min(Math.Min(remaining, SupplySettings.SlingCapacityKg), Math.Min(job.Stock.Current, space));
            if (Cargo <= 0) { Release(); State = "No cargo or receiving capacity"; return 0; }
            job.Stock.Change(-Cargo);
            delivering = true;
            FlyTo(job.Target);
            return 0;
        }
        double delivered = 0;
        foreach (var destination in SupplyInventory.Read(job.Target).Where(stock => stock.Key == job.Stock.Key && stock.Transferable))
        {
            double part = Math.Min(Cargo, destination.Free);
            if (part <= 0) continue;
            destination.Change(part);
            Cargo -= part;
            delivered += part;
        }
        if (Cargo > 0) { State = "Holding cargo: destination full"; return delivered; }
        delivering = false;
        FlyTo(job.Source);
        return delivered;
    }

    private void FlyTo(Vessel ship) => flight!.Follow(ship, 0, 0, "UNREP helicopter station", height: 1.5f, leadNm: 0.025f);
    private void Refund()
    {
        // Cargo belongs to the source inventory until delivered. Preserve it on abort.
        if (job.Source != null && !job.Source.IsDestroyed && Cargo > 0) job.Stock.Change(Cargo);
        Cargo = 0;
    }
    internal void Release() { Refund(); flight?.Restore(); Released = true; }
}
