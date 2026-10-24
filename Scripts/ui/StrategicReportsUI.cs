using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _ledgerGrid;
    private ScrollContainer _ledgerReportScroll;
    private VBoxContainer _ledgerReportRows;

    private void BuildStrategicReports(VBoxContainer rows)
    {
        _ledgerReportScroll = new ScrollContainer { Name = "StrategicReports", Visible = false,
            SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        rows.AddChild(_ledgerReportScroll);
        _ledgerReportRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _ledgerReportRows.AddThemeConstantOverride("separation", 9);
        _ledgerReportScroll.AddChild(_ledgerReportRows);
    }

    private void RefreshStrategicReport(Nation nation)
    {
        ClearRuntimeChildren(_ledgerReportRows);
        if (_ledgerTab == "Vulnerabilities") { ShowEconomicExposure(nation); return; }
        ShowCampaignChronicle(nation);
    }

    /// <summary>
    /// The ledger's chronicle tab: the latest lines for this country and the wars it is in.
    /// Fixed rows inside the scroll that was already here. Reading does not record.
    /// </summary>
    private void ShowCampaignChronicle(Nation nation)
    {
        _ledgerSummary.Text = Tr(CampaignChronicle.Title);
        _ledgerSummary.AddThemeColorOverride("font_color", Ui.Colour("text.accent"));
        var lines = CampaignChronicle.Read(_campaign, nation, _campaign.Player);
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var empty = line.Sequence == 0 && line.Kind.Length == 0;
            var label = Fixed(Title(empty ? CampaignChronicle.Empty : line.Text, 14,
                Ui.Colour(empty ? "text.body_soft" : "text.body")));
            label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            label.CustomMinimumSize = new Vector2(0, 22);
            if (!empty)
            {
                label.TooltipText = Ui.Tip(line.Text);
                label.MouseFilter = MouseFilterEnum.Stop;
            }
            _ledgerReportRows.AddChild(label);
        }
    }

    private void ShowEconomicExposure(Nation nation)
    {
        _ledgerSummary.AddThemeColorOverride("font_color", Ui.Colour("text.body_soft"));
        _ledgerSummary.Text = Tr("Physical import exposure in the latest market figures. This is not a forecast of prices, GDP or replacement suppliers.");
        var ready = _campaign.TradeRoutesDay >= 0;
        _ledgerReportRows.AddChild(Title(ready
            ? Simulation.Loc.Format("Named routes: campaign day {0}; current day {1}.", _campaign.TradeRoutesDay, _campaign.ElapsedDays)
            : Tr("Supplier attribution unavailable until the next market calculation. Saved import totals are still shown."), 13, Ui.Colour("text.caption")));
        var entries = EconomicExposure.Read(_campaign, nation);
        BuildSupplyStressTest(nation, entries);
        if (entries.Length == 0) _ledgerReportRows.AddChild(Title("No imported goods with positive demand in the current figures.", 15, Ui.Colour("text.body_soft")));
        foreach (var entry in entries)
        {
            var card = WorkbenchCard(_ledgerReportRows);
            card.AddChild(Title(CommodityCatalog.Get(entry.Good).Name, 17, Ui.Colour("text.accent")));
            card.AddChild(Title(Simulation.Loc.Format("Imports / demand: {0:P0} · current supply: {1:P0}", entry.ImportShare, entry.Satisfaction), 14, Ui.Colour("text.body")));
            if (entry.Suppliers.Length > 0)
            {
                var top = _campaign.NationById(entry.Suppliers[0].NationId);
                card.AddChild(Title(Simulation.Loc.Format("Largest supplier: {0} · {1:P0} of imports · {2:P0} of demand", top.DisplayName, entry.LargestImportShare, entry.LargestDemandShare), 14, Ui.Colour("state.warning")));
                var count = (int)Math.Clamp(InterfaceSkin.Metric("exposure.suppliers_shown", 4), 1, 20);
                foreach (var supplier in entry.Suppliers.Take(count))
                    card.AddChild(Title(Simulation.Loc.Format("{0}: {1:0.##} units", _campaign.NationById(supplier.NationId).DisplayName, supplier.Volume), 12, Ui.Colour("text.body_soft")));
                if (entry.Suppliers.Length > count) card.AddChild(Title(Simulation.Loc.Format("Additional suppliers: {0}", entry.Suppliers.Length - count), 12, Ui.Colour("text.caption")));
            }
            if (entry.UnattributedImports > .001f) card.AddChild(Title(Simulation.Loc.Format("Imports without supplier attribution: {0:0.##} units", entry.UnattributedImports), 13, Ui.Colour("text.caption")));
            if (nation.Id == _campaign.Player.Id && CampaignUiCatalog.For("Economy").Tabs.Any(t => t.Id == "Market"))
                card.AddChild(ActionButton("Inspect good", () =>
            {
                CloseLedger(); OpenEconomyTab("Market"); _shortagesOnly.ButtonPressed = false;
                _marketSearch.Text = Tr(CommodityCatalog.Get(entry.Good).Name); RefreshMarketTable();
            }, 160, 30));
        }
    }
}
