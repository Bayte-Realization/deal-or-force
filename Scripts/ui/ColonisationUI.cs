using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The side panel for land that belongs to nobody: what it is, what settling it costs, who
/// is already on the way, and the one order worth giving about it (ColonisationCore).
/// </summary>
public partial class DealOrForceGame
{
    /// <summary>The unclaimed region the side panel is about, or null when it is about something else.</summary>
    private ScenarioState _selectedUnclaimed;

    private void OnUnclaimedPicked(ScenarioState state, int provinceId)
    {
        if (state is null || _campaign is null) return;
        if (_networkLayer is not null && _networkLayer.Visible) return;
        if (_pickerLayer.Visible || !_hudLayer.Visible) return;

        _selectedUnclaimed = state;
        // Nothing that reads a region expects one without an owner, so the region the rest of
        // the interface answers for is left empty rather than pointed at the ice.
        _selectedState = null;
        _selectedProvinceId = provinceId;
        _selectedArmy = null;
        _selectedArmies.Clear();
        _globe.SelectArmy(-1);
        _openScreen = "";
        _sidePanel.Visible = true;
        SetArmyPanelVisible(false);
        _countryActions.Visible = false;
        _regionControls.Visible = false;
        _economyControls.Visible = false;
        _decisionControls.Visible = false;
        _battleDetailControls.Visible = false;
        _militarySpending.Visible = false;
        ShowSideFlag(null);
        PrepareDossierTabs("Unclaimed");
        ApplyDossierTab();
        ShowUnclaimedLand("");
    }

    private void ShowUnclaimedLand(string feedback)
    {
        var state = _selectedUnclaimed;
        if (state is null || _campaign is null) return;
        var player = _campaign.Player;

        ClearDetail();
        _sideTitle.Text = state.DisplayName;
        if (!ColonisationCore.IsUnclaimed(state))
        {
            // Settled while the panel was open: it is somebody's region now, and that
            // country's own panel is the one that answers for it.
            var owner = _campaign.Nations.Find(nation => nation.MapIndex == state.NationMapIndex);
            _sideBody.Text = Loc.Format("A colony of {0}.", owner?.DisplayName ?? Tr("an unknown state"));
            return;
        }
        _sideBody.Text = Tr("Land that belongs to nobody. A country that can reach it - across a border of its own, " +
                            "or by sea from a coast of its own - may settle it.");

        var colonist = ColonisationCore.Colonist(_campaign, state);
        var days = ColonisationCore.Days(_campaign, state);
        DetailHeading("SETTLEMENT");
        DetailRow("Provinces", ColonisationCore.Provinces(_campaign, state).ToString("N0"));
        DetailRow("Cost of founding", Fmt.Money(ColonisationCore.Cost(_campaign, state)),
            Tr("Paid from the treasury once, when the settlers set out. Calling them home returns none of it."));
        DetailRow("Time to found", Loc.Format("{0:N0} days", days),
            Tr("From the day the settlers set out to the day the region is the country's own."));
        if (colonist is not null)
        {
            DetailRow("Being settled by", colonist.DisplayName);
            DetailRow("Progress", Fmt.Percent(state.ColonisationProgress),
                Loc.Format("About {0:N0} days to go.", Mathf.CeilToInt((1f - state.ColonisationProgress) * days)));
        }
        if (player is null) return;

        var ours = colonist == player;
        var button = ours
            ? ActionButton("Call the settlers home", AbandonSelectedColony, 0, 32)
            : ActionButton("Found a colony", ColoniseSelectedLand, 0, 32, true);
        button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var refusal = ours ? "" : ColonisationCore.Refusal(_campaign, player, state);
        button.Disabled = refusal.Length > 0;
        Tip(button, ours
            ? Tr("The settlers come home and the land stays nobody's. What was spent is spent.")
            : refusal.Length > 0 ? refusal
            : Loc.Format("Send settlers for {0}. In {1:N0} days the region is ours, with the people who went.",
                Fmt.Money(ColonisationCore.Cost(_campaign, state)), days));
        _sideDetail.AddChild(button);

        var note = feedback.Length > 0 ? feedback : refusal;
        if (note.Length > 0)
        {
            var line = Title(note, 12, Ui.Colour("text.accent_dim"));
            line.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _sideDetail.AddChild(line);
        }
    }

    private void ColoniseSelectedLand()
    {
        if (_selectedUnclaimed is null || _campaign?.Player is null) return;
        var command = new ColoniseCommand { IssuerId = _campaign.Player.Id, StateMapIndex = _selectedUnclaimed.MapIndex };
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        ShowUnclaimedLand(command.Started
            ? Loc.Format("Our settlers set out for {0}.", _selectedUnclaimed.DisplayName)
            : ColonisationCore.Refusal(_campaign, _campaign.Player, _selectedUnclaimed));
    }

    private void AbandonSelectedColony()
    {
        if (_selectedUnclaimed is null || _campaign?.Player is null) return;
        _campaign.Commands.Enqueue(new AbandonColonyCommand
            { IssuerId = _campaign.Player.Id, StateMapIndex = _selectedUnclaimed.MapIndex });
        _campaign.Commands.ApplyAll(_campaign);
        ShowUnclaimedLand(Tr("The settlers are coming home."));
    }
}
