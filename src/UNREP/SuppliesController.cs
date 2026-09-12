using SeaPower;
using SeaPower.SharedUi;
using SeapowerUI;
using UnityEngine;

namespace UNREP;

public class SuppliesController : MonoBehaviour
{
    private readonly UnitStatusPage page = new(0x5552, "Supplies");
    private Vessel? selected;
    private SuppliesView? view;
    private float nextRefresh;

    private void Update()
    {
        if (Time.realtimeSinceStartup < nextRefresh) return;
        nextRefresh = Time.realtimeSinceStartup + 0.3f;
        SupplyTransfers.Update();
        var unit = Singleton<RenderPosition>.InstanceExists() ? Singleton<RenderPosition>.Instance.getSelectedObject() as Vessel : null;
        bool available = SupplyTransfers.Available(unit);
        page.Refresh(MainGameView.Instance, Globals._mainGameViewModel?.UnitStatus, available);
        if (!available) { selected = null; view = null; page.Content = null; return; }
        if (unit != selected || page.Content == null)
        {
            selected = unit;
            view = new SuppliesView(unit!);
            page.Content = view.Content;
        }
        view?.Refresh();
    }

    private void OnDestroy()
    {
        page.Dispose();
        foreach (var job in SupplyTransfers.Jobs) job.Release();
        SupplyTransfers.Jobs.Clear();
        SupplySpeedLimits.Update();
    }
}
