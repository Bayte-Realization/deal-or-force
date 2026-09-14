using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private string _formationConfirmation = "";
    private bool _showAllFormables;

    private void ShowFormableNations()
    {
        var nation = _campaign.Player;
        _sideBody.Text = Tr("A new state, built on territory you already own. No land is granted by proclamation.");
        ClearRuntimeChildren(_decisionControls);
        var all = new CheckButton { Text = Tr("Show all formable nations"), ButtonPressed = _showAllFormables };
        all.Toggled += value => { _showAllFormables = value; _formationConfirmation = ""; ShowFormableNations(); };
        _decisionControls.AddChild(all);
        if (!string.IsNullOrEmpty(nation.FormedNationId))
        {
            _decisionControls.AddChild(Title(Simulation.Loc.Format("Proclaimed: {0} · campaign day {1}", nation.DisplayName,
                nation.FormedNationDay), 18, Ui.Colour("text.accent")));
            _decisionControls.AddChild(Title("Each country may proclaim one new state in a campaign.", 13, Ui.Colour("text.body_soft")));
        }
        var definitions = FormableNations.All.Where(d => _showAllFormables || FormableNations.Eligible(nation, d)).ToArray();
        if (definitions.Length == 0)
            _decisionControls.AddChild(Title("No formation is defined for this country. You can browse other states above.", 14, Ui.Colour("text.body_soft")));
        foreach (var d in definitions)
        {
            var status = FormableNations.Evaluate(_campaign, nation, d);
            var card = WorkbenchCard(_decisionControls);
            card.AddChild(Title(d.Name, 21, Ui.Colour("text.accent")));
            var future = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            var futureSprite = FlagCatalog.ResolveFormationPreview(_campaign, nation, d.Id);
            future.AddChild(new TextureRect {
                Name = "FormationFlag_" + d.Id, Texture = FlagSpriteTexture(futureSprite),
                CustomMinimumSize = new Vector2(72, 48), SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
                SizeFlagsVertical = SizeFlags.ShrinkCenter, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered });
            var flagCaption = Title(futureSprite.Length == 0 ? "No flag available" : "Flag after formation", 13, Ui.Colour("text.body_soft"));
            flagCaption.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            flagCaption.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            future.AddChild(flagCaption);
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
            card.AddChild(new ProgressBar
            {
                Name = "FormationTerritory_" + d.Id, MaxValue = System.Math.Max(1, status.Total), Value = status.Owned,
                CustomMinimumSize = new Vector2(0, 10), ShowPercentage = false, MouseFilter = MouseFilterEnum.Ignore
            });
            card.AddChild(Title(Simulation.Loc.Format("Territory owned and controlled: {0}/{1}", status.Owned, status.Total), 13, Ui.Colour("text.bright")));
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
            Tip(button, status.CanForm ? "Proclamation spends the displayed cost. Existing borders do not change." : string.Join("\n", status.Requirements.Where(r => !r.Met).Select(r => r.Label)));
            card.AddChild(button);
            if (confirming) card.AddChild(ActionButton("Cancel", () => { _formationConfirmation = ""; ShowFormableNations(); }, 0, 30));
        }
    }
}
