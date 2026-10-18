using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private void ShowOpportunityAtlas()
    {
        _formationConfirmation = "";
        ClearRuntimeChildren(_decisionControls);
        var nation = _campaign.Player;
        _sideBody.Text = Simulation.Loc.Format("Opportunities for {0} — an overview, not an instruction to spend.", nation.DisplayName);
        var snapshot = OpportunityAtlas.Read(_campaign, nation, InterfaceSkin.Metric("atlas.shortage_threshold", .985f));
        var limit = (int)Math.Clamp(InterfaceSkin.Metric("atlas.cards_per_section", 3), 1, 12);
        var groups = CampaignUiCatalog.For("Decisions").Tabs.FirstOrDefault(t => t.Id == "Opportunities")?.Groups ?? Array.Empty<string>();
        _decisionControls.AddChild(Title("Opportunity atlas", 19, Ui.Colour("text.accent")));
        _decisionControls.AddChild(Title("These are live conditions, not forecasts. Opening a card spends nothing and issues no orders.", 12, Ui.Colour("text.muted")));
        foreach (var group in groups.Distinct())
        {
            if (group == "formables")
            {
                AtlasHeading("New state identities", snapshot.Formations.Length, limit);
                if (snapshot.Formations.Length == 0)
                    _decisionControls.AddChild(Title("No unproclaimed state is defined for this founding country.", 13, Ui.Colour("text.muted")));
                foreach (var entry in snapshot.Formations.Take(limit))
                {
                    var definition = entry.Definition; var status = entry.Status;
                    var card = AtlasCard(definition.Name, status.CanForm ? "Ready to proclaim" : "Requirements remain", status.CanForm);
                    var flag = FlagSpriteTexture(FlagCatalog.ResolveFormationPreview(_campaign, nation, definition.Id));
                    if (flag is not null) card.AddChild(new TextureRect { Texture = flag, CustomMinimumSize = new Vector2(60, 36),
                        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                        SizeFlagsHorizontal = SizeFlags.ShrinkBegin, TooltipText = Tr("Flag after formation") });
                    card.AddChild(Title(Simulation.Loc.Format("Territory owned and controlled: {0}/{1}", status.Owned, status.Total), 13, Ui.Colour("text.body")));
                    card.AddChild(Title(Simulation.Loc.Format("Founding cost: {0} ({1:P1} of GDP)", Fmt.Money(status.Cost), definition.CostShare), 12, Ui.Colour("text.accent_dim")));
                    foreach (var requirement in status.Requirements.Where(r => !r.Met))
                        card.AddChild(Title("• " + requirement.Label, 12, Ui.Colour("state.warning")));
                    if (AtlasHasTab("Formables")) card.AddChild(ActionButton("Review requirements", () => AtlasOpenTab("Formables", "FormNation_" + definition.Id), 0, 32));
                }
            }
            else if (group == "shortages")
            {
                AtlasHeading("Supply gaps", snapshot.Shortages.Length, limit);
                if (snapshot.Shortages.Length == 0)
                    _decisionControls.AddChild(Title("No shortage meets the atlas threshold in the latest market figures.", 13, Ui.Colour("text.muted")));
                foreach (var entry in snapshot.Shortages.Take(limit))
                {
                    var card = AtlasCard(CommodityCatalog.Get(entry.Good).Name, "Needs investigation", false);
                    card.AddChild(Title(Simulation.Loc.Format("Supply {0:P0}", entry.Satisfaction), 15, Ui.Colour("state.warning")));
                    card.AddChild(Title(Simulation.Loc.Format("Output {0:0.#} · demand {1:0.#} · imports {2:0.#} · exports {3:0.#}",
                        entry.Production, entry.Demand, entry.Imports, entry.Exports), 12, Ui.Colour("text.body_soft")));
                    card.AddChild(Title("Inspect production and trade before investing. More buildings alone may not resolve this gap.", 12, Ui.Colour("text.muted")));
                    if (CampaignUiCatalog.For("Economy").Tabs.Any(t => t.Id == "Market"))
                        card.AddChild(ActionButton("Inspect good", () =>
                        {
                            OpenEconomyTab("Market");
                            _shortagesOnly.ButtonPressed = false;
                            _marketSearch.Text = Tr(CommodityCatalog.Get(entry.Good).Name);
                            RefreshMarketTable();
                        }, 0, 32));
                }
            }
            else if (group == "decisions")
            {
                AtlasHeading("National initiatives", snapshot.Decisions.Length, limit);
                if (snapshot.Decisions.Length == 0) _decisionControls.AddChild(Title("No national decisions are defined.", 13, Ui.Colour("text.muted")));
                foreach (var entry in snapshot.Decisions.Take(limit))
                {
                    var definition = entry.Definition;
                    var card = AtlasCard(definition.Name, entry.Block.Length == 0 ? "Available now" : "Not available yet", entry.Block.Length == 0);
                    card.AddChild(Title(definition.Description, 12, Ui.Colour("text.body_soft")));
                    card.AddChild(Title(Simulation.Loc.Format("Cost {0} · cooldown {1} days", Fmt.Money(entry.Cost), definition.CooldownDays), 12, Ui.Colour("text.accent_dim")));
                    if (entry.Block.Length > 0)
                    {
                        var days = nation.DecisionAvailableDay.GetValueOrDefault(definition.Id) - _campaign.ElapsedDays;
                        card.AddChild(Title(days > 0 ? Simulation.Loc.Format("Available in {0} days", days) : Tr(entry.Block), 12, Ui.Colour("state.warning")));
                    }
                    if (AtlasHasTab("Decisions")) card.AddChild(ActionButton("Review decision", () => AtlasOpenTab("Decisions", "Decision_" + definition.Id), 0, 32));
                }
            }
        }
    }

    private static bool AtlasHasTab(string id) => CampaignUiCatalog.For("Decisions").Tabs.Any(t => t.Id == id);
    private void AtlasHeading(string title, int count, int limit)
    {
        _decisionControls.AddChild(Title(title, 16, Ui.Colour("text.accent_dim")));
        if (count > limit) _decisionControls.AddChild(Title(Simulation.Loc.Format("Showing {0} of {1}; the full list is in the corresponding tab.", limit, count), 12, Ui.Colour("text.muted")));
    }
    private VBoxContainer AtlasCard(string title, string state, bool ready)
    {
        var card = WorkbenchCard(_decisionControls);
        card.AddChild(Title(title, 16, Ui.Colour("text.bright")));
        card.AddChild(Title(state, 12, Ui.Colour(ready ? "state.positive" : "state.warning")));
        return card;
    }
    private void AtlasOpenTab(string tab, string targetName)
    {
        SelectDossierTab(tab);
        if (tab == "Decisions" && targetName.StartsWith("Decision_", StringComparison.Ordinal))
            SelectAgendaEntry(AgendaWorkspace.Section.Decisions, targetName[9..]);
        var target = _decisionControls.FindChildren("*", "Button", true, false).OfType<Control>().FirstOrDefault(c => c.Name == targetName);
        if (target?.GetParent() is Control card)
            _ = AtlasScrollToCard(card, tab);
    }

    private async System.Threading.Tasks.Task AtlasScrollToCard(Control card, string tab)
    {
        // New cards need a completed container layout before measuring their position.
        var tree = GetTree();
        await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        if (!GodotObject.IsInstanceValid(card) || !card.IsInsideTree() || !card.IsVisibleInTree() || _dossierTab != tab) return;
        // Align the heading, not just the action button, so the player sees what they opened.
        _dossierScroll.ScrollVertical += (int)Math.Round(card.GlobalPosition.Y - _dossierScroll.GlobalPosition.Y);
    }
}
