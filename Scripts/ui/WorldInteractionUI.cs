using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private VBoxContainer _countryActions = null!;
    private Label _countryActionFeedback = null!;
    private Button _declareWarButton = null!;
    private HBoxContainer _subjectActions = null!;
    private Button _integrateButton = null!;
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
        _subjectActions = new HBoxContainer { Visible = false };
        _subjectActions.AddThemeConstantOverride("separation", 5);
        _countryActions.AddChild(_subjectActions);
        _subjectActions.AddChild(ActionButton("Release", ReleaseSelectedSubject, 96, 32));
        _integrateButton = ActionButton("Integrate", IntegrateSelectedSubject, 110, 32);
        _subjectActions.AddChild(_integrateButton);

        _declareWarButton = ActionButton("Declare war", ConfirmOrDeclareWar, 0, 38);
        _declareWarButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _countryActions.AddChild(_declareWarButton);
        _countryActionFeedback = Title("", 12, Ui.Colour("text.body_soft"));
        _countryActionFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _countryActions.AddChild(_countryActionFeedback);
    }

    /// <summary>
    /// Who this country answers to, and who answers to it.
    ///
    /// Who answers to whom is public - it is the one thing about a dependency that everybody
    /// can see. How badly it wants out is a judgement about somebody else's politics, so it
    /// is shown for a country's own subjects and for anybody the player can actually see
    /// into, and withheld otherwise rather than guessed at.
    /// </summary>
    private void ShowDependencies(Nation nation)
    {
        var overlord = SubjectSystem.OverlordOf(_campaign, nation);
        var subjects = SubjectSystem.SubjectsOf(_campaign, nation);
        if (overlord is null && subjects.Count == 0) return;

        DetailHeading("ARRANGEMENTS");
        if (overlord is not null)
        {
            var kind = SubjectCatalog.Of(nation.SubjectType);
            DetailRow("Answers to", $"{overlord.DisplayName} · {kind?.Name ?? nation.SubjectType}",
                kind?.Description ?? "");
            if (kind is not null)
            {
                var held = _campaign.ElapsedDays - nation.SubjectSinceDay;
                DetailRow("Arranged", Simulation.Loc.Format("{0} year(s) ago", $"{held / 365.0:0.0}"));
                if (kind.Tribute > 0f)
                    DetailRow("Tribute", $"{kind.Tribute:P0} " +
                                         Simulation.Loc.Format("of its taxes, {0} last week",
                                             Fmt.Money(nation.TributePaidWeekly)));
            }
            if (nation.OverlordId == _campaign.Player.Id || CanInspectNation(nation.Id))
                DetailRow("Wants out", $"{nation.LibertyDesire:P0}",
                    Tr("What it wants this week is worked out from how large it has grown, whether " +
                       "its overlord can govern itself, and whether that overlord is at war. At a " +
                       "hundred per cent it simply leaves."),
                    nation.LibertyDesire > 0.6f ? "state.negative" : "text.bright");
        }

        foreach (var subject in subjects.OrderByDescending(other => other.Population).Take(8))
        {
            var kind = SubjectCatalog.Of(subject.SubjectType);
            var wants = nation.Id == _campaign.Player.Id || CanInspectNation(subject.Id)
                ? $" · {Simulation.Loc.Format("wants out {0}", $"{subject.LibertyDesire:P0}")}"
                : "";
            DetailRow(subject.DisplayName, $"{kind?.Name ?? subject.SubjectType}{wants}",
                kind?.Description ?? "",
                subject.LibertyDesire > 0.6f ? "state.negative" : "text.bright");
        }
        if (subjects.Count > 8)
            DetailRow("And", Simulation.Loc.Format("{0} more", $"{subjects.Count - 8:N0}"));
    }

    /// <summary>Let one go, or take it in - the two ends an arrangement has.</summary>
    private void ReleaseSelectedSubject()
    {
        if (_campaign?.Player is null || _selectedNation is null) return;
        if (_selectedNation.OverlordId != _campaign.Player.Id) return;
        var name = _selectedNation.DisplayName;
        SubjectSystem.Release(_campaign, _selectedNation);
        _countryActionFeedback.Text = Simulation.Loc.Format("{0} answers to nobody now.", name);
        Refresh();
    }

    private void IntegrateSelectedSubject()
    {
        if (_campaign?.Player is null || _selectedNation is null) return;
        var name = _selectedNation.DisplayName;
        if (!SubjectSystem.Annex(_campaign, _campaign.Player, _selectedNation, out var refusal))
        {
            _countryActionFeedback.Text = refusal;
            return;
        }
        // The country it was is off the map now, so nothing may go on pointing at it.
        _selectedNation = _campaign.Player;
        _countryActionFeedback.Text = Simulation.Loc.Format("{0} is part of your country now.", name);
        Refresh();
    }

    private void ShowCountryInteraction(Nation nation, string location)
    {
        if (nation is null || _campaign?.Player is null) return;
        PrepareDossierTabs("Diplomacy");
        ApplyDossierTab();
        ClearDetail();
        _selectedNation = nation;
        _sideTitle.Text = nation.DisplayName;
        var relation = nation == _campaign.Player ? null : CampaignDiplomacy.Get(_campaign, _campaign.Player.Id, nation.Id);
        // The number, and then what it is made of. An opinion nobody can account for is a
        // number the player has no way to act on: "they dislike you" is not a fact you can
        // do anything with, and "they have not forgotten the war, and the trade agreement
        // is not making up for it" is.
        var war = nation == _campaign.Player ? null : _campaign.Wars.Find(candidate =>
            !candidate.Ended && candidate.Involves(_campaign.Player.Id) && candidate.Involves(nation.Id));
        _sideBody.Text = Tr(location);
        DetailHeading("COUNTRY");
        DetailRow("Capital", nation.CapitalName);
        DetailRow("Form of government", Tr(PoliticsCore.GovernmentOf(nation).Name));
        if (CanInspectNation(nation.Id))
        {
            DetailRow("Population", Fmt.People(nation.Population));
            DetailRow("GDP", Fmt.Money(nation.Gdp));
            DetailRow("Treasury", Fmt.Money(nation.Treasury));
        }
        else DetailRow("Intelligence", Tr("Unknown (fog of war)"));
        DetailHeading("RELATIONS");
        if (relation is null) DetailRow("Status", Tr("Your country"));
        else
        {
            DetailRow("Opinion", $"{relation.Opinion:+0;-0;0}", Tr("How this government regards you."),
                relation.Opinion < 0 ? "state.negative" : "state.positive", () => relation.OpinionDrivers);
            DetailRow("Stance", Tr(relation.Stance.ToString()));
            foreach (var entry in relation.OpinionDrivers.Entries.OrderByDescending(item => System.Math.Abs(item.Value)).Take(5))
                DetailRow(entry.Source, $"{entry.Value:+0;-0;0}");
        }
        if (war is not null) DetailRow("War score", $"{(war.AttackerId == _campaign.Player.Id ? war.WarScore : -war.WarScore):+0.0;-0.0;0.0}");
        ShowDependencies(nation);
        _countryActions.Visible = nation != _campaign.Player;
        _subjectActions.Visible = nation.OverlordId == _campaign.Player.Id;
        if (_subjectActions.Visible)
        {
            var may = SubjectSystem.MayAnnex(_campaign, _campaign.Player, nation, out var why);
            _integrateButton.Disabled = !may;
            _integrateButton.TooltipText = Ui.Tip(may
                ? Tr("Take it into your country. Its regions become yours.")
                : why);
        }
        if (!_countryActions.Visible) return;
        var canDeclare = CampaignDiplomacy.CanDeclareWar(_campaign, _campaign.Player.Id, nation.Id, out var reason);
        _declareWarButton.Disabled = !canDeclare;
        _declareWarButton.TooltipText = Ui.Tip(canDeclare ? "Requires a second click to confirm." : reason);
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
