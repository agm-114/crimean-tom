using Ceto;
using SeaPower;
using UnityEngine;

namespace Amphibs;

internal class AmphibiousFlotation : MonoBehaviour
{
    private Bounds hull;
    private float lastTime = -1;
    private float height;
    private float pitch;
    private float roll;
    private bool floating;

    internal void Initialize(Bounds bounds) => hull = bounds;

    internal void Apply(LandUnit unit, AmphibiousProfile profile)
    {
        float now = GameTime.CurrentMissionSessionTime;
        float elapsed = lastTime < 0 ? 0 : Mathf.Clamp(now - lastTime, 0, 1);
        lastTime = now;
        if (!AmphibiousMovement.Afloat(unit))
        {
            if (floating) transform.rotation = Quaternion.Euler(0, transform.eulerAngles.y, 0);
            floating = false;
            return;
        }
        var yaw = Quaternion.Euler(0, transform.eulerAngles.y, 0);
        var size = Vector3.Scale(hull.size, new Vector3(profile.Value("BoxScaleX"), profile.Value("BoxScaleY"), profile.Value("BoxScaleZ")));
        var centre = transform.position + yaw * new Vector3(hull.center.x, 0, hull.center.z);
        var forward = yaw * Vector3.forward;
        var right = yaw * Vector3.right;
        float bow = WaterHeight(centre + forward * size.z * 0.5f);
        float stern = WaterHeight(centre - forward * size.z * 0.5f);
        float starboard = WaterHeight(centre + right * size.x * 0.5f);
        float port = WaterHeight(centre - right * size.x * 0.5f);
        float wantedHeight = (bow + stern + starboard + port) * 0.25f;
        float wantedPitch = -Mathf.Atan2(bow - stern, Mathf.Max(0.001f, size.z)) * Mathf.Rad2Deg;
        float wantedRoll = Mathf.Atan2(starboard - port, Mathf.Max(0.001f, size.x)) * Mathf.Rad2Deg;
        // Smooth sampled slopes without letting wave pose alter land steering or feed back into the wake overlay.
        float blend = floating ? 1 - Mathf.Exp(-elapsed * 3) : 1;
        height = Mathf.Lerp(height, wantedHeight, blend);
        pitch = Mathf.Lerp(pitch, Mathf.Clamp(wantedPitch, -25, 25), blend);
        roll = Mathf.Lerp(roll, Mathf.Clamp(wantedRoll, -25, 25), blend);
        floating = true;
        var position = transform.position;
        position.y = Mathf.Max(height + Utils.heightInMeterToUnity(profile.WaterlineOffset), TerrainUtils.GetHeightAt(unit._geoPosition));
        transform.SetPositionAndRotation(position, yaw * Quaternion.Euler(pitch, 0, roll));
        unit.DesiredAltitude.Value = position.y;
        unit._geoPosition = new GeoPosition(unit._geoPosition, position.y);
        unit.LandUnitParams._objectResourcesRoot.transform.SetPositionAndRotation(position, transform.rotation);
    }

    internal static float WaterHeight(Vector3 position)
    {
        float height = Ocean.Instance ? Ocean.Instance.QueryWaves(position.x, position.z, ignoreOverlays: true) : 0;
        return float.IsFinite(height) ? height : 0;
    }
}
