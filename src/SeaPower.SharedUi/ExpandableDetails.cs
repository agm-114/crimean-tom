using Noesis;

namespace SeaPower.SharedUi;

public class ExpandableDetails
{
    private static readonly ControlTemplate CaretTemplate = (ControlTemplate)GUI.ParseXaml("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ToggleButton">
            <Border x:Name="Face" Background="Transparent">
                <Path x:Name="Caret" Data="M 0,0 L 4,4 L 8,0" Stroke="#C8C9CB" StrokeThickness="1.5"
                      HorizontalAlignment="Center" VerticalAlignment="Center" RenderTransformOrigin="0.5,0.5"/>
            </Border>
            <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True">
                    <Setter TargetName="Face" Property="Background" Value="#494C4F"/>
                </Trigger>
                <Trigger Property="IsChecked" Value="True">
                    <Setter TargetName="Caret" Property="RenderTransform">
                        <Setter.Value><RotateTransform Angle="180"/></Setter.Value>
                    </Setter>
                </Trigger>
            </ControlTemplate.Triggers>
        </ControlTemplate>
        """);

    public ToggleButton Toggle { get; } = new() { Template = CaretTemplate, Width = 18, Height = 20 };
    public ScrollViewer Content { get; }
    public bool IsExpanded => Toggle.IsChecked == true;
    private readonly FrameworkElement summary;
    private readonly string description;
    private bool synchronizing;

    public ExpandableDetails(FrameworkElement summary, FrameworkElement content, string description,
        float maxHeight, Action<bool> changed)
    {
        this.summary = summary;
        this.description = description;
        Content = NativeUi.Scroll(content);
        Content.MaxHeight = maxHeight;
        Toggle.Visibility = Visibility.Collapsed;
        Show(false);
        Toggle.Checked += (_, _) => OnToggle();
        Toggle.Unchecked += (_, _) => OnToggle();

        void OnToggle()
        {
            if (synchronizing) return;
            bool expanded = IsExpanded;
            Show(expanded);
            changed(expanded);
        }
    }

    public void SetState(bool available, bool expanded)
    {
        Toggle.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        expanded &= available;
        synchronizing = true;
        try { Toggle.IsChecked = expanded; }
        finally { synchronizing = false; }
        Show(expanded);
    }

    private void Show(bool expanded)
    {
        Toggle.ToolTip = (expanded ? "Hide " : "Show ") + description;
        summary.Visibility = expanded ? Visibility.Hidden : Visibility.Visible;
        Content.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (expanded) Content.ScrollToTop();
    }
}
