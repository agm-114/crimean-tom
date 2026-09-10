using SeaPower;
using SeapowerUI;
using SeapowerUI.ViewModels;

namespace SeaPower.GameBridge;

internal static class EncyclopediaControls
{
    internal static bool Ready => !MenuControls.Loading && Globals._inMuseum && TimeControls.Read().Available
        && MainGameView.Instance != null && Singleton<RenderPosition>.InstanceExists()
        && Singleton<RenderPosition>.Instance.SelectedObject != null;

    internal static object Open(GameCommand command)
    {
        MenuControls.RequireConfirm(command);
        if (Ready) return new { ok = true, state = "ready" };
        return MenuControls.Execute(new() { Action = "menu", Text = "encyclopedia", Confirm = true });
    }

    internal static object Select(GameCommand command)
    {
        MenuControls.RequireConfirm(command);
        if (!Ready) throw new InvalidOperationException("The encyclopedia is not ready. Unit selection is restricted to museum mode.");
        ObjectInformationViewModel model = new(MainGameView.Instance, command.Text, command.Index);
        if (model.SelectedMuseumEntry == null || !model.TestCommand.CanExecute(null))
        {
            model.Dispose();
            throw new ArgumentException($"No displayable encyclopedia unit '{command.Text}' with variant {command.Index}. Use the exact unit INI name and a zero-based variant.");
        }
        // Use the museum's own display action, including variant, loadout and terrain handling.
        model.TestCommand.Execute(null);
        if (Singleton<WindowHostManager>.InstanceExists() && Singleton<WindowHostManager>.Instance.CurrentHost is { } host)
            foreach (var window in host.Windows.OfType<ObjectInformationViewModel>().ToArray())
                WindowHostCommands.CloseWindowCommand.Execute(window, MainGameView.Instance);
        WindowHostCommands.OpenWindowCommand.Execute(model, MainGameView.Instance);
        return new { ok = true, state = "unit-selected", unit = model.SelectedMuseumEntry.IniRef, variant = model.SelectedMuseumEntry.VariantIndex };
    }
}
