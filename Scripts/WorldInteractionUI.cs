using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private VBoxContainer _countryActions = null!;
    private Label _countryActionFeedback = null!;
    private Button _declareWarButton = null!;
    private int _pendingWarTarget = -1;

    private void BuildCountryActionControls(VBoxContainer parent)
    {
        _countryActions = new VBoxContainer { Visible = false };
        _countryActions.AddThemeConstantOverride("separation", 6);
        parent.AddChild(_countryActions);
        _countryActions.AddChild(Title("DIPLOMATIC ACTIONS", 12, Ui.Colour("text.caption")));
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 5);
        _countryActions.AddChild(row);
        row.AddChild(ActionButton("Improve", ImproveSelectedRelations, 96, 32, true));
        row.AddChild(ActionButton("Denounce", DenounceSelected, 96, 32));
        row.AddChild(ActionButton("Capital", FocusSelectedCapital, 96, 32));
        _declareWarButton = ActionButton("Declare war", ConfirmOrDeclareWar, 0, 38);
        _declareWarButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _countryActions.AddChild(_declareWarButton);
        _countryActionFeedback = Title("", 12, Ui.Colour("text.body_soft"));
        _countryActionFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _countryActions.AddChild(_countryActionFeedback);
    }

    private void ShowCountryInteraction(Nation nation, string location)
    {
        if (nation is null || _campaign?.Player is null) return;
        _selectedNation = nation;
        _sideTitle.Text = nation.Name;
        var relation = nation == _campaign.Player ? null : CampaignDiplomacy.Get(_campaign, _campaign.Player.Id, nation.Id);
        // The number, and then what it is made of. An opinion nobody can account for is a
        // number the player has no way to act on: "they dislike you" is not a fact you can
        // do anything with, and "they have not forgotten the war, and the trade agreement
        // is not making up for it" is.
        var relationText = relation is null
            ? "Your country"
            : $"Relations: {relation.Opinion:+0;-0;0} · {relation.Stance}";
        if (relation is not null && relation.OpinionDrivers.HasEntries)
            foreach (var entry in relation.OpinionDrivers.Entries
                         .OrderByDescending(item => System.Math.Abs(item.Value))
                         .Take(5))
                relationText += $"\n    {entry.Source}  {entry.Value:+0;-0;0}";
        var war = nation == _campaign.Player ? null : _campaign.Wars.Find(candidate =>
            !candidate.Ended && candidate.Involves(_campaign.Player.Id) && candidate.Involves(nation.Id));
        _sideBody.Text =
            Simulation.Loc.Format("{0} ({1})\n{2}\n\nCapital: {3}\n",
                nation.Name, nation.Tag, location, nation.CapitalName) +
            (CanInspectNation(nation.Id)
                ? Simulation.Loc.Format("Population: {0:N0}\nGDP: ${1:0.00}B\nTreasury: ${2:0.00}B\n\n",
                    nation.Population, nation.Gdp, nation.Treasury)
                : Tr("Internal statistics: unknown (fog of war).\n\n")) +
            relationText +
            (war is null
                ? ""
                : Simulation.Loc.Format("\nAt war · score {0:+0.0;-0.0;0.0}",
                    war.AttackerId == _campaign.Player.Id ? war.WarScore : -war.WarScore));
        _countryActions.Visible = nation != _campaign.Player;
        if (!_countryActions.Visible) return;
        var canDeclare = CampaignDiplomacy.CanDeclareWar(_campaign, _campaign.Player.Id, nation.Id, out var reason);
        _declareWarButton.Disabled = !canDeclare;
        _declareWarButton.TooltipText = canDeclare ? "Requires a second click to confirm." : reason;
        _declareWarButton.Text = _pendingWarTarget == nation.Id
            ? Simulation.Loc.Format("CONFIRM WAR ON {0}", nation.Tag)
            : Tr("Declare war");
        if (!canDeclare && string.IsNullOrWhiteSpace(_countryActionFeedback.Text))
            _countryActionFeedback.Text = reason;
    }

    private void ImproveSelectedRelations()
    {
        if (!ValidForeignSelection()) return;
        var relation = CampaignDiplomacy.Get(_campaign, _campaign.Player.Id, _selectedNation.Id);
        var before = relation.Opinion;
        _campaign.Commands.Enqueue(new ImproveRelationsCommand { IssuerId = _campaign.Player.Id, TargetId = _selectedNation.Id });
        _campaign.Commands.ApplyAll(_campaign);
        _pendingWarTarget = -1;
        _countryActionFeedback.Text = relation.Opinion > before
            ? $"Diplomatic mission succeeded: relations {relation.Opinion:+0;-0;0}."
            : "Diplomatic action is on cooldown or the treasury is insufficient.";
        ShowCountryInteraction(_selectedNation, "Diplomatic mission");
    }

    private void DenounceSelected()
    {
        if (!ValidForeignSelection()) return;
        var relation = CampaignDiplomacy.Get(_campaign, _campaign.Player.Id, _selectedNation.Id);
        var before = relation.Opinion;
        _campaign.Commands.Enqueue(new DenounceCommand { IssuerId = _campaign.Player.Id, TargetId = _selectedNation.Id });
        _campaign.Commands.ApplyAll(_campaign);
        _pendingWarTarget = -1;
        _countryActionFeedback.Text = relation.Opinion < before
            ? $"Public denunciation issued: relations {relation.Opinion:+0;-0;0}."
            : "Diplomatic action is still on cooldown.";
        ShowCountryInteraction(_selectedNation, "Diplomatic confrontation");
    }

    private void ConfirmOrDeclareWar()
    {
        if (!ValidForeignSelection()) return;
        if (!CampaignDiplomacy.CanDeclareWar(_campaign, _campaign.Player.Id, _selectedNation.Id, out var reason))
        {
            _countryActionFeedback.Text = reason;
            ShowCountryInteraction(_selectedNation, "Diplomatic crisis");
            return;
        }
        if (_pendingWarTarget != _selectedNation.Id)
        {
            _pendingWarTarget = _selectedNation.Id;
            _countryActionFeedback.Text = Tr("This begins an immediate land war. Click the highlighted button again to confirm.");
            ShowCountryInteraction(_selectedNation, "War declaration prepared");
            return;
        }
        _campaign.Commands.Enqueue(new DeclareWarCampaignCommand
        {
            IssuerId = _campaign.Player.Id,
            TargetId = _selectedNation.Id,
            ClaimedStateMapIndex = _selectedState?.NationMapIndex == _selectedNation.MapIndex
                ? _selectedState.MapIndex
                : -1
        });
        _campaign.Commands.ApplyAll(_campaign);
        PlayStrategicCue("war_declared");
        _pendingWarTarget = -1;
        _countryActionFeedback.Text = Simulation.Loc.Format("War declared on {0}. Select your armies and assign Advance front.", _selectedNation.Name);
        ShowCountryInteraction(_selectedNation, "WAR DECLARED");
    }

    private void FocusSelectedCapital()
    {
        if (_selectedNation is null || string.IsNullOrWhiteSpace(_selectedNation.CapitalName)) return;
        _globe.FocusOn(_selectedNation.CapitalLatitude, _selectedNation.CapitalLongitude, 7.1f);
        _countryActionFeedback.Text = Simulation.Loc.Format("Focused on {0}.", _selectedNation.CapitalName);
    }

    private bool ValidForeignSelection() => _campaign?.Player is not null &&
        _selectedNation is not null && _selectedNation != _campaign.Player;
}
