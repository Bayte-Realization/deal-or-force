using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private VBoxContainer _countryActions = null!;
    private Label _countryActionFeedback = null!;
    private Button _declareWarButton = null!;
    private Button _negotiatePeaceButton = null!;
    private Button _quickUltimatumButton = null!;
    private VBoxContainer _subjectActions = null!;
    private Button _integrateButton = null!;
    private Button _allianceButton = null!;
    private Button _countryImproveButton = null!;
    private Button _guaranteeButton = null!;
    private int _pendingWarTarget = -1;

    private void BuildCountryActionControls(VBoxContainer parent)
    {
        _countryActions = new VBoxContainer { Name = "CountryActions", Visible = false, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _countryActions.AddThemeConstantOverride("separation", 8);
        parent.AddChild(_countryActions);
        _countryActions.AddChild(Title("DIPLOMATIC ACTIONS", 12, Ui.Colour("text.caption")));
        var list = new VBoxContainer { Name = "DiplomaticActionList", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 6);
        _countryActions.AddChild(list);
        var row = new VBoxContainer { Name = "RelationsActions" };
        row.AddThemeConstantOverride("separation", 5);
        list.AddChild(Title("Relations and treaties", 12, Ui.Colour("text.caption")));
        list.AddChild(row);
        _countryImproveButton = ActionButton("Improve", ImproveSelectedRelations, 96, 32, true);
        row.AddChild(_countryImproveButton);
        row.AddChild(ActionButton("Denounce", DenounceSelected, 96, 32));
        row.AddChild(ActionButton("Capital", FocusSelectedCapital, 96, 32));
        var pacts = new VBoxContainer();
        pacts.AddThemeConstantOverride("separation", 5);
        list.AddChild(pacts);
        _allianceButton = ActionButton("Alliance", ToggleSelectedAlliance, 140, 32);
        _guaranteeButton = ActionButton("Guarantee", ToggleSelectedGuarantee, 140, 32);
        pacts.AddChild(_allianceButton);
        pacts.AddChild(_guaranteeButton);
        BuildAgreementControls(list);
        BuildSubmissionControls(list);
        AddCountryDiplomaticActButtons(list);

        _subjectActions = new VBoxContainer { Name = "SubjectActions", Visible = false };
        _subjectActions.AddThemeConstantOverride("separation", 5);
        list.AddChild(_subjectActions);
        _subjectActions.AddChild(Title("Subject administration", 12, Ui.Colour("text.caption")));
        _subjectActions.AddChild(ActionButton("Release", ReleaseSelectedSubject, 96, 32));
        _integrateButton = ActionButton("Integrate", IntegrateSelectedSubject, 110, 32);
        _subjectActions.AddChild(_integrateButton);

        _declareWarButton = ActionButton("Declare war", ConfirmOrDeclareWar, 0, 38);
        _declareWarButton.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        list.AddChild(Title("War and peace", 12, Ui.Colour("text.caption")));
        _negotiatePeaceButton = ActionButton("Negotiate peace", () => OpenDiplomaticPeace(_selectedNation), 0, 38);
        _negotiatePeaceButton.Name = "NegotiatePeace";
        list.AddChild(_negotiatePeaceButton);
        _quickUltimatumButton = ActionButton("Issue ultimatum",
            () => IssueSelectedUltimatum(_selectedNation, _countryActionFeedback), 0, 38);
        _quickUltimatumButton.Name = "IssueUltimatumQuick";
        list.AddChild(_quickUltimatumButton);
        list.AddChild(_declareWarButton);
        _countryActionFeedback = Title("", 12, Ui.Colour("text.body_soft"));
        _countryActionFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _countryActions.AddChild(_countryActionFeedback);
    }

    /// <summary>
    /// What this country has signed, and with whom.
    ///
    /// Who is allied to whom is public - it is the one thing about an alliance everybody is
    /// meant to know, which is most of what an alliance is for. What is NOT public is an
    /// offer nobody has answered, so that only ever appears on the table it was put on.
    /// </summary>
    private void ShowPacts(Nation nation)
    {
        var mine = nation.Id == _campaign.Player.Id
            ? null
            : CampaignDiplomacy.Peek(_campaign, _campaign.Player.Id, nation.Id);
        var allies = AllianceSystem.AlliesOf(_campaign, nation);
        var offered = nation.Id == _campaign.Player.Id
            ? AllianceSystem.OfferedTo(_campaign, nation)
            : null;
        var guarantees = new List<string>();
        var guarantors = new List<string>();
        foreach (var other in _campaign.Nations)
        {
            if (other.Id == nation.Id) continue;
            var relation = CampaignDiplomacy.Peek(_campaign, nation.Id, other.Id);
            if (relation is null) continue;
            if (relation.GuaranteedBy(nation.Id)) guarantees.Add(other.Tag);
            if (relation.GuaranteedBy(other.Id)) guarantors.Add(other.Tag);
        }

        if (allies.Count == 0 && offered is null && guarantees.Count == 0 && guarantors.Count == 0 &&
            mine is not { NonAggressionPact: true }) return;

        DetailHeading("PACTS");
        if (offered is not null)
            DetailRow("Offered", Simulation.Loc.Format("{0} has offered an alliance", offered.DisplayName),
                Tr("Answer it from their own panel, where the alliance button accepts it."),
                "state.positive");
        if (mine is { Stance: DiplomaticStance.Allied })
            DetailRow("Allied", mine.AllianceSinceDay > -9000
                    ? Simulation.Loc.Format("with you, since day {0}", $"{mine.AllianceSinceDay:N0}")
                    : Tr("with you"),
                Tr("An attack on either of you is answered by the other."), "state.positive");
        if (mine is { NonAggressionPact: true })
            DetailRow("Non-aggression pact", Tr("Signed with you"));
        if (allies.Count > 0)
            DetailRow("Allied to", string.Join(", ", allies.Take(8).Select(ally => ally.Tag)) +
                                   (allies.Count > 8 ? $" +{allies.Count - 8:N0}" : ""));
        if (guarantees.Count > 0)
            DetailRow("Guarantees", string.Join(", ", guarantees.Take(8)) +
                                    (guarantees.Count > 8 ? $" +{guarantees.Count - 8:N0}" : ""),
                Tr("It has promised to fight for these countries if they are attacked."));
        if (guarantors.Count > 0)
            DetailRow("Guaranteed by", string.Join(", ", guarantors.Take(8)) +
                                       (guarantors.Count > 8 ? $" +{guarantors.Count - 8:N0}" : ""),
                Tr("These countries have promised to fight for it."));
    }

    /// <summary>Ask, accept, or walk out - whichever of the three this country is.</summary>
    private void ToggleSelectedAlliance()
    {
        if (!ValidForeignSelection()) return;
        var player = _campaign.Player;
        var target = _selectedNation;
        var relation = CampaignDiplomacy.ReadOrNeutral(_campaign, player.Id, target.Id);

        if (relation.Stance == DiplomaticStance.Allied)
        {
            _campaign.Commands.Enqueue(new BreakAllianceCommand { IssuerId = player.Id, TargetId = target.Id });
            _campaign.Commands.ApplyAll(_campaign);
            _countryActionFeedback.Text = Simulation.Loc.Format("Left the alliance with {0}.", target.DisplayName);
        }
        else if (AllianceSystem.OfferedTo(_campaign, player) is { } waiting && waiting.Id == target.Id)
        {
            _campaign.Commands.Enqueue(new AnswerAllianceCommand { IssuerId = player.Id, Accept = true });
            _campaign.Commands.ApplyAll(_campaign);
            _countryActionFeedback.Text = Simulation.Loc.Format("Allied with {0}.", target.DisplayName);
        }
        else if (!AllianceSystem.CanPropose(_campaign, player, target, out var refusal, preview: true))
        {
            _countryActionFeedback.Text = refusal;
        }
        else
        {
            OpenAllianceProposalReview(target);
            return;
        }
        RefreshCountryViews("Diplomacy");
    }

    /// <summary>The cheap half of an alliance, which commits nothing until it commits everything.</summary>
    private void ToggleSelectedGuarantee()
    {
        if (!ValidForeignSelection()) return;
        var relation = CampaignDiplomacy.Get(_campaign, _campaign.Player.Id, _selectedNation.Id);
        var on = !relation.GuaranteedBy(_campaign.Player.Id);
        _campaign.Commands.Enqueue(new GuaranteeCommand
        {
            IssuerId = _campaign.Player.Id, TargetId = _selectedNation.Id, Guarantee = on
        });
        _campaign.Commands.ApplyAll(_campaign);
        _countryActionFeedback.Text = relation.GuaranteedBy(_campaign.Player.Id)
            ? Simulation.Loc.Format("You guarantee {0}.", _selectedNation.DisplayName)
            : Simulation.Loc.Format("You no longer guarantee {0}.", _selectedNation.DisplayName);
        RefreshCountryViews("Diplomacy");
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
            if (nation.AnswersTo(_campaign.Player.Id) || CanInspectNation(nation.Id))
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

    /// <summary>
    /// What the alliance button is for right now, and what pressing it would get.
    ///
    /// The tooltip carries the answer before the asking: a country that would refuse says
    /// so, with the parts of the judgement that made it refuse. An offer a player can only
    /// discover by making it is a dice roll with a diplomatic theme.
    /// </summary>
    private void RefreshPactButtons(Nation nation)
    {
        if (nation == _campaign.Player) return;
        var relation = CampaignDiplomacy.ReadOrNeutral(_campaign, _campaign.Player.Id, nation.Id);
        var offered = AllianceSystem.OfferedTo(_campaign, _campaign.Player);

        if (relation.Stance == DiplomaticStance.Allied)
        {
            _allianceButton.Text = Tr("Break alliance");
            _allianceButton.Disabled = false;
            _allianceButton.TooltipText = Ui.Tip(Tr("Walk out. They remember it, and so does everybody watching."));
        }
        else if (offered is not null && offered.Id == nation.Id)
        {
            _allianceButton.Text = Tr("Accept alliance");
            _allianceButton.Disabled = false;
            _allianceButton.TooltipText = Ui.Tip(Tr("They asked. Accepting binds you to their wars and them to yours."));
        }
        else
        {
            _allianceButton.Text = Tr("Propose alliance");
            var can = AllianceSystem.CanPropose(_campaign, _campaign.Player, nation, out var refusal, preview: true);
            _allianceButton.Disabled = !can;
            _allianceButton.TooltipText = AllianceProposalTooltip(nation);
        }

        var guaranteed = relation.GuaranteedBy(_campaign.Player.Id);
        var asked = !guaranteed && Guarantee.RequesterTo(_campaign, _campaign.Player) is { } asking &&
            asking.Id == nation.Id;
        _guaranteeButton.Text = guaranteed
            ? Tr("Withdraw guarantee")
            : asked ? Tr("Grant guarantee") : Tr("Guarantee");
        _guaranteeButton.Disabled = relation.Stance == DiplomaticStance.AtWar;
        _guaranteeButton.TooltipText = Ui.Tip(guaranteed
            ? Tr("Take back the promise. They will notice.")
            : asked
                ? Tr("They have asked you to stand behind them. Answering here is a yes.")
                : Tr("Promise to fight for them if they are attacked. It costs nothing until it costs everything."));
    }

    /// <summary>Let one go, or take it in - the two ends an arrangement has.</summary>
    private void ReleaseSelectedSubject()
    {
        if (_campaign?.Player is null || _selectedNation is null) return;
        if (!_selectedNation.AnswersTo(_campaign.Player.Id)) return;
        var name = _selectedNation.DisplayName;
        SubjectSystem.Release(_campaign, _selectedNation);
        _countryActionFeedback.Text = Simulation.Loc.Format("{0} answers to nobody now.", name);
        Refresh();
        if (MapSelectionShowing) ShowMapSelectionContent();
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
        if (MapSelectionShowing) ShowMapSelectionContent();
    }

    private void ShowCountryInteraction(Nation nation, string location)
    {
        if (nation is null || _campaign?.Player is null) return;
        ReturnCountryActions();
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
        DetailRow(CountryNames.CapitalTitle(_campaign, nation), CountryNames.Capital(nation, _campaign));
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
            DetailRow("Stance", Simulation.DiplomaticVocabulary.StanceName(relation.Stance));
            foreach (var entry in relation.OpinionDrivers.Entries.OrderByDescending(item => System.Math.Abs(item.Value)).Take(5))
                DetailRow(entry.Source, $"{entry.Value:+0;-0;0}");
        }
        if (war is not null) DetailRow("War score", $"{(war.AttackerId == _campaign.Player.Id ? war.WarScore : -war.WarScore):+0.0;-0.0;0.0}");
        ShowPacts(nation);
        ShowDependencies(nation);
        RefreshCountryActions(nation);
    }

    /// <summary>
    /// Bring the diplomatic buttons up to date for this country - what each would do, and
    /// whether it may. Wherever the buttons are standing: the dossier or the country panel.
    /// </summary>
    private void RefreshCountryActions(Nation nation)
    {
        _countryActions.Visible = nation != _campaign.Player;
        RefreshPactButtons(nation);
        RefreshAgreementButtons(nation);
        RefreshSubmissionButtons(nation);
        RefreshCountryDiplomaticActButtons(nation);
        _subjectActions.Visible = nation.AnswersTo(_campaign.Player.Id);
        if (_subjectActions.Visible)
        {
            var may = SubjectSystem.MayAnnex(_campaign, _campaign.Player, nation, out var why);
            _integrateButton.Disabled = !may;
            _integrateButton.TooltipText = Ui.Tip(may
                ? Tr("Take it into your country. Its regions become yours.")
                : why);
        }
        if (!_countryActions.Visible) return;
        RefreshOutreachButton(_countryImproveButton, nation);
        RefreshDiplomaticPeaceButton(_negotiatePeaceButton, nation);
        _declareWarButton.Visible = !_negotiatePeaceButton.Visible;
        var canDeclare = CampaignDiplomacy.CanDeclareWar(_campaign, _campaign.Player.Id, nation.Id, out var reason);
        _declareWarButton.Disabled = !canDeclare;
        _quickUltimatumButton.Visible = !_negotiatePeaceButton.Visible;
        _quickUltimatumButton.Disabled = !canDeclare || nation.IsHuman;
        _quickUltimatumButton.TooltipText = Ui.Tip(!canDeclare ? reason : nation.IsHuman
            ? Tr("Human-controlled countries cannot be forced to answer automatically.")
            : Tr("Compose territorial, financial and treaty demands. Review before sending."));
        StyleButton(_quickUltimatumButton, false);
        _quickUltimatumButton.Text = Tr("Issue ultimatum");
        _declareWarButton.TooltipText = Ui.Tip(canDeclare ? Tr("Review allies and intervention risks before declaring war.") : reason);
        _declareWarButton.Text = Tr("Declare war");
        if (!canDeclare && !_negotiatePeaceButton.Visible && string.IsNullOrWhiteSpace(_countryActionFeedback.Text))
            _countryActionFeedback.Text = reason;
    }

    private void ImproveSelectedRelations()
    {
        if (!ValidForeignSelection()) return;
        var feedback = SendOrRecallDiplomaticMission(_selectedNation);
        _pendingWarTarget = -1;
        _countryActionFeedback.Text = feedback;
        RefreshCountryViews("Diplomatic mission");
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
        RefreshCountryViews("Diplomatic confrontation");
    }

    private void ConfirmOrDeclareWar()
    {
        if (!ValidForeignSelection()) return;
        OpenWarDeclaration(_selectedNation, _selectedState?.NationMapIndex == _selectedNation.MapIndex ? _selectedState.MapIndex : -1);
    }

    private void FocusSelectedCapital()
    {
        if (_selectedNation is null || !CapitalCore.HasSeat(_campaign, _selectedNation)) return;
        _globe.FocusOn(CapitalCore.EffectiveLatitude(_campaign, _selectedNation),
            CapitalCore.EffectiveLongitude(_campaign, _selectedNation), 7.1f);
        _countryActionFeedback.Text = Simulation.Loc.Format("Focused on {0}.", CountryNames.Capital(_selectedNation, _campaign));
    }

    private bool ValidForeignSelection() => _campaign?.Player is not null &&
        _selectedNation is not null && _selectedNation != _campaign.Player;
}
