using SeaPower;
using Ceto;
using UnityEngine;

namespace Amphibs;

internal class AmphibiousWake : MonoBehaviour
{
    private LandUnit unit = null!;
    private AmphibiousProfile profile = null!;
    private readonly List<(ParticleSystem Particle, Transform Root, string Position, string Scale, float Speed)> particles = [];
    private Transform water = null!;
    private Bounds hullBounds;
    private Vector3 Box => Vector3.Scale(hullBounds.size, new Vector3(Value("BoxScaleX"), Value("BoxScaleY"), Value("BoxScaleZ")));
    private AddWaveOverlay surface = null!;
    private AddFoamTrail foam = null!;
    private AddFoamTrail hullFoam = null!;

    internal void Initialize(LandUnit owner, AmphibiousProfile settings, LandUnitParameters parameters)
    {
        unit = owner;
        profile = settings;
        hullBounds = MeasureHull(parameters);
        owner.gameObject.AddComponent<AmphibiousFlotation>().Initialize(hullBounds);
        water = new GameObject("Amphibs wake").transform;
        water.SetParent(transform, false);
        CreateSurface();
        Create("ships/particles/bowwave_v_small", "BowPosition", "BowSpray");
        Create("ships/particles/propwash_v_small", "SternPosition", "Propwash");
    }

    private Bounds MeasureHull(LandUnitParameters parameters)
    {
        Bounds? measured = null;
        // Measure the assembled, undamaged hull, not the resource container's spare meshes and LODs.
        parameters._objectsDictionary.TryGetValue(parameters._objectMeshName, out var hull);
        var mesh = hull ? hull.GetComponent<MeshFilter>() : null;
        if (mesh && mesh.sharedMesh)
        {
            var bounds = mesh.sharedMesh.bounds;
            for (int corner = 0; corner < 8; corner++)
            {
                var local = bounds.center + Vector3.Scale(bounds.extents,
                    new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1));
                var point = transform.InverseTransformPoint(mesh.transform.TransformPoint(local));
                var combined = measured ?? new Bounds(point, Vector3.zero);
                combined.Encapsulate(point);
                measured = combined;
            }
        }
        if (measured is { } result && result.size.x > 0 && result.size.y > 0 && result.size.z > 0)
        {
            Debug.Log($"[Amphibs] {profile.Id} wake box: size={result.size.ToString("F4")}, centre={result.center.ToString("F4")} Unity units.");
            return result;
        }
        Debug.LogWarning($"[Amphibs] No mesh bounds for {profile.Id}; using catalogue dimensions.");
        return new Bounds(Vector3.zero, new Vector3(Utils.heightInMeterToUnity(profile.Length * 0.4f),
            Utils.heightInMeterToUnity(2.5f), Utils.heightInMeterToUnity(profile.Length)));
    }

    private void Create(string resource, string position, string scale)
    {
        Singleton<ResourceLoader>.Instance.RequestGameObjectResource(resource, prefab =>
        {
            if (!prefab || !this) return;
            var effect = Instantiate(prefab, water, false);
            effect.transform.localRotation = Quaternion.identity;
            effect.transform.localScale = Vector3.one * profile.WakeScale * 0.45f;
            effect.SetActive(true);
            ParticleUtils.SetAutoDestroyAndCustomWorldSpace(effect, autodestroy: false);
            foreach (var particle in effect.GetComponentsInChildren<ParticleSystem>(true))
            {
                particle.gameObject.layer = 9;
                var main = particle.main;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                particle.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                particles.Add((particle, effect.transform, position, scale, main.startSpeedMultiplier));
            }
        }, async: false);
    }

    private void CreateSurface()
    {
        surface = new GameObject("Water displacement").AddComponent<AddWaveOverlay>();
        surface.transform.SetParent(water, false);
        surface.width = Utils.heightInMeterToUnity(profile.Length * 2.6f * profile.WakeScale);
        surface.height = Utils.heightInMeterToUnity(profile.Length * 3.9f * profile.WakeScale);
        surface.transform.localPosition = new Vector3(0, 0, -Utils.heightInMeterToUnity(profile.Length * 0.35f));
        surface.heightTexture.tex = Texture("bowwave_hm");
        surface.heightTexture.mask = surface.heightTexture.tex;
        surface.heightTexture.maskMode = OVERLAY_MASK_MODE.WAVES;
        surface.normalTexture.tex = Texture("bowwave_normal");
        surface.normalTexture.mask = surface.normalTexture.tex;
        surface.normalTexture.maskMode = OVERLAY_MASK_MODE.OVERLAY;
        surface.foamTexture.tex = Texture("bowwave_tx2");
        surface.foamTexture.mask = Texture("bowwave_mask");
        surface.foamTexture.maskMode = OVERLAY_MASK_MODE.WAVES;
        surface.heightTexture.alpha = surface.normalTexture.alpha = surface.foamTexture.alpha = 0;

        foam = new GameObject("Stern foam").AddComponent<AddFoamTrail>();
        foam.transform.SetParent(water, false);
        foam.transform.localPosition = new Vector3(0, 0, -Utils.heightInMeterToUnity(profile.Length * 0.45f));
        foam.foamTexture = Texture("Ceto_Propwash");
        foam.rotation = AddFoamTrail.ROTATION.RELATIVE;
        foam.size = 1.3f * Mathf.Max(0.1f, Utils.heightInMeterToUnity(profile.Length * profile.WakeScale));
        foam.spacing = 0.1f; // Native minimum is about 6.7 metres.
        foam.expansion = Utils.heightInMeterToUnity(0.15f);
        foam.momentum = foam.spin = 0;
        foam.jitter = 0.1f;
        foam.mustBeBelowWater = false;
        foam.duration = 0;
        hullFoam = new GameObject("Hull foam").AddComponent<AddFoamTrail>();
        hullFoam.transform.SetParent(water, false);
        hullFoam.foamTexture = Texture("Ceto_Hullwake");
        hullFoam.rotation = AddFoamTrail.ROTATION.RELATIVE;
        hullFoam.mustBeBelowWater = false;
        hullFoam.duration = 0;
        hullFoam.momentum = hullFoam.spin = 0;
        hullFoam.jitter = 0.1f;
    }

    private static Texture2D Texture(string name) =>
        Singleton<ResourceLoader>.Instance.getTexture2dResource("ships/particles/wake/" + name);

    private void LateUpdate()
    {
        if (!unit || unit._obp == null || !water) return;
        var position = transform.position;
        water.position = new Vector3(position.x, 0, position.z);
        water.rotation = Quaternion.Euler(0, transform.eulerAngles.y, 0);
        bool swimming = !unit.IsDestroyed && AmphibiousMovement.Afloat(unit);
        float strength = swimming && profile.WakeScale > 0 ? Mathf.Clamp01((Mathf.Abs(unit._velocityInKnots) - profile.Speed * Value("MinEffectSpeed"))
            / Mathf.Max(0.1f, profile.Speed * (Value("MaxEffectSpeed") - Value("MinEffectSpeed")))) : 0;
        if (surface)
        {
            surface.width = Box.x * Value("WakeWidth") * profile.WakeScale;
            surface.height = Distance("WakeLength") * profile.WakeScale;
            surface.transform.localPosition = new Vector3(hullBounds.center.x, 0, hullBounds.center.z + Distance("WakePosition"));
            surface.heightTexture.alpha = Box.y * Value("WaveHeightCoefficient") * strength * Value("DisplacementEnabled");
            surface.normalTexture.alpha = Value("NormalStrength") * strength * Value("RipplesEnabled");
            surface.foamTexture.alpha = Value("SurfaceFoam") * strength * Value("SurfaceFoamEnabled");
            UpdateTrail(foam, "SternFoam", Value("SternFoamOpacity") * Value("SternFoamEnabled"), strength);
            UpdateTrail(hullFoam, "HullFoam", Value("HullFoam") * Value("HullFoamEnabled"), strength);
        }
        foreach (var effect in particles)
        {
            var particle = effect.Particle;
            effect.Root.localPosition = new Vector3(hullBounds.center.x,
                Box.y * Value(effect.Scale + "Y"), hullBounds.center.z
                    + Distance(effect.Position) * (effect.Position == "BowPosition" ? 0.5f : 1f));
            var sprayPosition = effect.Root.position;
            sprayPosition.y += AmphibiousFlotation.WaterHeight(sprayPosition);
            effect.Root.position = sprayPosition;
            effect.Root.localScale = Vector3.one * (Box.x / Utils.heightInMeterToUnity(3)) * profile.WakeScale * Value("SprayScale") * Value(effect.Scale);
            var main = particle.main;
            main.startSpeedMultiplier = effect.Speed * Value("SpraySpeed") * Value(effect.Scale + "Speed");
            main.startColor = new Color(1, 1, 1, strength);
            if (strength > 0.02f && Value(effect.Scale + "Enabled") > 0 && Value(effect.Scale) > 0 && Value("SprayScale") > 0)
            {
                if (!particle.isPlaying) particle.Play(false);
            }
            else if (particle.isPlaying) particle.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
        }
    }

    private float Value(string key) => profile.Value(key);
    private float Distance(string key) => Box.z * Value(key);
    private void UpdateTrail(AddFoamTrail trail, string prefix, float opacity, float strength)
    {
        trail.transform.localPosition = new Vector3(hullBounds.center.x,
            Box.y * Value(prefix + "Y"), hullBounds.center.z + Distance(prefix + "Position"));
        trail.size = Mathf.Max(0.1f, Value(prefix + "Size") * Box.x * profile.WakeScale);
        trail.spacing = Mathf.Max(0.1f, Box.z * Value(prefix + "Spacing"));
        trail.expansion = Box.x * Value(prefix + "Expansion");
        trail.momentum = Box.z * Value(prefix + "Momentum");
        trail.spin = Value(prefix + "Spin");
        trail.jitter = Value(prefix + "Jitter");
        trail.duration = strength > 0.02f && opacity > 0 ? Value(prefix + "Duration") : 0;
        trail.alpha = opacity * strength;
    }
}
