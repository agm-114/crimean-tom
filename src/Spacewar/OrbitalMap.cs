using MapControl;
using Noesis;
using SeaPower;
using SeaPower.SharedUi;
using SeapowerUI;
using MapPanel = MapControl.MapPanel;

namespace Spacewar;

internal class OrbitalMap : IDisposable
{
    private static readonly Brush[] Colors =
    [
        new SolidColorBrush(Color.FromRgb(233, 165, 61)),
        new SolidColorBrush(Color.FromRgb(46, 201, 223)),
        new SolidColorBrush(Color.FromRgb(210, 119, 234)),
        new SolidColorBrush(Color.FromRgb(135, 208, 93)),
    ];
    private MapBase? map;
    private MapPanel? layer;
    private readonly List<Track> tracks = [];
    private double lastSample = double.NaN;

    internal void Refresh(double seconds, Spacecraft? selected)
    {
        var current = MainGameView.Instance is { IsVisible: true } host
            ? NativeUi.Find<MapBase>(host, candidate => candidate.IsVisible) : null;
        if (current != map || layer?.Parent == null)
        {
            Dispose();
            if (current == null) return;
            map = current;
            layer = new MapPanel { Name = "SpacewarOrbitalPaths", ParentMap = map, ClipToBounds = true };
            map.Children.Add(layer);
            for (int index = 0; index < SpacecraftCatalog.Units.Count; index++)
                tracks.Add(new(SpacecraftCatalog.Units[index], Colors[index % Colors.Length], layer));
        }
        bool sample = double.IsNaN(lastSample) || seconds < lastSample || seconds - lastSample >= 5;
        tracks.ForEach(track => track.Update(seconds, selected == track.Craft, sample));
        if (sample) lastSample = seconds;
    }

    public void Dispose()
    {
        if (layer != null)
        {
            tracks.ForEach(track => track.Detach());
            map?.Children.Remove(layer);
            layer.ParentMap = null;
            layer.Children.Clear();
        }
        tracks.Clear();
        layer = null;
        map = null;
        lastSample = double.NaN;
    }

    private class Track
    {
        internal Spacecraft Craft { get; }
        private readonly Brush color;
        private readonly MapPanel layer;
        private readonly List<MapPolyline> lines = [];
        private readonly Ellipse marker;
        private readonly TextBlock label;

        internal Track(Spacecraft craft, Brush color, MapPanel layer)
        {
            Craft = craft;
            this.color = color;
            this.layer = layer;
            marker = new Ellipse
            {
                Width = 14, Height = 14, Fill = color, Stroke = Brushes.Black, StrokeThickness = 1,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            label = new TextBlock
            {
                Text = craft.Name + " orbit", Foreground = color, FontSize = 12,
                Margin = new Thickness(7, 0, 0, 5), VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            marker.MouseLeftButtonDown += Select;
            label.MouseLeftButtonDown += Select;
            layer.Children.Add(marker);
            layer.Children.Add(label);
        }

        private void Select(object sender, MouseButtonEventArgs args)
        {
            SpacewarController.Active?.Select(Craft);
            args.Handled = true;
        }

        internal void Update(double seconds, bool selected, bool sample)
        {
            if (sample)
            {
                var segments = Segments(Craft.Orbit, seconds);
                while (lines.Count < segments.Count)
                {
                    var line = new MapPolyline { Stroke = color, ParentMap = layer.ParentMap, IsHitTestVisible = false };
                    layer.Children.Insert(0, line);
                    lines.Add(line);
                }
                for (int index = 0; index < lines.Count; index++)
                {
                    lines[index].Locations = index < segments.Count ? segments[index] : Array.Empty<GeoPosition>();
                    lines[index].Visibility = index < segments.Count ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            lines.ForEach(line => line.StrokeThickness = selected ? 2.5f : 1.5f);
            var position = Craft.Position;
            var location = new GeoPosition(position.Latitude, position.Longitude);
            MapPanel.SetPosition(marker, location);
            MapPanel.SetPosition(label, location);
        }

        internal void Detach()
        {
            marker.MouseLeftButtonDown -= Select;
            label.MouseLeftButtonDown -= Select;
            lines.ForEach(line => line.ParentMap = null);
        }
    }

    private static List<List<GeoPosition>> Segments(Orbit orbit, double seconds)
    {
        const int samples = 720;
        List<List<GeoPosition>> segments = [];
        var start = orbit.At(seconds);
        var previous = new GeoPosition(start.Latitude, start.Longitude);
        List<GeoPosition> segment = [previous];
        for (int step = 1; step <= samples; step++)
        {
            var position = orbit.At(seconds + orbit.Period * step / samples);
            var next = new GeoPosition(position.Latitude, position.Longitude);
            double delta = next.Longitude - previous.Longitude;
            if (Math.Abs(delta) > 180)
            {
                // Meet both edges of the date line without drawing across the whole map.
                double unwrapped = next.Longitude - Math.Sign(delta) * 360;
                double edge = delta > 0 ? -180 : 180;
                double width = unwrapped - previous.Longitude;
                double fraction = Math.Abs(width) < 1e-10 ? 0 : (edge - previous.Longitude) / width;
                double latitude = previous.Latitude + fraction * (next.Latitude - previous.Latitude);
                segment.Add(new(latitude, edge));
                segments.Add(segment);
                segment = [new(latitude, -edge)];
            }
            else if (Math.Abs(next.Longitude - segment[0].Longitude) > 90)
            {
                // Short sections let the native map wrap each part around its own viewport.
                segments.Add(segment);
                segment = [previous];
            }
            segment.Add(next);
            previous = next;
        }
        segments.Add(segment);
        return segments;
    }
}
