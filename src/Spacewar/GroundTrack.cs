using Noesis;
using static SeaPower.SharedUi.NativeUi;

namespace Spacewar;

internal class GroundTrack
{
    private const float Width = 720, Height = 300, Top = 25;
    private static readonly Brush GridBrush = new SolidColorBrush(Color.FromRgb(65, 72, 83));
    private static readonly Brush TrackBrush = new SolidColorBrush(Color.FromRgb(213, 168, 118));
    private readonly Canvas moving = new();
    internal Canvas Canvas { get; } = new() { Width = Width, Height = Height + 55, ClipToBounds = true };

    internal GroundTrack()
    {
        for (int longitude = -180; longitude <= 180; longitude += 60)
        {
            float x = X(longitude);
            Line(Canvas, x, Top, x, Top + Height, GridBrush);
            Label($"{longitude}°", Math.Clamp(x - 12, 0, Width - 35), 3);
        }
        for (int latitude = -90; latitude <= 90; latitude += 30)
        {
            float y = Y(latitude);
            Line(Canvas, 0, y, Width, y, GridBrush);
            Label($"{latitude}°", 3, Math.Clamp(y + 2, Top, Top + Height - 18));
        }
        Label("Ground track · next orbit   |   Gold: selected spacecraft", 3, Top + Height + 8);
        Canvas.Children.Add(moving);
    }

    internal void Draw(Orbit orbit, double seconds)
    {
        moving.Children.Clear();
        var previous = orbit.At(seconds);
        for (int step = 1; step <= 180; step++)
        {
            var next = orbit.At(seconds + orbit.Period * step / 180);
            // Split the track at the date line instead of drawing a line across the chart.
            if (Math.Abs(previous.Longitude - next.Longitude) < 180)
                Line(moving, X(previous.Longitude), Y(previous.Latitude), X(next.Longitude), Y(next.Latitude), TrackBrush);
            previous = next;
        }
        var current = orbit.At(seconds);
        Dot(X(current.Longitude), Y(current.Latitude), TrackBrush);
    }

    private static float X(double longitude) => (float)((longitude + 180) / 360 * Width);
    private static float Y(double latitude) => Top + (float)((90 - latitude) / 180 * Height);
    private static void Line(Canvas canvas, float x1, float y1, float x2, float y2, Brush brush) =>
        canvas.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = 1 });
    private void Dot(float x, float y, Brush brush)
    {
        var dot = new Ellipse { Width = 8, Height = 8, Fill = brush };
        Noesis.Canvas.SetLeft(dot, x - 4);
        Noesis.Canvas.SetTop(dot, y - 4);
        moving.Children.Add(dot);
    }
    private void Label(string label, float x, float y)
    {
        var text = Text(label);
        Noesis.Canvas.SetLeft(text, x);
        Noesis.Canvas.SetTop(text, y);
        Canvas.Children.Add(text);
    }
}
