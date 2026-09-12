using Noesis;
using SeaPower;
using SeaPower.SharedUi;
using SeapowerUI;
using UnityEngine;
using static SeaPower.SharedUi.NativeUi;

namespace UNREP;

public class SuppliesController : MonoBehaviour
{
    private readonly UnitStatusPage page = new(0x5552, "Supplies");
    private float nextRefresh;

    private void Update()
    {
        if (Time.realtimeSinceStartup < nextRefresh) return;
        nextRefresh = Time.realtimeSinceStartup + 0.3f;
        var unit = Singleton<RenderPosition>.InstanceExists() ? Singleton<RenderPosition>.Instance.getSelectedObject() : null;
        bool available = unit is Vessel && !unit.IsDestroyed && unit.IsPlayerUnit && unit.AcceptsOrdersFromPlayer;
        page.Refresh(MainGameView.Instance, Globals._mainGameViewModel?.UnitStatus, available);
        if (available && page.Content == null)
            page.Content = new StackPanel
            {
                Margin = new Thickness(10),
                Children = { Text("Supplies"), Text("Replenishment controls are not implemented yet.") },
            };
    }

    private void OnDestroy() => page.Dispose();
}
