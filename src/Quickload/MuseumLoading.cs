using System.Collections;
using HarmonyLib;
using Noesis;
using SeaPower;
using SeapowerUI;
using SeapowerUI.ViewModels;
using Debug = UnityEngine.Debug;
using Time = UnityEngine.Time;

namespace Quickload;

internal static class MuseumLoading
{
    private static LoadScreenView? screen;
    private static UIElement? originalContent;
    private static Grid? overlay;
    private static ObjectInformationViewModel? preview;
    private static ObjectInformationView? reference;
    private static System.Windows.Input.ICommand? testCommand;

    internal static IEnumerator Dispatch(LoadAction action)
    {
        switch (action.description)
        {
            case "PreloadInis":
                if (Singleton<BackgroundSceneController>.Instance.loadActions.Any(stage => stage.description == "OpenEncyclopedia"))
                    OpenPreview();
                break;
            case "OpenEncyclopedia":
                yield return OpenMuseum(action.GetEnumerator());
                yield break;
            case "DEM Data":
                // This native wait also turns terrain updates on before it yields.
                Singleton<TerrainManager>.Instance.StartCoroutine(action.GetEnumerator());
                yield break;
            case "Terrain Chunks":
                var terrain = Singleton<TerrainManager>.Instance;
                Debug.Log($"[Quickload] Opening Encyclopedia while terrain streams (DEM ready: {terrain.IsDEMLoadingDone}, chunks ready: {terrain.IsInitialLoadingDone}).");
                terrain.StartCoroutine(FinishTerrain(action.GetEnumerator()));
                yield break;
        }
        yield return action.GetEnumerator();
    }

    private static void OpenPreview()
    {
        ClosePreview();
        try
        {
            screen = LoadScreenView.Instance;
            if (screen?.Content is not UIElement content) return;
            preview = new(screen);
            testCommand = preview.TestCommand;
            SetMode(preview, false, null, null);
            reference = new ObjectInformationView { DataContext = preview };
            var panel = new Border
            {
                Child = reference, Background = new SolidColorBrush(Color.FromArgb(245, 35, 38, 40)),
                Margin = new Thickness(24, 24, 24, 96),
            };
            overlay = new();
            originalContent = content;
            screen.Content = null;
            overlay.Children.Add(content);
            overlay.Children.Add(panel);
            screen.Content = overlay;
            Debug.Log("[Quickload] Encyclopedia reference browser shown on the loading screen.");
        }
        catch (Exception error)
        {
            ClosePreview();
            Debug.LogWarning($"[Quickload] Could not open the Encyclopedia preview: {error.Message}");
        }
    }

    private static void SetMode(ObjectInformationViewModel model, bool testing, System.Windows.Input.ICommand? test,
        System.Windows.Input.ICommand? select)
    {
        AccessTools.PropertySetter(typeof(ObjectInformationViewModel), nameof(ObjectInformationViewModel.IsTestMode)).Invoke(model, [testing]);
        AccessTools.PropertySetter(typeof(ObjectInformationViewModel), nameof(ObjectInformationViewModel.TestCommand)).Invoke(model, [test]);
        AccessTools.PropertySetter(typeof(ObjectInformationViewModel), nameof(ObjectInformationViewModel.SelectItem)).Invoke(model, [select]);
    }

    private static IEnumerator OpenMuseum(IEnumerator native)
    {
        if (preview == null || reference == null)
        {
            yield return native;
            yield break;
        }

        while (MainGameView.Instance == null || !Singleton<RenderPosition>.InstanceExists()
            || Singleton<RenderPosition>.Instance.SelectedObject == null) yield return null;

        var game = MainGameView.Instance;
        var model = preview;
        var view = reference;
        var start = view.PointToScreen(new Point());
        var end = view.PointToScreen(new Point(view.ActualWidth, view.ActualHeight));
        // Noesis owns each view's visual tree. Transfer the model, leaving each control in its own view.
        view.DataContext = null;
        var savedTest = testCommand;
        ClosePreview(dispose: false);
        SetMode(model, true, savedTest, new SeaPower.DelegateCommand(entry =>
        {
            if (entry is not MuseumEntry item) return;
            ObjectInformationViewModel details = item switch
            {
                MuseumWeaponEntry => new ObjectWeaponInformationViewModel(game, item),
                MuseumSensorEntry => new ObjectSensorInformationViewModel(game, item),
                _ => new(game, item),
            };
            WindowHostCommands.OpenWindowCommand.Execute(details, game);
        }));
        WindowHostCommands.OpenWindowCommand.Execute(model, game);
        game.UpdateLayout();
        var window = FindWindow(game, model);
        if (window == null)
        {
            yield return null;
            window = FindWindow(game, model);
        }
        if (window == null)
        {
            if (Singleton<WindowHostManager>.InstanceExists()) Singleton<WindowHostManager>.Instance.CurrentHost?.Windows.Remove(model);
            model.Dispose();
            yield return native;
            yield break;
        }
        Singleton<SceneCreator>.Instance.StartCoroutine(Snap(window, start, end));
        Debug.Log("[Quickload] Encyclopedia reference moved into the scene.");
        (native as IDisposable)?.Dispose();
    }

    private static SeapowerUI.Window? FindWindow(DependencyObject node, ObjectInformationViewModel model)
    {
        if (node is SeapowerUI.Window window && ReferenceEquals(window.DataContext, model)) return window;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindWindow(VisualTreeHelper.GetChild(node, i), model) is { } child) return child;
        return null;
    }

    private static IEnumerator Snap(SeapowerUI.Window window, Point start, Point end)
    {
        var topLeft = window.PointFromScreen(start);
        var bottomRight = window.PointFromScreen(end);
        float width = bottomRight.X - topLeft.X, height = bottomRight.Y - topLeft.Y;
        if (width <= 0 || height <= 0 || window.ActualWidth <= 0 || window.ActualHeight <= 0) yield break;

        float x = window.X, y = window.Y, targetWidth = window.Width, targetHeight = window.Height;
        if (!float.IsFinite(targetWidth) || !float.IsFinite(targetHeight)) yield break;
        float startX = x + topLeft.X, startY = y + topLeft.Y;
        width = Math.Clamp(width, window.MinWidth, window.MaxWidth);
        height = Math.Clamp(height, window.MinHeight, window.MaxHeight);
        try
        {
            var loading = Singleton<LoadScreen>.Instance;
            const float duration = 0.5f;
            float elapsed = 0;
            while (window.View != null && elapsed < duration)
            {
                float t = 1 - elapsed / duration;
                float eased = t * t * (3 - 2 * t);
                Place(window, x + (startX - x) * eased, y + (startY - y) * eased,
                    targetWidth + (width - targetWidth) * eased, targetHeight + (height - targetHeight) * eased);
                yield return null;
                if (!loading.cameraObject.enabled) elapsed += Time.unscaledDeltaTime;
            }
        }
        finally
        {
            if (window.View != null) Place(window, x, y, targetWidth, targetHeight);
        }
    }

    private static void Place(SeapowerUI.Window window, float x, float y, float width, float height)
    {
        window.SetCurrentValue(SeapowerUI.Window.XProperty, x);
        window.SetCurrentValue(SeapowerUI.Window.YProperty, y);
        window.SetCurrentValue(FrameworkElement.WidthProperty, width);
        window.SetCurrentValue(FrameworkElement.HeightProperty, height);
    }

    private static void ClosePreview(bool dispose = true)
    {
        if (screen != null && overlay != null && originalContent != null)
        {
            overlay.Children.Remove(originalContent);
            screen.Content = originalContent;
        }
        if (dispose)
        {
            preview?.Dispose();
            (testCommand as IDisposable)?.Dispose();
        }
        testCommand = null;
        reference = null;
        preview = null;
        overlay = null;
        originalContent = null;
        screen = null;
    }

    private static IEnumerator FinishTerrain(IEnumerator wait)
    {
        yield return wait;
        Debug.Log("[Quickload] Encyclopedia terrain finished streaming.");
    }
}
