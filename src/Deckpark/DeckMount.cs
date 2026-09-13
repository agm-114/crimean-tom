using SeaPower;
using UnityEngine;

namespace Deckpark;

public class DeckMount : MonoBehaviour
{
    internal LandUnit Unit { get; set; } = null!;
    internal int ParentId { get; set; }
    internal int Slot { get; set; }
    internal Vector3 Offset { get; set; }
    internal float Heading { get; set; }
    internal bool WeaponsEnabled { get; set; }
    private Vessel? parent;
    private bool collisionPairsSet;
    internal bool Ready => parent != null && !parent.IsDestroyed;

    internal void ApplyPose()
    {
        if (Unit == null) return;
        Unit._correctAltitudeOnSpawn = false;
        Unit._performAutoFlatten = Unit._maskVegetation = false;
        // A bolted-down vehicle is a stationary weapon mount relative to the deck.
        // Native mobile-launch restrictions must not mistake ship speed for driving.
        Unit._isMobile = Unit._isMobileAndCanMove = false;
        if (Unit.RigidBody is { } body) { body.useGravity = false; body.isKinematic = true; }
        if (parent == null && collisionPairsSet)
        {
            if (!Unit.IsDestroyed) Unit.destroyObject();
            return;
        }
        if (parent == null && Singleton<ObjectsManager>.InstanceExists())
            parent = Singleton<ObjectsManager>.Instance._listOfAllObjects.OfType<Vessel>().FirstOrDefault(ship => ship.UniqueID == ParentId);
        if (parent == null) return;
        if (parent.IsDestroyed)
        {
            if (!Unit.IsDestroyed) Unit.destroyObject();
            return;
        }
        if (!collisionPairsSet)
        {
            foreach (var own in Unit.GetComponentsInChildren<Collider>())
                foreach (var hull in parent.GetComponentsInChildren<Collider>()) Physics.IgnoreCollision(own, hull);
            collisionPairsSet = true;
            var slot = DeckOperations.Slots(parent).FirstOrDefault(item => item.Number == Slot);
            if (slot != null) slot.Used = true;
        }
        Vector3 displacement = parent.transform.rotation * (Offset * Utils.heightInMeterToUnity(1));
        transform.SetPositionAndRotation(parent.transform.position + displacement,
            parent.transform.rotation * Quaternion.Euler(0, Heading, 0));
        var position = parent._geoPosition;
        position._latitude += Utils.unityHeightToMeter(displacement.z) / 1852d / 60;
        position._longitude += Utils.unityHeightToMeter(displacement.x) / 1852d / 60
            / Math.Max(0.01, Math.Cos(position._latitude * Math.PI / 180));
        position._height = transform.position.y;
        Unit._geoPosition = position;
        Unit.SetTeleportProtection();
        Unit._velocityInKnots = parent._velocityInKnots;
        Unit._velocityVecInUnity = parent._velocityVecInUnity;
        Unit.DesiredAltitude.Value = transform.position.y;
        Unit.LandUnitParams._dustEffectPS?.Stop();
    }

    private void LateUpdate() => ApplyPose();

    internal void SetWeapons(bool enabled)
    {
        WeaponsEnabled = enabled;
        Unit.SetWeaponStatus(enabled ? ObjectBase.WeaponStatus.Free : ObjectBase.WeaponStatus.Hold);
        if (!enabled)
            foreach (var ciws in Unit._obp._weaponSystems.OfType<WeaponSystemCIWS>()) ciws.StopFire();
    }

    internal void SetRadar(bool enabled)
    {
        Unit.setEMCON(!enabled);
        foreach (var radar in Unit._obp._airSearchRadars.Concat(Unit._obp._surfaceSearchRadars).Distinct())
        {
            if (enabled) radar.Enable(allowMessage: false);
            else radar.Disable();
        }
    }
}
