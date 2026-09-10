using SeaPower;

namespace SeaPower.UiHelper;

internal static class UnitSnapshot
{
    internal static object Read()
    {
        TimeStatus time = TimeControls.Read();
        List<object> units = [];
        if (time.Available && Singleton<ObjectsManager>.InstanceExists())
            foreach (ObjectBase unit in Singleton<ObjectsManager>.Instance._listOfAllObjects)
                if (unit != null && unit.isUnit() && unit._obp != null) units.Add(Describe(unit));
        return new { snapshot = Guid.NewGuid().ToString("N"), identityVersion = 1, time, units };
    }

    private static object Describe(ObjectBase unit)
    {
        GeoPosition position = unit._geoPosition;
        ObjectBaseParameters parameters = unit._obp;
        return new
        {
            id = unit.GetInstanceID(), name = unit.getName(), type = unit.GetType().Name,
            unitClass = parameters._objectIniName, variant = parameters._objectVariantReference,
            nation = unit.Nation.Value, taskforce = unit._taskforce?._nameInMissionFile, side = unit._taskforce?.Side.ToString(),
            playerControlled = unit._taskforce != null && unit.IsPlayerUnit && unit._hasControl,
            destroyed = unit.IsDestroyed,
            position = new { latitude = Round(position._latitude, 6), longitude = Round(position._longitude, 6), altitudeMeters = Round(position._height, 1) },
            headingDegrees = Round(unit.Heading.Value, 1), speedKnots = Round(unit._velocityInKnots, 1),
            loadout = parameters._loadoutVariant,
            fuelKg = unit is Aircraft ? Round(parameters._currentFuelMass, 1) : null,
        };
    }

    private static double? Round(double value, int digits) => double.IsNaN(value) || double.IsInfinity(value) ? null : Math.Round(value, digits);
}
