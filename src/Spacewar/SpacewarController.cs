using Noesis;
using SeaPower;
using SeaPower.SharedUi;
using SeapowerUI;
using UnityEngine;
using static SeaPower.SharedUi.NativeUi;

namespace Spacewar;

public class SpacewarController : MonoBehaviour
{
    internal List<Satellite> Satellites { get; set; } = [];
    private readonly UnitStatusPage page = new(0x5357, "Spacewar");
    private Action<double, double, double>? refresh;
    private float nextRefresh;

    private void Update()
    {
        if (Time.realtimeSinceStartup < nextRefresh) return;
        nextRefresh = Time.realtimeSinceStartup + 0.5f;
        var selected = Singleton<RenderPosition>.InstanceExists() ? Singleton<RenderPosition>.Instance.getSelectedObject() : null;
        bool available = Satellites.Count > 0 && selected != null && selected.IsPlayerUnit
            && Singleton<GameMain>.InstanceExists() && Singleton<GameMain>.Instance._initialized
            && Singleton<SceneCreator>.InstanceExists();
        page.Refresh(MainGameView.Instance, Globals._mainGameViewModel?.UnitStatus, available);
        if (!available) { page.Content = null; refresh = null; return; }
        if (page.Content == null) Open();
        // Native saved mission start plus current session preserves the orbital phase on load.
        double seconds = Singleton<SceneCreator>.Instance._missionStartTime + GameTime.missionSessionTime;
        refresh?.Invoke(seconds, selected!._geoPosition._latitude, selected._geoPosition._longitude);
    }

    private void Open()
    {
        var rows = Satellites.Select(satellite => new SatelliteRow(satellite)).ToArray();
        var table = Table("SpacewarSatellites", TextColumn("Satellite", nameof(SatelliteRow.Name), 140),
            TextColumn("Role", nameof(SatelliteRow.Role), 120), TextColumn("Alt km", nameof(SatelliteRow.Altitude), 85),
            TextColumn("Latitude", nameof(SatelliteRow.Latitude), 80), TextColumn("Longitude", nameof(SatelliteRow.Longitude), 85),
            TextColumn("Period min", nameof(SatelliteRow.Period), 90), TextColumn("Horizon", nameof(SatelliteRow.Visibility), 120));
        foreach (var row in rows) table.Items.Add(row);
        table.SelectedIndex = 0;
        var satellite = Satellites[0];
        var choice = Choice(Satellites, item => item.Name, changed: item => satellite = item);
        choice.Name = "SpacewarOrbitChoice";
        var description = Text("");
        description.Name = "SpacewarElements";
        var position = Text("");
        position.Name = "SpacewarPosition";
        var track = new GroundTrack();
        var orbit = new Grid();
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            orbit.RowDefinitions.Add(new() { Height = height });
        orbit.Children.Add(choice);
        description.Margin = new Thickness(0, 8, 0, 8);
        Grid.SetRow(description, 1);
        orbit.Children.Add(description);
        var chart = new Viewbox { Child = track.Canvas, Stretch = Stretch.Uniform };
        Grid.SetRow(chart, 2);
        orbit.Children.Add(chart);
        Grid.SetRow(position, 3);
        orbit.Children.Add(position);
        var tabs = Tabs(("Satellites", table), ("Orbit", orbit));
        tabs.Name = "SpacewarTabs";
        table.SelectionChanged += (_, _) =>
        {
            if (table.SelectedIndex >= 0) choice.SelectedIndex = table.SelectedIndex;
        };
        var summary = Text("");
        summary.Name = "SpacewarSummary";
        summary.Margin = new Thickness(0, 0, 0, 6);
        var root = new Grid { Name = "SpacewarPanel", Margin = new Thickness(5) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(summary);
        Grid.SetRow(tabs, 1);
        root.Children.Add(tabs);
        root.DataContext = root;
        page.Content = root;
        refresh = (seconds, latitude, longitude) =>
        {
            foreach (var row in rows) row.Update(seconds, latitude, longitude);
            summary.Text = $"{rows.Length} satellites · {rows.Count(row => row.AboveHorizon)} above selected unit's horizon · T+{seconds / 60:N1} min";
            var elements = satellite.Orbit;
            var current = elements.At(seconds);
            description.Text = $"{elements.Perigee:N0} × {elements.Apogee:N0} km · Inclination {elements.Inclination:N1}° · Period {elements.Period / 60:N1} min\n"
                + $"Ascending node {elements.AscendingNode:N1}° · Periapsis argument {elements.PeriapsisArgument:N1}° · Epoch mean anomaly {elements.MeanAnomaly:N1}°";
            position.Text = $"{current.Altitude:N0} km · {current.Speed:N2} km/s · Elevation {current.Elevation(latitude, longitude):N1}° from selected unit";
            if (tabs.SelectedIndex == 1) track.Draw(elements, seconds, latitude, longitude);
        };
    }

    private void OnDestroy() => page.Dispose();
}

public class SatelliteRow : System.ComponentModel.INotifyPropertyChanged
{
    private readonly Satellite satellite;
    private OrbitalPosition position;
    private double elevation;
    internal SatelliteRow(Satellite satellite) { this.satellite = satellite; position = satellite.Orbit.At(0); }
    public string Name => satellite.Name;
    public string Role => satellite.Role;
    public string Altitude => position.Altitude.ToString("N0");
    public string Latitude => $"{position.Latitude:N1}°";
    public string Longitude => $"{position.Longitude:N1}°";
    public string Period => (satellite.Orbit.Period / 60).ToString("N1");
    public string Visibility => AboveHorizon ? $"Visible {elevation:N0}°" : "Below horizon";
    internal bool AboveHorizon => elevation > 0;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    internal void Update(double seconds, double latitude, double longitude)
    {
        position = satellite.Orbit.At(seconds);
        elevation = position.Elevation(latitude, longitude);
        foreach (string property in new[] { nameof(Altitude), nameof(Latitude), nameof(Longitude), nameof(Visibility) })
            PropertyChanged?.Invoke(this, new(property));
    }
}
