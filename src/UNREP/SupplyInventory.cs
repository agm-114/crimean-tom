using System.Runtime.CompilerServices;
using HarmonyLib;
using SeaPower;

namespace UNREP;

internal class SupplyBin(string key, string name, double capacity, double amount)
{
    internal string Key { get; } = key;
    internal string Name { get; } = name;
    internal double Capacity { get; } = capacity;
    private double storedAmount = Math.Clamp(amount, 0, capacity);
    internal double Amount { get => storedAmount; set => storedAmount = Math.Clamp(value, 0, Capacity); }
    internal void Change(double delta) => Amount += delta;
}

[HarmonyPatch]
internal static class SupplyInventory
{
    private static readonly ConditionalWeakTable<Vessel, List<SupplyBin>> Bins = new();
    private static IniHandler? names;
    private static string? language;

    private static string CategoryName(string key)
    {
        string current = Singleton<LanguageResourceHandler>.Instance.Language;
        if (names == null || language != current)
        {
            language = current;
            names = LocalizationLoader.OpenVirtualLocalizationFile("language_" + current, "ammunition_names", "ammunition_names");
        }
        return names?.readValue("CategoryNames", key, key) ?? key;
    }

    internal static List<SupplyBin> Additional(Vessel ship) => Bins.GetValue(ship, unit =>
    {
        List<SupplyBin> bins = [];
        foreach (var (key, name) in new[]
        {
            ("Fuel", "Fuel reserve"), ("Food", "Food"),
            ("Water", "Fresh water"), ("SpareParts", "Spare parts"),
        })
            bins.Add(new(key, name, SupplySettings.Amount(unit, key + "Capacity"), SupplySettings.Amount(unit, key)));
        return bins;
    });

    internal static List<SupplyStock> Read(Vessel ship)
    {
        List<SupplyStock> stocks = [];
        foreach (var bin in Additional(ship))
            stocks.Add(new(bin.Key == "Fuel" ? "fuel" : "unrep/" + bin.Key,
                bin.Key == "Fuel" ? "Fuel" : "Provisions and spares", bin.Name, "UNREP inventory", "kg",
                () => bin.Amount, () => bin.Capacity, bin.Change, showWhenEmpty: true)
            { RatePerHour = () => SupplyConsumption.For(ship).Rate(bin) });

        foreach (var magazine in ship._obp._magazineSystems.Concat(ship._obp._campaignMagazineSystems).Distinct())
        foreach (var ammo in magazine._ammunitionList.Distinct())
        {
            string reference = ammo._ap._ammunitionFileName;
            stocks.Add(new("ammo/" + reference, "Ship ammunition", ammo._ap._displayedName, SupplyLabels.Location(magazine._systemName), "items",
                () => ammo._ap._magazineAmmoCount, () => magazine._initialAmmoCount.GetValueOrDefault(ammo), delta =>
                {
                    ammo._ap._magazineAmmoCount += (int)delta;
                    if (!magazine._isCampaignMagazine) ship.changeAmmunitionAmount(reference, (int)delta);
                }, () => !magazine.Inoperable.Value, showWhenEmpty: true));
        }
        foreach (var weapon in ship._obp._weaponSystems)
        foreach (var container in weapon._containers)
        {
            var ammo = container._loadedAmmunition ?? container._initialAmmunition;
            if (ammo == null) continue;
            stocks.Add(new("mounted/" + ammo._ap._ammunitionFileName, "Mounted weapons", ammo._ap._displayedName,
                SupplyLabels.Location(weapon._systemName), "items", () => container._weapons.Count, () => container._initialAmmoCount,
                showWhenEmpty: container._initialAmmoCount > 0));
        }

        if (ship._obp._flightDeck is { } deck)
        {
            stocks.Add(new("ammo-points", "Aviation stores", "Common ammunition", "Flight deck", "points",
                () => deck._currentAmmo, () => deck._ammoCapacity, delta =>
                { deck._currentAmmo += (float)delta; deck.UpdateLoadoutsAvailability(); }, () => !deck.Inoperable.Value,
                showWhenEmpty: deck._ammoCapacity > 0));
            AddCategories(stocks, deck._accountableAmmunition, deck._accountableAmmunitionCapacity,
                "Aviation stores", "Flight deck", () => deck.UpdateLoadoutsAvailability(), () => !deck.Inoperable.Value);
            foreach (var vehicle in deck._vehiclesOnBoard)
            foreach (var squadron in vehicle.Squadrons)
                stocks.Add(new("aircraft/" + vehicle._fileName, "Aircraft", vehicle.DisplayName,
                    squadron.DisplayName ?? squadron._name, "items", () => squadron.Numbers, () => double.NaN));
            foreach (var task in deck.FlightDeckTasks.OfType<PendingLaunchTask>())
                stocks.Add(new("preparing-aircraft", "Aircraft", task.AircraftType, "Preparing / ready", "items",
                    () => task.LaunchCount, () => double.NaN));
            foreach (var task in deck.FlightDeckTasks.Where(task => task is CoolDownTask))
                stocks.Add(new("servicing-aircraft", "Aircraft", task.AircraftType, "Recovery servicing", "items", () => 1, () => double.NaN));
        }
        foreach (var system in ship._obp._supplySystems)
        {
            string location = SupplyLabels.Location(system._systemName);
            stocks.Add(new("fuel", "Fuel", "Replenishment fuel", location, "kg",
                () => system.FuelCargoCurrent, () => system.FuelCargoCapacity,
                system._useLoadoutFuelAsCargo ? null : delta => system._currentFuel += (float)delta,
                () => !system.Inoperable.Value, showWhenEmpty: system.FuelCargoCapacity > 0));
            stocks.Add(new("ammo-points", "Replenishment cargo", "Common ammunition", location, "points",
                () => system._currentAmmo, () => system._ammoCapacity, delta => system._currentAmmo += (float)delta,
                () => !system.Inoperable.Value));
            AddCategories(stocks, system._accountableAmmunition, system._accountableAmmunitionCapacity,
                "Replenishment cargo", location, () => { }, () => !system.Inoperable.Value);
        }
        return stocks;
    }

    internal static string Structure(Vessel ship) => string.Join("|", ship._obp._flightDeck?.FlightDeckTasks
        .Select(task => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(task).ToString()) ?? []);

    internal static IEnumerable<SupplyStock> Transferable(Vessel ship, string key) =>
        Read(ship).Where(stock => stock.Key == key && stock.Transferable);

    internal static List<SupplyStock> Stacked(Vessel ship) => Read(ship)
        .GroupBy(stock => new { stock.Key, stock.Category, stock.Name, stock.Unit, stock.CanChange })
        .Select(group =>
        {
            var stock = SupplyStock.Stack(group.ToArray());
            stock.Minimum = () => SupplyPolicy.For(ship).Minimum(stock.Key);
            return stock;
        }).ToList();

    private static void AddCategories(List<SupplyStock> stocks, Dictionary<string, int> current,
        Dictionary<string, int> capacities, string category, string location, Action refresh, Func<bool> usable)
    {
        foreach (string key in current.Keys.Union(capacities.Keys).ToArray())
            stocks.Add(new("category/" + key, category, CategoryName(key), location, "items",
                () => current.GetValueOrDefault(key), () => capacities.GetValueOrDefault(key), delta =>
                { current[key] = current.GetValueOrDefault(key) + (int)delta; refresh(); }, usable,
                showWhenEmpty: category == "Aviation stores" && capacities.GetValueOrDefault(key) > 0));
    }

    [HarmonyPostfix, HarmonyPatch(typeof(Vessel), nameof(Vessel.SaveStateToFile))]
    private static void Save(Vessel __instance, IniHandler ini, string sectionName)
    {
        SupplyPolicy.For(__instance).Save(ini, sectionName);
        if (Bins.TryGetValue(__instance, out var bins))
            foreach (var bin in bins)
            {
                double cargo = VertrepOperation.AirborneCargo(__instance, "unrep/" + bin.Key);
                ini.writeValue(sectionName, "UNREP_" + bin.Key, (float)(bin.Amount + cargo));
            }
    }

    [HarmonyPostfix, HarmonyPatch(typeof(Vessel), nameof(Vessel.LoadStateFromFile))]
    private static void Load(Vessel __instance, IniHandler ini, string sectionName)
    {
        SupplyPolicy.For(__instance).Load(ini, sectionName);
        foreach (var bin in Additional(__instance))
        {
            double amount = ini.readValue(sectionName, "UNREP_" + bin.Key, (float)bin.Amount);
            if (double.IsFinite(amount)) bin.Amount = Math.Clamp(amount, 0, bin.Capacity);
        }
    }
}
