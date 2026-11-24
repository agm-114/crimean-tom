using Noesis;
using SeaPower;
using UI;
using SeapowerUI;
using UnityEngine;
using static UI.NativeUi;

namespace Spacewar;

public class SpacewarController : MonoBehaviour
{
    internal static SpacewarController? Active { get; private set; }
    internal static double MissionSeconds => Singleton<SceneCreator>.Instance._missionStartTime + GameTime.missionSessionTime;
    private readonly UnitStatusPage page = new(0x5357, "Orbit");
    private readonly OrbitalMap orbitalMap = new();
    private readonly List<SpacecraftProxy> proxies = [];
    private ObjectsManager? mission;
    private float nextRefresh;

    private void Awake() => Active = this;

    internal void MoveSpacecraft() => proxies.ForEach(proxy => { if (proxy != null) proxy.Move(); });

    internal void Select(Spacecraft craft)
    {
        var proxy = proxies.Find(item => item != null && item.Craft == craft);
        if (proxy == null || proxy.Unit.IsDestroyed) return;
        proxy.Move();
        Singleton<RenderPosition>.Instance.switchToObject(proxy.Unit, performSmoothMove: false, checkAirLeader: false);
    }

    private void LateUpdate()
    {
        bool available = Singleton<GameMain>.InstanceExists() && Singleton<GameMain>.Instance._initialized
            && Singleton<SceneCreator>.InstanceExists() && Singleton<ObjectsManager>.InstanceExists() && !Globals._inMuseum;
        if (!available)
        {
            mission = null;
            proxies.Clear();
            page.Dispose();
            orbitalMap.Dispose();
            OrbitalAtmosphere.Restore();
            return;
        }
        double seconds = MissionSeconds;
        SpacecraftCatalog.Units.ForEach(craft => craft.AdvanceTo(seconds));
        var manager = Singleton<ObjectsManager>.Instance;
        if (mission != manager)
        {
            mission = manager;
            CreateProxies(manager);
        }
        var selected = Singleton<RenderPosition>.InstanceExists() ? Singleton<RenderPosition>.Instance.getSelectedObject() : null;
        var current = SpacecraftProxy.For(selected);
        bool canInspect = current != null && !current.Unit.Disabled && selected!.IsPlayerUnit;
        page.Refresh(MainGameView.Instance, Globals._mainGameViewModel?.UnitStatus, canInspect);
        if (canInspect && page.SetContent(current!, Build)) page.RefreshContent();
        if (Time.realtimeSinceStartup < nextRefresh) return;
        nextRefresh = Time.realtimeSinceStartup + 0.5f;
        orbitalMap.Refresh(seconds, current?.Craft);
        page.RefreshContent();
    }

    private void CreateProxies(ObjectsManager manager)
    {
        proxies.Clear();
        var existing = manager._listOfAllObjects.Concat(Globals._playerTaskforce._taskforceDisabledUnits)
            .Select(SpacecraftProxy.For).OfType<SpacecraftProxy>().ToArray();
        foreach (var craft in SpacecraftCatalog.Units)
        {
            var restored = existing.FirstOrDefault(proxy => proxy.Craft == craft);
            if (restored != null) { proxies.Add(restored); continue; }
            try
            {
                var unit = manager.createAircraft("Squadron1", "Default", null, null, "civ_h700",
                    manager._listOfAllObjects.Count + 1, Singleton<SceneCreator>.Instance.GeoCenterPosition,
                    SpacecraftProxy.ParkedPosition, Vector3.zero,
                    Globals._playerTaskforce, createMinimapSymbol: true, loadWeaponSystems: false, enableColliders: false);
                if (unit == null) throw new InvalidOperationException("Native aircraft creation returned null.");
                proxies.Add(SpacecraftProxy.Attach(unit, craft));
                Debug.Log($"[Spacewar] Created native proxy for {craft.Name} ({unit.UniqueID}).");
            }
            catch (Exception error) { Debug.LogError($"[Spacewar] Could not create {craft.Name}: {error}"); }
        }
    }

    private static (FrameworkElement, Action) Build(SpacecraftProxy proxy)
    {
        var description = Text("");
        description.Name = "SpacewarElements";
        var position = Text("");
        position.Name = "SpacewarPosition";
        var track = new GroundTrack();
        position.Margin = new Thickness(0, 8, 0, 8);
        var chart = new Viewbox { Child = track.Canvas, Stretch = Stretch.Uniform };
        var root = FillPanel("SpacewarPanel", 5, Stack(description, position), chart);
        root.DataContext = root;
        return (root, Refresh);

        void Refresh()
        {
            var craft = proxy.Craft;
            var elements = craft.Orbit;
            double seconds = craft.SimulationSeconds;
            var current = craft.Position;
            description.Text = $"{craft.Name} · {craft.Role}\n"
                + $"{elements.Perigee:N0} × {elements.Apogee:N0} km · Inclination {elements.Inclination:N1}° · Period {elements.Period / 60:N1} min\n"
                + $"Ascending node {elements.AscendingNode:N1}° · Periapsis argument {elements.PeriapsisArgument:N1}° · Epoch mean anomaly {elements.MeanAnomaly:N1}°";
            position.Text = $"Orbital altitude {current.Altitude:N0} km · Orbital speed {current.Speed:N2} km/s\n"
                + $"Latitude {current.Latitude:N2}° · Longitude {current.Longitude:N2}°\n"
                + $"Display altitude {proxy.DisplayAltitudeKm:N1} km · Altitude scale {proxy.AltitudeScale:N4}\n"
                + $"Horizontal scale {proxy.HorizontalScale:N4} · {(proxy.HorizontalScale < 0.9999 ? "Pinned toward map boundary" : "Accurate ground position")}";
            track.Draw(elements, seconds);
        }
    }

    private void OnDestroy()
    {
        if (Active == this) Active = null;
        OrbitalAtmosphere.Restore();
        orbitalMap.Dispose();
        page.Dispose();
    }
}
