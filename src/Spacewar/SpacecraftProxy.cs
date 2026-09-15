using SeaPower;
using UnityEngine;

namespace Spacewar;

internal class SpacecraftProxy : MonoBehaviour
{
    internal Spacecraft Craft { get; private set; } = null!;
    internal Aircraft Unit { get; private set; } = null!;
    internal double DisplayAltitudeKm => OrbitalDisplay.HeightKm(Craft.Position.Altitude);
    internal double AltitudeScale => DisplayAltitudeKm / Craft.Position.Altitude;
    internal double HorizontalScale { get; private set; } = 1;
    internal static SpacecraftProxy? For(ObjectBase? unit) => unit == null ? null : unit.GetComponent<SpacecraftProxy>();

    internal static SpacecraftProxy Attach(Aircraft unit, Spacecraft craft)
    {
        var proxy = For(unit) ?? unit.gameObject.AddComponent<SpacecraftProxy>();
        proxy.Unit = unit;
        proxy.Craft = craft;
        unit.setName(craft.Name);
        unit._isUndetectable = true;
        foreach (var sensor in unit._obp._sensorSystems) sensor.SetDisabled();
        proxy.Move();
        return proxy;
    }

    private static GeoPosition DisplayPosition(OrbitalPosition position, out double scale)
    {
        var center = Singleton<SceneCreator>.Instance.GeoCenterPosition;
        var display = OrbitalDisplay.Project(position.Latitude, position.Longitude, center._latitude, center._longitude);
        scale = display.Scale;
        return new(display.Latitude, display.Longitude, OrbitalDisplay.HeightInUnity(position.Altitude));
    }

    internal static GeoPosition ParkedPosition => Utils.worldPositionFromUnityToLongLat(Vector3.zero, Globals._currentCenterTile);

    internal void Move()
    {
        if (Unit.IsDestroyed) return;
        Craft.AdvanceTo(SpacewarController.MissionSeconds);
        var current = Craft.Position;
        var geo = DisplayPosition(current, out double scale);
        HorizontalScale = scale;
        var tile = Globals._currentCenterTile;
        var local = OrbitalDisplay.Local(geo._latitude, geo._longitude, tile.x, tile.y);
        var world = new Vector3((float)local.X, (float)geo._height, (float)local.Z);
        Place(geo, world);
        if (Unit.Disabled) Unit.Enable();
        var next = DisplayPosition(Craft.Predict(1), out _);
        // Difference in double precision, before the final Unity cast, keeps heading stable.
        var velocity = new Vector3(
            (float)(OrbitalDisplay.WrapLongitude(next._longitude - geo._longitude) * OrbitalDisplay.UnitsPerDegree),
            (float)(next._height - geo._height),
            (float)((next._latitude - geo._latitude) * OrbitalDisplay.UnitsPerDegree));
        // Crossing the antipodal map seam changes the pin's side, not its orbital speed.
        if (velocity.magnitude > 1000) velocity = Vector3.zero;
        if (velocity.sqrMagnitude > 0.000001f) Unit.transform.rotation = Quaternion.LookRotation(velocity);
        Unit.setVelocityInKnots(velocity.magnitude * (float)OrbitalDisplay.MetresPerUnityUnit * 1.94384449f);
        Unit._velocityVecInUnity = velocity;
        Unit._velocityInUnity = velocity.magnitude;
        Unit.Heading.Value = Unit.getHeading();
        Unit.Velocity.Value = Unit.getVelocityInKnots();
        Unit.Altitude.Value = Unit.getHeightInFeet();
        Unit.DesiredAltitude.Value = world.y;
        Place(geo, world);
    }

    private void Place(GeoPosition geo, Vector3 world)
    {
        Unit.setGeoPosition(geo);
        Unit.transform.position = world;
        var resources = Unit.Ap._objectResourcesRoot;
        if (resources != null) resources.transform.SetPositionAndRotation(world, Unit.transform.rotation);
    }
}
