using System.Xml.Linq;

namespace FlightDeckTweaks.Preparation;

internal class StoreRequirement(string reference, double amount, double mountSeconds, double removeSeconds, int priority)
{
    internal string Reference { get; } = reference;
    internal double Amount { get; } = amount;
    internal double MountSeconds { get; } = mountSeconds;
    internal double RemoveSeconds { get; } = removeSeconds;
    internal int Priority { get; } = priority;
}

internal class PreparationLoadout(double refuelSeconds, IReadOnlyList<StoreRequirement> stores)
{
    internal double RefuelSeconds { get; } = refuelSeconds;
    internal IReadOnlyList<StoreRequirement> Stores { get; } = stores;
}

// Pure preparation state. No game objects, inventory mutations, timers or launch commands.
internal class AircraftPreparation
{
    internal double Fuel { get; private set; }
    internal Dictionary<string, double> Mounted { get; } = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StoreRequirement> knownStores = new(StringComparer.OrdinalIgnoreCase);
    internal PreparationLoadout Target { get; private set; }
    internal double RemainingWork => Operations().Sum(operation => operation.Amount * operation.Seconds);
    internal bool Ready => RemainingWork <= 0;
    internal string Activity => Operations().Select(operation => operation.Activity).FirstOrDefault() ?? "Ready";

    internal AircraftPreparation(PreparationLoadout target)
    {
        Target = target;
        Retarget(target);
    }

    internal void Retarget(PreparationLoadout target)
    {
        Target = target;
        foreach (StoreRequirement store in target.Stores) knownStores[store.Reference] = store;
    }

    private IEnumerable<(string Reference, double Amount, double Seconds, string Activity)> Operations()
    {
        if (Fuel < 1) yield return ("", 1 - Fuel, Target.RefuelSeconds, "Refueling");
        foreach (StoreRequirement store in Target.Stores.Where(store => store.Priority == 0))
        {
            double missing = store.Amount - Mounted.GetValueOrDefault(store.Reference);
            if (missing > 0) yield return (store.Reference, missing, store.MountSeconds, "Loading Built-ins");
        }
        foreach (var mounted in Mounted.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            double wanted = Target.Stores.FirstOrDefault(store => store.Reference.Equals(mounted.Key, StringComparison.OrdinalIgnoreCase))?.Amount ?? 0;
            if (mounted.Value > wanted)
                yield return (mounted.Key, mounted.Value - wanted, knownStores[mounted.Key].RemoveSeconds, "Unloading");
        }
        foreach (StoreRequirement store in Target.Stores.Where(store => store.Priority != 0)
            .OrderBy(store => store.Priority).ThenBy(store => store.Reference, StringComparer.Ordinal))
        {
            double missing = store.Amount - Mounted.GetValueOrDefault(store.Reference);
            if (missing > 0) yield return (store.Reference, missing, store.MountSeconds, "Loading Weapons");
        }
    }

    internal void Advance(double seconds)
    {
        while (seconds > 0)
        {
            var operation = Operations().FirstOrDefault();
            if (operation.Amount <= 0) break;
            double work = Math.Min(seconds, operation.Amount * operation.Seconds);
            double amount = work / operation.Seconds;
            if (operation.Reference == "") Fuel = work >= operation.Amount * operation.Seconds ? 1 : Fuel + amount;
            else
            {
                double before = Mounted.GetValueOrDefault(operation.Reference);
                double after = before + (operation.Activity == "Unloading" ? -amount : amount);
                if (work >= operation.Amount * operation.Seconds)
                    after = Target.Stores.FirstOrDefault(store => store.Reference.Equals(operation.Reference, StringComparison.OrdinalIgnoreCase))?.Amount ?? 0;
                if (after <= 0) Mounted.Remove(operation.Reference);
                else Mounted[operation.Reference] = after;
            }
            seconds -= work;
        }
    }

    internal void Complete()
    {
        Fuel = 1;
        Mounted.Clear();
        foreach (StoreRequirement store in Target.Stores) Mounted[store.Reference] = store.Amount;
    }

    internal XElement Save() => new("Aircraft", new XAttribute("Fuel", Fuel), Mounted.Select(pair =>
        new XElement("Store", new XAttribute("Ref", pair.Key), new XAttribute("Amount", pair.Value),
            new XAttribute("RemoveSeconds", knownStores[pair.Key].RemoveSeconds))));

    internal void Load(XElement saved)
    {
        Fuel = ValidNumber((double?)saved.Attribute("Fuel") ?? 0, 1);
        Mounted.Clear();
        foreach (XElement store in saved.Elements("Store"))
        {
            string reference = (string?)store.Attribute("Ref") ?? throw new FormatException("Missing store reference");
            double amount = ValidNumber((double?)store.Attribute("Amount") ?? 0, 1000000);
            double removal = ValidNumber((double?)store.Attribute("RemoveSeconds") ?? 0, 86400);
            if (reference.Length == 0 || removal == 0) throw new FormatException("Invalid store data");
            knownStores[reference] = new(reference, amount, removal, removal, 1);
            Mounted.Add(reference, amount);
        }
        Retarget(Target);
    }

    private static double ValidNumber(double value, double maximum) =>
        double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > maximum
            ? throw new FormatException("Invalid preparation quantity") : value;
}

internal class PreparationBatch(int count, PreparationLoadout target)
{
    internal List<AircraftPreparation> Aircraft { get; } = Enumerable.Range(0, count).Select(_ => new AircraftPreparation(target)).ToList();
    internal double RemainingWork => Aircraft.Sum(aircraft => aircraft.RemainingWork);
    internal int ReadyCount => Aircraft.Count(aircraft => aircraft.Ready);
    internal void Retarget(PreparationLoadout loadout) => Aircraft.ForEach(aircraft => aircraft.Retarget(loadout));
    internal void Complete() => Aircraft.ForEach(aircraft => aircraft.Complete());

    internal double EstimatedSeconds(int crews)
    {
        if (crews <= 0) return double.PositiveInfinity;
        double[] availability = new double[Math.Min(crews, Math.Max(1, Aircraft.Count))];
        foreach (AircraftPreparation aircraft in Aircraft.Where(aircraft => !aircraft.Ready))
            availability[Array.IndexOf(availability, availability.Min())] += aircraft.RemainingWork;
        return availability.Max();
    }

    internal void Advance(double seconds, int crews)
    {
        while (seconds > 0 && crews > 0)
        {
            AircraftPreparation[] working = Aircraft.Where(aircraft => !aircraft.Ready).Take(crews).ToArray();
            if (working.Length == 0) break;
            double step = Math.Min(seconds, working.Min(aircraft => aircraft.RemainingWork));
            foreach (AircraftPreparation aircraft in working) aircraft.Advance(step);
            seconds -= step;
        }
    }

    internal string Save() => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
        new XElement("Preparation", Aircraft.Select(aircraft => aircraft.Save())).ToString(SaveOptions.DisableFormatting)))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal void Load(string saved, PreparationLoadout loadout)
    {
        // INI strips comment markers and splits '='; unpadded base64url avoids both.
        string encoded = saved.Replace('-', '+').Replace('_', '/');
        encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
        XElement root = XElement.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        XElement[] units = root.Elements("Aircraft").ToArray();
        if (root.Name != "Preparation" || units.Length != Aircraft.Count) throw new FormatException("Preparation count mismatch");
        List<AircraftPreparation> restored = [];
        foreach (XElement unit in units)
        {
            AircraftPreparation aircraft = new(loadout);
            aircraft.Load(unit);
            restored.Add(aircraft);
        }
        Aircraft.Clear();
        Aircraft.AddRange(restored);
    }
}
