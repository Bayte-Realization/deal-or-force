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
        var relationText = relation is null
            ? "Your country"
            : $"Relations: {relation.Opinion:+0;-0;0} · {relation.Stance}";
        var war = nation == _campaign.Player ? null : _campaign.Wars.Find(candidate =>
            !candidate.Ended && candidate.Involves(_campaign.Player.Id) && candidate.Involves(nation.Id));
        _sideBody.Text =
            $"{nation.Name} ({nation.Tag})\n{location}\n\n" +
            $"Capital: {nation.CapitalName}\nPopulation: {nation.Population:N0}\n" +
            $"GDP: ${nation.Gdp:0.00}B\nTreasury: ${nation.Treasury:0.00}B\n\n" +
            relationText +
            (war is null ? "" : $"\nAt war · score {(war.AttackerId == _campaign.Player.Id ? war.WarScore : -war.WarScore):+0.0;-0.0;0.0}");
        _countryActions.Visible = nation != _campaign.Player;
        if (!_countryActions.Visible) return;
        var canDeclare = CampaignDiplomacy.CanDeclareWar(_campaign, _campaign.Player.Id, nation.Id, out var reason);
        _declareWarButton.Disabled = !canDeclare;
        _declareWarButton.TooltipText = canDeclare ? "Requires a second click to confirm." : reason;
        _declareWarButton.Text = _pendingWarTarget == nation.Id ? $"CONFIRM WAR ON {nation.Tag}" : "Declare war";
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
            _countryActionFeedback.Text = "This begins an immediate land war. Click the highlighted button again to confirm.";
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
        _countryActionFeedback.Text = $"War declared on {_selectedNation.Name}. Select your armies and assign Advance front.";
        ShowCountryInteraction(_selectedNation, "WAR DECLARED");
    }

    private void FocusSelectedCapital()
    {
        if (_selectedNation is null || string.IsNullOrWhiteSpace(_selectedNation.CapitalName)) return;
        _globe.FocusOn(_selectedNation.CapitalLatitude, _selectedNation.CapitalLongitude, 7.1f);
        _countryActionFeedback.Text = $"Focused on {_selectedNation.CapitalName}.";
    }

    private bool ValidForeignSelection() => _campaign?.Player is not null &&
        _selectedNation is not null && _selectedNation != _campaign.Player;
}
