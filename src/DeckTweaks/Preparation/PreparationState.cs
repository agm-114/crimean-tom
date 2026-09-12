using System.Xml.Linq;

namespace DeckTweaks.Preparation;

internal class StoreRequirement(string reference, double amount, double mountSeconds, double removeSeconds, int priority)
{
    internal string Reference { get; } = reference;
    internal double Amount { get; } = amount;
    internal double MountSeconds { get; } = mountSeconds;
    internal double RemoveSeconds { get; } = removeSeconds;
    internal int Priority { get; } = priority;
}

internal class PreparationLoadout(double refuelSeconds, IReadOnlyList<StoreRequirement> stores, double fuelCapacity = 0)
{
    internal double RefuelSeconds { get; } = refuelSeconds;
    internal IReadOnlyList<StoreRequirement> Stores { get; } = stores;
    internal double FuelCapacity { get; } = fuelCapacity;
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
    internal string Activity(Func<string, string> weaponName)
    {
        var operation = Operations().FirstOrDefault();
        return operation.Amount <= 0 ? "Ready" : operation.Reference == "" ? operation.Activity
            : $"{operation.Activity} {weaponName(operation.Reference)}";
    }

    internal string Describe(double workSpeed, bool working, string waiting, Func<string, string> weaponName)
    {
        Dictionary<string, double> completion = new(StringComparer.OrdinalIgnoreCase);
        double elapsed = 0;
        foreach (var operation in Operations())
        {
            elapsed += operation.Amount * operation.Seconds / workSpeed;
            completion[operation.Reference] = elapsed;
        }
        string queued = working ? "" : " after crew assigned";
        double total = Target.Stores.Sum(store => store.Amount * store.MountSeconds);
        double loaded = Target.Stores.Sum(store => Math.Min(store.Amount, Mounted.GetValueOrDefault(store.Reference)) * store.MountSeconds);
        List<string> lines = [Ready ? "Ready" : working ? Activity(weaponName) : waiting,
            $"Fuel: {Fuel * Target.FuelCapacity:0.0}/{Target.FuelCapacity:0.0} kg, {Math.Floor(Fuel * 100):0}%"];
        if (Fuel < 1)
        {
            double rate = Target.FuelCapacity / Target.RefuelSeconds * workSpeed;
            string service = double.IsPositiveInfinity(workSpeed) ? "instant"
                : $"{rate:0.00} kg/s, {Duration((1 - Fuel) * Target.RefuelSeconds / workSpeed)} of refueling left";
            lines.Add(working ? $"Refueling: {service}" : $"Refueling: 0 kg/s. When assigned: {service}");
        }
        lines.Add($"Target stores loaded: {(total > 0 ? Math.Floor(loaded / total * 100) : 100):0}%");
        int deviations = 0;
        foreach (StoreRequirement store in Target.Stores.OrderBy(store => store.Priority).ThenBy(store => store.Reference, StringComparer.Ordinal))
        {
            double mounted = Mounted.GetValueOrDefault(store.Reference);
            double missing = store.Amount - mounted;
            if (missing <= 0) continue;
            deviations++;
            lines.Add($"{weaponName(store.Reference)}: {Quantity(mounted)}/{Quantity(store.Amount)} loaded; "
                + $"{Quantity(missing)} missing\n  Load work: {Duration(missing * store.MountSeconds / workSpeed)}; "
                + $"loaded in {Duration(completion[store.Reference])}{queued}");
        }
        foreach (var mounted in Mounted.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            double wanted = Target.Stores.FirstOrDefault(store => store.Reference.Equals(mounted.Key, StringComparison.OrdinalIgnoreCase))?.Amount ?? 0;
            double excess = mounted.Value - wanted;
            if (excess <= 0) continue;
            deviations++;
            lines.Add($"{weaponName(mounted.Key)}: {Quantity(excess)} {(wanted > 0 ? "excess" : "incompatible")} to remove, "
                + $"{Duration(excess * knownStores[mounted.Key].RemoveSeconds / workSpeed)} unloading work\n  "
                + $"Removed in {Duration(completion[mounted.Key])}{queued}");
        }
        if (deviations == 0) lines.Add("Weapons match selected loadout.");
        return string.Join("\n", lines);
    }

    private static string Quantity(double value) => value > 0 && value < 0.01 ? "<0.01" : $"{Math.Floor(value * 100) / 100:0.##}";
    internal static string Duration(double seconds) => double.IsInfinity(seconds) || double.IsNaN(seconds) ? "unknown"
        : $"{Math.Floor(Math.Ceiling(seconds) / 60):0}m {Math.Ceiling(seconds) % 60:00}s";

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

    internal AircraftPreparation Preview(PreparationLoadout target)
    {
        AircraftPreparation preview = new(Target) { Fuel = Fuel };
        foreach (var store in knownStores) preview.knownStores[store.Key] = store.Value;
        foreach (var store in Mounted) preview.Mounted[store.Key] = store.Value;
        preview.Retarget(target);
        return preview;
    }

    private IEnumerable<(string Reference, double Amount, double Seconds, string Activity)> Operations()
    {
        if (Fuel < 1) yield return ("", 1 - Fuel, Target.RefuelSeconds, "Refueling");
        foreach (StoreRequirement store in Target.Stores.Where(store => store.Priority == 0))
        {
            double missing = store.Amount - Mounted.GetValueOrDefault(store.Reference);
            if (missing > 0) yield return (store.Reference, missing, store.MountSeconds, "Loading");
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
            if (missing > 0) yield return (store.Reference, missing, store.MountSeconds, "Loading");
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
            double removal = ValidNumber((double?)store.Attribute("RemoveSeconds") ?? 0, 86400d * 86400);
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

    internal PreparationBatch Preview(PreparationLoadout target)
    {
        PreparationBatch preview = new(0, target);
        preview.Aircraft.AddRange(Aircraft.Select(aircraft => aircraft.Preview(target)));
        return preview;
    }

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

    internal string Save() => Encode(new XElement("Preparation", Aircraft.Select(aircraft => aircraft.Save())));
    internal static string Encode(XElement value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value.ToString(SaveOptions.DisableFormatting)))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static XElement Decode(string saved)
    {
        // INI strips comment markers and splits '='; unpadded base64url avoids both.
        string encoded = saved.Replace('-', '+').Replace('_', '/');
        encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
        return XElement.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
    }

    internal void Load(string saved, PreparationLoadout loadout)
    {
        XElement root = Decode(saved);
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
