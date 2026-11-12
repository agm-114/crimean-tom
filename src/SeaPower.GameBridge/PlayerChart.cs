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
            Add(new TextDrawing($"OODA · {mark.Label}") { Location = mark.Position, ColorPreset = color });
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
