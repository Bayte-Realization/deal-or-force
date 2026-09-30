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
    private bool _chronicleWorld;
    private int _chroniclePage;
    private int _chronicleCountry = -1;

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
        _ledgerSummary.Text = Tr("Recorded facts, not inferred causes. Internal decisions and construction are visible only to their country.");
        if (_chronicleCountry != nation.Id) { _chronicleCountry = nation.Id; _chroniclePage = 0; }
        var filters = new HBoxContainer();
        _ledgerReportRows.AddChild(filters);
        foreach (var world in new[] { false, true })
        {
            var selected = world;
            filters.AddChild(ActionButton(world ? "World events" : "Country events", () =>
            { _chronicleWorld = selected; _chroniclePage = 0; _ledgerReportScroll.ScrollVertical = 0; RefreshLedger(); }, 150, 32, world == _chronicleWorld));
        }
        var entries = _campaign.Chronicle.Read(_campaign.Player.Id, nation.Id, _chronicleWorld);
        var size = (int)Math.Clamp(InterfaceSkin.Metric("chronicle.page_size", 30), 5, 100);
        _chroniclePage = Math.Clamp(_chroniclePage, 0, Math.Max(0, (entries.Length - 1) / size));
        _ledgerReportRows.AddChild(Title(Simulation.Loc.Format("Recorded: {0} · retained limit: {1}", entries.Length, WorldChronicle.Capacity), 12, Ui.Colour("text.caption")));
        if (entries.Length == 0) _ledgerReportRows.AddChild(Title("No recorded events yet. Earlier history is not reconstructed from the starting scenario.", 15, Ui.Colour("text.body_soft")));
        foreach (var entry in entries.Skip(_chroniclePage * size).Take(size))
        {
            var card = WorkbenchCard(_ledgerReportRows);
            card.AddChild(Title($"{entry.Date.Day:D2}.{entry.Date.Month:D2}.{entry.Date.Year} · {entry.Date.Hour:D2}:00", 12, Ui.Colour("text.accent_dim")));
            var key = "chronicle." + entry.Kind;
            card.AddChild(Title(Simulation.Loc.Format(key, Tr(entry.ActorName), Tr(entry.TargetName), Tr(entry.Subject), Tr(entry.Place)), 15, Ui.Colour("text.body")));
            var actor = _campaign.NationById(entry.ActorId);
            if (actor is not null) card.AddChild(ActionButton("Locate country", () =>
            { CloseLedger(); _globe.SetMapModeSubject(actor); _globe.FocusOn(actor.CapitalLatitude, actor.CapitalLongitude, 6.8f); }, 160, 30));
        }
        var paging = new HBoxContainer();
        _ledgerReportRows.AddChild(paging);
        if (_chroniclePage > 0) paging.AddChild(ActionButton("Newer events", () =>
        { _chroniclePage--; _ledgerReportScroll.ScrollVertical = 0; RefreshLedger(); }, 160, 32));
        if ((_chroniclePage + 1) * size < entries.Length) paging.AddChild(ActionButton("Older events", () =>
        { _chroniclePage++; _ledgerReportScroll.ScrollVertical = 0; RefreshLedger(); }, 160, 32));
    }

    private void ShowEconomicExposure(Nation nation)
    {
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
