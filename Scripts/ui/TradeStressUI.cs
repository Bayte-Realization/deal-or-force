using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private int _stressSupplierId = -1;
    private int _stressNationId = -1;

    private void BuildSupplyStressTest(Nation nation, EconomicExposure.Row[] exposure)
    {
        if (_stressNationId != nation.Id) { _stressNationId = nation.Id; _stressSupplierId = -1; }
        var suppliers = exposure.SelectMany(r => r.Suppliers).Select(s => s.NationId).Distinct()
            .Select(id => _campaign.NationById(id)).Where(n => n is not null)
            .OrderBy(n => n.DisplayName, StringComparer.Ordinal).ThenBy(n => n.Id).ToArray();
        if (!suppliers.Any(s => s.Id == _stressSupplierId)) _stressSupplierId = -1;
        if (suppliers.Length == 0) return;

        var card = WorkbenchCard(_ledgerReportRows);
        card.AddChild(Title("Supplier stress test", 18, Ui.Colour("text.accent")));
        card.AddChild(Title("What if one supplier stopped selling to this country? Today's figures, with no replacement found.", 13, Ui.Colour("text.caption")));
        var picker = new OptionButton { Name = "StressSupplier", FitToLongestItem = false,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 36) };
        picker.AddItem(Tr("Choose a supplier — baseline only"), -1);
        foreach (var supplier in suppliers) picker.AddItem(supplier.DisplayName, supplier.Id);
        picker.Select(_stressSupplierId < 0 ? 0 : Array.FindIndex(suppliers, s => s.Id == _stressSupplierId) + 1);
        picker.ItemSelected += index =>
        {
            _stressSupplierId = index == 0 ? -1 : suppliers[(int)index - 1].Id;
            _ledgerReportScroll.ScrollVertical = 0;
            RefreshLedger();
        };
        card.AddChild(picker);
        if (_stressSupplierId < 0) return;
        var report = SupplyStressTest.Read(_campaign, nation, _stressSupplierId);
        if (!report.Available)
        {
            card.AddChild(Title("Stress test unavailable until the next market calculation.", 14, Ui.Colour("state.warning")));
            return;
        }
        if (report.ExcludedGoods > 0)
            card.AddChild(Title(Simulation.Loc.Format("Goods left out for lack of data: {0}", report.ExcludedGoods), 13, Ui.Colour("state.warning")));
        if (report.Rows.Length == 0)
            card.AddChild(Title("No measurable imports from this supplier in the current snapshot.", 14, Ui.Colour("text.caption")));
        foreach (var row in report.Rows)
        {
            var result = WorkbenchCard(_ledgerReportRows);
            result.Name = "Stress_" + row.Good;
            result.AddChild(Title(CommodityCatalog.Get(row.Good).Name, 17, Ui.Colour("text.accent")));
            AddSupplyStressBar(result, "Current supply: {0:P0}", row.CurrentSupply, "state.info");
            AddSupplyStressBar(result, "Without supplier: {0:P0}", row.RemainingSupply,
                row.AddedShortfall > .001 ? "state.warning" : "state.info");
            result.AddChild(Title(Simulation.Loc.Format("Imports removed: {0:0.##} units · additional unmet demand: {1:0.##} units", row.RemovedImports, row.AddedShortfall), 13, Ui.Colour("text.body_soft")));
            if (row.AddedShortfall <= .001)
                result.AddChild(Title("Current production and remaining imports cover the lost supply without increasing the shortfall.", 13, Ui.Colour("text.caption")));
        }
        _ledgerReportRows.AddChild(Title("Baseline import details", 18, Ui.Colour("text.accent")));
    }

    private static void AddSupplyStressBar(VBoxContainer rows, string caption, double supply, string colour)
    {
        rows.AddChild(Title(Simulation.Loc.Format(caption, supply), 13, Ui.Colour("text.body")));
        var bar = new ProgressBar { MinValue = 0, MaxValue = 100, Value = supply * 100,
            ShowPercentage = false, MouseFilter = MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(0, Math.Clamp(InterfaceSkin.Metric("exposure.stress_bar_height", 8), 4, 24)) };
        bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = Ui.Colour(colour) });
        bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = Ui.Colour("surface.background") });
        rows.AddChild(bar);
    }
}
