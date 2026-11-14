using Noesis;
using SeapowerUI;
using NativeWindow = SeapowerUI.Window;

namespace SeaPower.SharedUi;

public static partial class NativeUi
{
    public static Grid Overlay(FrameworkElement content, Brush backdrop, Brush background, Brush borderBrush,
        float width, float height = float.NaN, float maxHeight = float.PositiveInfinity)
    {
        var border = new Border
        {
            Child = content, Width = width, Height = height, MaxHeight = maxHeight,
            Background = background, BorderBrush = borderBrush, BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var overlay = new Grid { Background = backdrop };
        overlay.Children.Add(border);
        return overlay;
    }
}

public class HostedWindow(Action? closed = null) : IDisposable
{
    private UIElement? host;
    private NativeWindow? window;

    public void Open(UIElement owner, Func<NativeWindow> create)
    {
        if (host != owner) Dispose();
        if (window == null)
        {
            window = create();
            host = owner;
            window.OnClosed += OnClosed;
        }
        WindowHostCommands.OpenWindowCommand.Execute(window, owner);
    }

    private void OnClosed(object sender, Noesis.EventArgs args)
    {
        if (!Equals(sender, window)) return;
        Release();
        closed?.Invoke();
    }

    private void Release()
    {
        if (window == null) return;
        window.OnClosed -= OnClosed;
        window = null;
        host = null;
    }

    public void Dispose()
    {
        if (window == null) return;
        var oldWindow = window;
        var oldHost = host;
        Release();
        try { WindowHostCommands.CloseWindowCommand.Execute(oldWindow, oldHost); }
        finally { closed?.Invoke(); }
    }
}
