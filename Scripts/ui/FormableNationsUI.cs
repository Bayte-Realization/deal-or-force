using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private string _formationConfirmation = "";
    private bool _showAllFormables;
    private string _formationSearch = "";
    private bool _peacefulFormablesOnly;
    private string _formableOnMap = "";

    private void ShowFormableNations()
    {
        _sideBody.Text = Tr("A shared state needs territory, a stable government and a founding mandate. Partners may join by treaty; a proclamation never grants unowned land.");
        ClearRuntimeChildren(_decisionControls);
        var all = new CheckButton { Text = Tr("Show all formable nations"), ButtonPressed = _showAllFormables };
        all.Toggled += value => { _showAllFormables = value; _formationConfirmation = ""; ShowFormableNations(); };
        _decisionControls.AddChild(all);
        var search = new LineEdit
        {
            Name = "FormationSearch", Text = _formationSearch,
            PlaceholderText = Tr("Search foundations or country tags…"),
            CustomMinimumSize = new Vector2(0, Ui.Metric("formation.search_height", 34)), SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        Tip(search, "Filter by the state's name or a member's country tag.");
        _decisionControls.AddChild(search);
        var peaceful = new CheckButton
        {
            Name = "PeacefulFoundationsOnly", Text = Tr("Peaceful accession routes"), ButtonPressed = _peacefulFormablesOnly
        };
        _decisionControls.AddChild(peaceful);
        var cards = new VBoxContainer { Name = "FormationCards", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        cards.AddThemeConstantOverride("separation", (int)Ui.Metric("formation.card_gap", 12));
        _decisionControls.AddChild(cards);
        search.TextChanged += value => { _formationSearch = value; _formationConfirmation = ""; RenderFormationCards(cards); };
        peaceful.Toggled += value => { _peacefulFormablesOnly = value; _formationConfirmation = ""; RenderFormationCards(cards); };
        RenderFormationCards(cards);
    }

    private void RenderFormationCards(VBoxContainer cards)
    {
        var nation = _campaign.Player;
        ClearRuntimeChildren(cards);
        if (!string.IsNullOrEmpty(nation.FormedNationId))
        {
            cards.AddChild(Title(Simulation.Loc.Format("Proclaimed: {0} · campaign day {1}", nation.DisplayName,
                nation.FormedNationDay), 18, Ui.Colour("text.accent")));
            cards.AddChild(Title("Your state can later grow into a larger one.", 13, Ui.Colour("text.body_soft")));
            // How well it holds together. A proclaimed state is not a fact of nature - it is a thing
            // a government does every week - and this is the number to watch to keep it
            // (UnionCohesion). Shown only for a country that has proclaimed something, which is the
            // only country the number means anything for.
            var cohesion = nation.UnionCohesion;
            var floor = _campaign.Defines.UnionCohesionFloor;
            var mood = cohesion >= 0.6f ? "The union is holding"
                : cohesion >= floor ? "The union is cracking"
                : "The union is coming apart";
            var caption = Title(Simulation.Loc.Format("{0} · {1}", Simulation.Loc.Get(mood), Fmt.Percent(cohesion)),
                13, Ui.Colour(cohesion >= 0.6f ? "state.positive" : "state.warning"));
            caption.Name = "UnionCohesionCaption";
            caption.TooltipText = Ui.Tip(Simulation.Loc.Get(
                "War, instability and radicalism wear a union down; a quiet, stable year puts it back together. Below the floor a region leaves, and the rest of the claim stays yours to pick up again."));
            cards.AddChild(caption);
            var bar = new ProgressBar
            {
                Name = "UnionCohesionBar", MinValue = 0, MaxValue = 100, Value = cohesion * 100,
                ShowPercentage = false, CustomMinimumSize = new Vector2(0, 6)
            };
            bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = Ui.Colour("surface.overlay") });
            bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat
            {
                BgColor = Ui.Colour(cohesion >= 0.6f ? "state.positive" : "state.warning")
            });
            cards.AddChild(bar);
        }
        var term = _formationSearch.Trim();
        var definitions = FormableNations.AvailableIn(_campaign).Where(d => (_showAllFormables || FormableNations.Eligible(_campaign, nation, d)) &&
            (!_peacefulFormablesOnly || d.PeacefulAccession) &&
            (term.Length == 0 || Simulation.Loc.Get(d.Name).Contains(term, System.StringComparison.OrdinalIgnoreCase) ||
             d.Id.Contains(term, System.StringComparison.OrdinalIgnoreCase) || d.RequiredTags.Any(t => t.Contains(term, System.StringComparison.OrdinalIgnoreCase))))
            .Select(d => (Definition: d, Status: FormableNations.Evaluate(_campaign, nation, d)))
            .OrderByDescending(entry => entry.Status.CanForm)
            .ThenBy(entry => Simulation.Loc.Get(entry.Definition.Name)).ToArray();
        cards.AddChild(Title(Simulation.Loc.Format("Foundations shown: {0}", definitions.Length), 12, Ui.Colour("text.body_soft")));
        if (definitions.Length == 0)
            cards.AddChild(Title("No foundations match these filters. Clear the search or browse all countries.", 14, Ui.Colour("text.body_soft")));
        foreach (var entry in definitions)
        {
            var d = entry.Definition;
            var status = entry.Status;
            var card = WorkbenchCard(cards);
            var future = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            var futureSprite = FlagCatalog.ResolveFormationPreview(_campaign, nation, d.Id);
            future.AddChild(new TextureRect {
                Name = "FormationFlag_" + d.Id, Texture = FlagSpriteTexture(futureSprite),
                CustomMinimumSize = new Vector2(Ui.Metric("formation.flag_width", 72), Ui.Metric("formation.flag_height", 48)), SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
                SizeFlagsVertical = SizeFlags.ShrinkCenter, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered });
            var heading = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            heading.AddChild(Title(d.Name, (int)Ui.Metric("formation.title_size", 20), Ui.Colour("text.accent")));
            heading.AddChild(Title(status.CanForm ? "Ready to proclaim" : d.PeacefulAccession ? "Treaty or territorial route" : "Territorial route",
                12, Ui.Colour(status.CanForm ? "state.positive" : "text.body_soft")));
            future.AddChild(heading);
            var statusBadge = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ShrinkEnd, SizeFlagsVertical = SizeFlags.ShrinkCenter };
            var badgeStyle = new StyleBoxFlat
            {
                BgColor = Ui.Colour(status.CanForm ? "state.positive" : "surface.card", status.CanForm ? 0.22f : 0.6f),
                BorderColor = Ui.Colour(status.CanForm ? "state.positive" : "surface.line_faint"),
            };
            badgeStyle.SetBorderWidthAll(1);
            badgeStyle.SetCornerRadiusAll(4);
            badgeStyle.SetContentMarginAll(6);
            statusBadge.AddThemeStyleboxOverride("panel", badgeStyle);
            statusBadge.AddChild(Title(status.CanForm ? Tr("Ready").ToUpperInvariant() : $"{status.Owned}/{status.Total}", 11, Ui.Colour(status.CanForm ? "state.positive" : "text.accent")));
            future.AddChild(statusBadge);
            Tip(future, futureSprite.Length == 0 ? "No flag available" : "Flag after formation");
            card.AddChild(future);
            card.AddChild(Title(d.Description, 13, Ui.Colour("text.body_soft")));
            var members = new HFlowContainer();
            foreach (var tag in d.RequiredTags)
            {
                var flag = FlagBadge(tag, 20);
                Tip(flag, _campaign.NationByTag(tag)?.DisplayName ?? tag);
                members.AddChild(flag);
            }
            card.AddChild(members);
            var pbar = new ProgressBar
            {
                Name = "FormationTerritory_" + d.Id, MaxValue = System.Math.Max(1, status.Total), Value = status.Owned,
                CustomMinimumSize = new Vector2(0, Ui.Metric("formation.progress_height", 10)), ShowPercentage = false, MouseFilter = MouseFilterEnum.Ignore
            };
            var track = new StyleBoxFlat { BgColor = Ui.Colour("surface.inset") };
            track.SetCornerRadiusAll(3);
            var fill = new StyleBoxFlat { BgColor = Ui.Colour(status.CanForm ? "state.positive" : "text.accent") };
            fill.SetCornerRadiusAll(3);
            pbar.AddThemeStyleboxOverride("background", track);
            pbar.AddThemeStyleboxOverride("fill", fill);
            card.AddChild(pbar);
            card.AddChild(Title(Simulation.Loc.Format("Foundation territory secured: {0}/{1}", status.Owned, status.Total), 13, Ui.Colour("text.bright")));
            if (status.Required > 0 && status.Required < status.Total)
                card.AddChild(Title(Simulation.Loc.Format("Needed to proclaim: {0} ({1:P0} of the territory)",
                    status.Required, status.Required / (float)System.Math.Max(1, status.Total)), 12, Ui.Colour("text.muted")));
            // The land itself, drawn on the map in the founder's colour: the state as it would stand.
            var shown = _formableOnMap == d.Id;
            var onMap = ActionButton(shown ? "Hide from the map" : "Show on the map", () =>
            {
                if (_formableOnMap == d.Id) { _formableOnMap = ""; _globe.ClearPeacePreview(); }
                else
                {
                    _formableOnMap = d.Id;
                    _globe.SetPeacePreview(FormableNations.Land(_campaign, nation, d), nation.MapIndex);
                }
                ShowFormableNations();
            }, 0, 28);
            onMap.Name = "FormableOnMap_" + d.Id;
            Tip(onMap, "Paint the foundation territory on the map in your colour, as the new state would stand.");
            card.AddChild(onMap);
            if (d.PeacefulAccession && FormableNations.Eligible(nation, d) && FormableNations.CompatibleProclamation(nation, d))
                AddFormationPartners(card, nation, d);
            // Successful technical checks are not UI content. Failed checks remain explicit.
            for (var i = 0; i < status.Requirements.Length; i++)
            {
                var r = status.Requirements[i];
                if (r.Met && i != 2 && i != 3) continue;
                card.AddChild(Title((r.Met ? "✓ " : "• ") + r.Label, 12, Ui.Colour(r.Met ? "state.positive" : "state.warning")));
            }
            card.AddChild(Title(Simulation.Loc.Format("Founding cost: {0} ({1:P1} of GDP)", Fmt.Money(status.Cost), d.CostShare), 14, Ui.Colour("text.accent_dim")));
            card.AddChild(Title(Simulation.Loc.Format("One-off: governance +{0:P0} (cap {2:P0}); productivity +{1:0.000}. Institutions retained; flag follows the new identity.",
                d.GovernanceGain, d.ProductivityGain, StabilityCore.Weights.GovernanceLimit), 12, Ui.Colour("text.body_soft")));
            if (!FormableNations.Eligible(nation, d)) continue; // Encyclopedia cards have no fake action.
            if (!status.CanForm) _formationConfirmation = _formationConfirmation == d.Id ? "" : _formationConfirmation;
            var confirming = _formationConfirmation == d.Id;
            var button = ActionButton(confirming ? "Confirm proclamation" : "Proclaim state", () =>
            {
                if (_formationConfirmation != d.Id) { _formationConfirmation = d.Id; ShowFormableNations(); return; }
                _formationConfirmation = "";
                _campaign.Commands.Enqueue(new FormNationCommand { IssuerId = nation.Id, FormableId = d.Id });
                _campaign.Commands.ApplyAll(_campaign);
                Refresh();
                ShowFormableNations();
            }, 0, 36, status.CanForm);
            button.Name = "FormNation_" + d.Id;
            button.Disabled = !status.CanForm;
            Tip(button, status.CanForm ? "Proclamation spends the displayed cost and integrates consenting partners' declared foundation territory." : string.Join("\n", status.Requirements.Where(r => !r.Met).Select(r => r.Label)));
            card.AddChild(button);
            if (confirming) card.AddChild(ActionButton("Cancel", () => { _formationConfirmation = ""; ShowFormableNations(); }, 0, 30));
        }
    }

    private void AddFormationPartners(VBoxContainer card, Nation founder, FormableNation definition)
    {
        if (_campaign.ProvinceMap is null) return;
        var owners = PeacefulFormation.Territory(_campaign, definition)
            .Where(p => p > 0 && p <= _campaign.ProvinceMap.ProvinceCount && !_campaign.ProvinceMap.WaterByProvince[p])
            .Select(p => _campaign.ProvinceMap.ProvinceToNation[p]).Distinct().ToHashSet();
        var partners = _campaign.Nations.Where(n => n.Id != founder.Id && owners.Contains((ushort)n.MapIndex)).ToArray();
        if (partners.Length == 0) return;
        card.AddChild(Title("ACCESSION TALKS", 12, Ui.Colour("text.accent_dim")));
        foreach (var partner in partners)
        {
            var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            row.AddChild(FlagBadge(partner.Tag, 22));
            var name = Title(partner.DisplayName, 13, Ui.Colour("text.bright"));
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(name);
            var participating = PeacefulFormation.Participates(_campaign, founder, partner, definition);
            if (participating)
            {
                row.AddChild(Title(partner.AnswersTo(founder.Id) ? "Subject ready" : "Treaty signed", 12, Ui.Colour("state.positive")));
                card.AddChild(row);
                continue;
            }
            var refusal = PeacefulFormation.Refusal(_campaign, founder, partner, definition);
            var willingness = PeacefulFormation.Willingness(_campaign, founder, partner);
            var invite = ActionButton("Invite", () =>
            {
                _campaign.Commands.Enqueue(new InviteFormationCommand { IssuerId = founder.Id, PartnerId = partner.Id, FormableId = definition.Id });
                _campaign.Commands.ApplyAll(_campaign);
                Refresh();
                ShowFormableNations();
            }, Ui.Metric("formation.invite_width", 72), Ui.Metric("formation.invite_height", 30));
            invite.Name = "InviteFoundation_" + definition.Id + "_" + partner.Tag;
            invite.Disabled = refusal.Length > 0;
            Tip(invite, refusal.Length > 0 ? Simulation.Loc.Get(refusal) : Simulation.Loc.Format("Willingness: {0:0}/100 · acceptance needs {1:0}. Culture, relations, alliances and trade matter.", willingness, _campaign.Defines.UnionAiAcceptance));
            row.AddChild(invite);
            card.AddChild(row);
            card.AddChild(Title(refusal.Length > 0 ? refusal : Simulation.Loc.Format("Willingness: {0:0}/100", willingness), 12, Ui.Colour("text.body_soft")));
        }
    }
}
