using MapControl;
using Newtonsoft.Json.Linq;
using SeaPower;
using SeapowerUI.ViewModels;

namespace SeaPower.GameBridge;

internal enum ChartMarkKind { Objective, ReferencePoint, SearchArea }

internal record ChartMark(ChartMarkKind Kind, string Label, GeoPosition Position, float? RadiusNm = null)
{
    internal JObject Describe() => new()
    {
        ["kind"] = Kind.ToString(), ["label"] = Label, ["position"] = PlayerPicture.Point(Position),
        ["radiusNm"] = RadiusNm
    };
}

internal class PlayerChart
{
    private MapDrawingLayer? layer;
    private readonly List<IDrawing> drawings = [];

    internal bool Replace(Taskforce side, IEnumerable<ChartMark> marks)
    {
        if (PlayerPicture.Side != side || PlayerTime.Blocked || Globals._mainGameViewModel?.Drawings is not { } currentLayer) return false;
        Clear();
        layer = currentLayer;
        foreach (ChartMark mark in marks)
        {
            DrawingColorPreset color = mark.Kind switch
            {
                ChartMarkKind.Objective => DrawingColorPreset.Yellow,
                ChartMarkKind.SearchArea => DrawingColorPreset.Orange,
                _ => DrawingColorPreset.Cyan
            };
            MarkerDrawing center = new() { Location = mark.Position, ColorPreset = color };
            Add(center);
            Add(new TextDrawing(mark.Label) { Location = mark.Position, ColorPreset = color });
            if (mark.Kind == ChartMarkKind.SearchArea && mark.RadiusNm is > 0 and <= 150)
            {
                MarkerDrawing edge = new() { Location = mark.Position.GeoOffsetByDistanceOnHeading(mark.RadiusNm.Value, 90) };
                Add(new RingDrawing { Center = center, Extremis = edge, ColorPreset = color });
            }
        }
        return true;
    }

    private void Add(IDrawing drawing)
    {
        drawings.Add(drawing);
        layer!.Graphics.Add(drawing);
    }

    internal void Clear()
    {
        if (layer != null)
            for (int index = drawings.Count - 1; index >= 0; index--) layer.Graphics.Remove(drawings[index]);
        drawings.Clear();
        layer = null;
    }
}

internal record FlightMapOrder(string UnitId, string Name, string Role, GeoPosition Position, GeoPosition LabelPosition,
    string? TargetId = null, GeoPosition? Target = null)
{
    internal JObject Describe() => new()
    {
        ["unitId"] = UnitId, ["name"] = Name, ["role"] = Role, ["position"] = PlayerPicture.Point(Position),
        ["labelPosition"] = PlayerPicture.Point(LabelPosition), ["targetId"] = TargetId,
        ["targetPosition"] = Target is { } point ? PlayerPicture.Point(point) : null
    };
}

internal record FlightChartAssignment(string Role, string? TargetId, double IssuedAt);

internal class FlightOrderChart
{
    private record Drawing(TextDrawing Label, RulerDrawing? Line, MarkerDrawing? Start, MarkerDrawing? End);
    private MapDrawingLayer? layer;
    private readonly Dictionary<string, Drawing> drawings = [];

    internal void Update(IEnumerable<FlightMapOrder> orders)
    {
        MapDrawingLayer? currentLayer = Globals._mainGameViewModel?.Drawings;
        if (layer != currentLayer) Clear();
        layer = currentLayer;
        if (layer == null) return;

        HashSet<string> active = [];
        foreach (FlightMapOrder order in orders)
        {
            string key = $"{order.UnitId}:{order.Role}:{order.TargetId}";
            active.Add(key);
            if (!drawings.TryGetValue(key, out Drawing? drawing))
            {
                DrawingColorPreset color = order.Role is "CAP" or "Intercept" ? DrawingColorPreset.Cyan : DrawingColorPreset.Orange;
                TextDrawing label = new($"{order.Name} · {order.Role}")
                {
                    ColorPreset = color, Location = order.LabelPosition, FontSize = 12,
                    HorizontalAnchor = Noesis.HorizontalAlignment.Right, VerticalAnchor = Noesis.VerticalAlignment.Bottom
                };
                MarkerDrawing? start = order.Target.HasValue ? new MarkerDrawing { Location = order.Position } : null;
                MarkerDrawing? end = order.Target is { } target ? new MarkerDrawing { Location = target } : null;
                RulerDrawing? line = start != null && end != null ? new RulerDrawing
                {
                    ColorPreset = color, DistanceLocation = DistanceLocation.NONE, ShowHeading = false, Splittable = false,
                    Start = start, End = end
                } : null;
                drawings[key] = drawing = new(label, line, start, end);
                layer.Graphics.Add(label);
                if (line != null) layer.Graphics.Add(line);
            }
            drawing.Label.Location = order.LabelPosition;
            if (drawing.Line is { } link && order.Target is { } targetPoint)
            {
                drawing.Start!.Location = order.Position;
                drawing.End!.Location = targetPoint;
                link.Update();
            }
        }
        foreach (string key in drawings.Keys.Where(key => !active.Contains(key)).ToArray()) Remove(key);
    }

    private void Remove(string key)
    {
        Drawing drawing = drawings[key];
        layer?.Graphics.Remove(drawing.Label);
        if (drawing.Line != null) layer?.Graphics.Remove(drawing.Line);
        drawings.Remove(key);
    }

    internal void Clear()
    {
        foreach (string key in drawings.Keys.ToArray()) Remove(key);
        layer = null;
    }
}
