using System.Reflection;
using System.Windows.Input;
using Noesis;
using SeaPower;
using SeapowerUI;
using SeapowerUI.ViewModels;

namespace SeaPower.GameBridge;

internal static class MenuControls
{
    private static readonly Func<bool, bool> CampaignExists = (Func<bool, bool>)typeof(Singleton<>)
        .MakeGenericType(typeof(MissionManager).Assembly.GetType("SeaPower.LinearCampaign", throwOnError: true))
        .GetMethod("InstanceExists")!.CreateDelegate(typeof(Func<bool, bool>));
    internal static bool InCampaign => CampaignExists(false);
    private static readonly Dictionary<string, string> Buttons = new()
    {
        ["scenarios"] = "Scenarios", ["campaigns"] = "Campaign", ["instant-action"] = "Instant",
        ["editor"] = "Editor", ["encyclopedia"] = "Encyclopedia",
        ["options"] = "Options", ["mods"] = "ModManager",
        ["manual"] = "Manual", ["exit"] = "Exit",
    };
    internal static bool Loading => !Singleton<LoadScreen>.InstanceExists()
        || Singleton<LoadScreen>.Instance.cameraObject == null || Singleton<LoadScreen>.Instance.cameraObject.enabled;
    internal static bool Idle => !Loading && !Singleton<GameMain>.InstanceExists() && !InCampaign
        && Current is { IsVisible: true, IsEnabled: true }
        && !(Singleton<WindowHostManager>.InstanceExists() && Singleton<WindowHostManager>.Instance.CurrentHost?.Windows.Count > 0);
    private static FrameworkElement? Current => Singleton<MenuSystemViewModel>.InstanceExists()
        ? Singleton<MenuSystemViewModel>.Instance.CurrentWindow as FrameworkElement : null;

    internal static void RequireConfirm(GameCommand command)
    {
        if (!command.Confirm) throw new InvalidOperationException($"{command.Action} requires Confirm=true.");
    }

    internal static object Execute(GameCommand command)
    {
        string page = command.Text;
        if (page.Length == 0) return new
        {
            currentWindow = Current?.GetType().Name, loading = Loading, idle = Idle,
            pages = new[] { "main", "continue" }.Concat(Buttons.Keys).ToArray(),
        };
        if (page is "encyclopedia" or "continue" or "manual" or "exit") RequireConfirm(command);
        if (!Idle) throw new InvalidOperationException("Menu navigation requires an idle menu with no open dialogs. Cancel a standalone mission first.");
        if (page == "main")
        {
            if (Current is MainMenuView) return new { ok = true, page };
            if (Current is not (ScenarioMenuView or CampaignMenuView or OptionsMenuView or ModMenuView or InstantActionView))
                throw new InvalidOperationException("Use the current page's native Back/Cancel control. It may contain unsaved work.");
            if (Current is OptionsMenuView or ModMenuView or InstantActionView) Click(Current, "Back");
            else
            {
                ICommand? back = Current.DataContext?.GetType().GetProperty("BackCommand")?.GetValue(Current.DataContext) as ICommand;
                if (back?.CanExecute(null) != true) throw new InvalidOperationException("The current page's Back command is unavailable.");
                back.Execute(null);
            }
            return new { ok = true, currentWindow = Current?.GetType().Name };
        }
        if (Current is not MainMenuView main) throw new InvalidOperationException("Return to the main menu with 'menu main' first.");
        var model = (MainMenuViewModel)main.DataContext;
        if (page == "continue")
        {
            if (!model.CanContinue || !model.ContinueCommand.CanExecute(null)) throw new InvalidOperationException("Continue is unavailable.");
            model.ContinueCommand.Execute(null);
        }
        else
        {
            if (!Buttons.TryGetValue(page, out string button)) throw new ArgumentException($"Unknown menu page '{page}'.");
            if (page == "instant-action" && !model.InstantActionEnabled) throw new InvalidOperationException("Instant Action is disabled by the game.");
            Click(main, button);
        }
        return new { ok = true, page, state = "requested" };
    }

    private static void Click(FrameworkElement view, string button)
    {
        // Only callers above choose handlers; requests cannot supply a method name.
        MethodInfo handler = view.GetType().GetMethod($"Button{button}_Click", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException($"The native {button} menu handler changed. Update the bridge.");
        try { handler.Invoke(view, [view, new RoutedEventArgs(ButtonBase.ClickEvent, view)]); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            throw new InvalidOperationException(error.InnerException.Message, error.InnerException);
        }
    }
}
