using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The region panel's "Land and troops" block (LandArrangements): who holds the region by
/// arrangement, and what the player may do about it - ask to lease it, end a lease, keep the
/// troops when a lease runs out, send troops in without a war, hold the vote, go home. The
/// steps that cannot be taken back ask for a second click, as a declaration of war does.
/// </summary>
public partial class DealOrForceGame
{
    private VBoxContainer _arrangementBox;
    private Label _arrangementStatus;
    private HFlowContainer _arrangementButtons;
    private Label _arrangementFeedback;
    private string _arrangementPending = "";

    private void EnsureArrangementBox()
    {
        if (_arrangementBox is not null || _regionControls is null) return;
        _arrangementBox = new VBoxContainer { Visible = false };
        _arrangementBox.AddThemeConstantOverride("separation", 5);
        _regionControls.AddChild(_arrangementBox);
        _arrangementBox.AddChild(Title("LAND AND TROOPS", 12, Ui.Colour("text.caption")));
        _arrangementStatus = Title("", 12, Ui.Colour("text.bright"));
        _arrangementStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _arrangementBox.AddChild(_arrangementStatus);
        _arrangementButtons = new HFlowContainer();
        _arrangementButtons.AddThemeConstantOverride("h_separation", 5);
        _arrangementButtons.AddThemeConstantOverride("v_separation", 5);
        _arrangementBox.AddChild(_arrangementButtons);
        _arrangementFeedback = Title("", 12, Ui.Colour("text.accent_dim"));
        _arrangementFeedback.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _arrangementBox.AddChild(_arrangementFeedback);
    }

    private void RefreshArrangementActions()
    {
        EnsureArrangementBox();
        if (_arrangementBox is null) return;
        foreach (var child in _arrangementButtons.GetChildren()) child.QueueFree();
        var player = _campaign?.Player;
        var region = _selectedState;
        if (player is null || region is null || _observerMode || !_campaign.FeatureEnabled("land_arrangements"))
        {
            _arrangementBox.Visible = false;
            return;
        }
        var owner = _campaign.NationByMapIndex(region.NationMapIndex);
        var arrangement = LandArrangementSystem.Covering(_campaign, region.MapIndex);
        _arrangementStatus.Text = LandArrangementSystem.Describe(_campaign, region);
        _arrangementStatus.Visible = _arrangementStatus.Text.Length > 0;
        var ours = owner?.Id == player.Id;

        if (ours)
        {
            if (arrangement is { Kind: LandArrangementSystem.Lease, LeavingOnDay: < 0 })
                AddArrangementButton("End the lease", "end_lease", "",
                    "The tenant has three months to leave - or to decide not to. They will not forget it.", confirm: true);
        }
        else if (owner is not null && owner.Movement is null)
        {
            var mine = arrangement is not null && arrangement.HolderId == player.Id;
            if (arrangement is null)
            {
                var rent = LandArrangementSystem.FairRent(_campaign, new[] { region });
                AddArrangementButton("Ask to lease it", "request_lease", LandArrangementSystem.LeaseRefusal(_campaign, player, region),
                    Simulation.Loc.Format("Ask {0} to let us hold this region for {1} years at {2:0.000} bn a year. Ours to garrison and to base fleets in; theirs in law.",
                        owner.DisplayName, _campaign.Defines.LeaseDefaultYears, rent));
            }
            if (mine && arrangement.Kind == LandArrangementSystem.Lease && arrangement.LeavingOnDay >= 0)
                AddArrangementButton("Keep the troops there", "stay", "",
                    "The lease is over; our soldiers stay anyway. The world will call it what it is.", confirm: true);
            if (arrangement is null || mine && arrangement.Kind == LandArrangementSystem.Lease)
                AddArrangementButton("Send troops in", "send_troops", LandArrangementSystem.PresenceRefusal(_campaign, player, region),
                    Simulation.Loc.Format("Our soldiers take the region without a war - for our people there, from a base we hold, or because nobody is left to stop us. {0} may swallow it, or fight.", owner.DisplayName),
                    confirm: true);
            if (mine && arrangement.Kind == LandArrangementSystem.Presence)
                AddArrangementButton("Hold a referendum", "referendum", LandArrangementSystem.ReferendumRefusal(_campaign, player, arrangement),
                    "A vote on joining us, counted by our soldiers, and the region is ours. Who lives there decides how much of the world believes it.",
                    confirm: true);
            if (mine && arrangement.Kind == LandArrangementSystem.Presence)
                AddArrangementButton("Proclaim a republic", "republic", LandArrangementSystem.ReferendumRefusal(_campaign, player, arrangement),
                    "The region declares itself a state, under our protection: theirs on paper, ours in practice. Its old country will call it a breakaway and may come back for it.",
                    confirm: true);
            if (mine)
                AddArrangementButton(arrangement.Kind == LandArrangementSystem.Lease ? "Give up the lease" : "Withdraw the troops", "withdraw", "",
                    "Our soldiers come home and the region goes back to its owner.", confirm: true);
        }
        // A region we hold in a war: the vote under the troops, the last step to owning it.
        if (!ours && region.HeldById == player.Id && HeldRegions.Administered(_campaign, region))
            AddArrangementButton("Hold a referendum", "held_referendum", HeldRegions.ReferendumRefusal(_campaign, player, region),
                "A vote on joining us in the region our troops hold, in the middle of the war. Its owner will not recognise it and the world will count it against us; the region becomes ours in law.",
                confirm: true);
        var any = _arrangementStatus.Visible || _arrangementButtons.GetChildCount() > 0;
        _arrangementBox.Visible = any;
        if (any) _regionControls.Visible = true;
    }

    private void AddArrangementButton(string caption, string action, string refusal, string explanation, bool confirm = false)
    {
        var button = ActionButton(caption, () => IssueArrangement(action, confirm), 0, 30);
        var pending = _arrangementPending == ArrangementKey(action);
        button.Disabled = refusal.Length > 0;
        button.TooltipText = Ui.Tip(Tr(explanation) + (refusal.Length > 0 ? "\n\n" + refusal : "") +
                                    (pending ? "\n\n" + Tr("Click again to confirm.") : ""));
        if (pending) button.Modulate = Ui.Colour("state.warning");
        _arrangementButtons.AddChild(button);
    }

    private string ArrangementKey(string action) => action + ":" + (_selectedState?.MapIndex ?? -1);

    private void IssueArrangement(string action, bool confirm)
    {
        if (_campaign?.Player is null || _selectedState is null) return;
        if (confirm && _arrangementPending != ArrangementKey(action))
        {
            _arrangementPending = ArrangementKey(action);
            _arrangementFeedback.Text = Tr("This cannot be taken back. Click the highlighted button again to confirm.");
            RefreshArrangementActions();
            return;
        }
        _arrangementPending = "";
        var command = new LandArrangementCommand
        {
            IssuerId = _campaign.Player.Id, Action = action, RegionMapIndex = _selectedState.MapIndex,
            Years = _campaign.Defines.LeaseDefaultYears
        };
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        _arrangementFeedback.Text = command.Result.Length > 0 ? command.Result : command.Done ? "✓" : "";
        if (command.Done && action == "send_troops") PlayStrategicCue("war_declared");
        RefreshArrangementActions();
    }

    /// <summary>Lease requests and renewals waiting on the player's desk.</summary>
    private int LeaseOffersIncoming() => _campaign?.Player is null ? 0 : System.Linq.Enumerable.Count(_campaign.LeaseOffers,
        o => o.State == "pending" && o.ToId == _campaign.Player.Id && o.ExpiresDay > _campaign.ElapsedDays);

    private string LeaseInboxKey() => _campaign?.Player is null ? "" : string.Join(";", System.Linq.Enumerable.Select(
        System.Linq.Enumerable.Where(_campaign.LeaseOffers, o => o.ToId == _campaign.Player.Id), o => $"{o.Id}:{o.State}"));

    /// <summary>One letter per offer, answered where it lies: a lease asked for, or a lease whose term has run out.</summary>
    private void AddLeaseInboxRows()
    {
        if (_campaign?.Player is null || _dealInboxRows is null) return;
        foreach (var offer in _campaign.LeaseOffers)
        {
            if (offer.State != "pending" || offer.ToId != _campaign.Player.Id || offer.ExpiresDay <= _campaign.ElapsedDays) continue;
            var asker = _campaign.NationById(offer.FromId);
            var region = _campaign.States.Find(s => s.MapIndex == offer.Region);
            var letter = new VBoxContainer();
            letter.AddThemeConstantOverride("separation", 4);
            var purpose = Simulation.Loc.Get("lease.purpose." + (offer.Purpose.Length > 0 ? offer.Purpose : "military_base"));
            letter.AddChild(WrappedUltimatumText(offer.Renewal
                ? Simulation.Loc.Format("Lease renewal · {0}\nIts lease of {1} ({2}) has run out. Renew for {3} years at {4:0.000} bn a year? Refuse, and they have three months to leave - or to decide not to.",
                    asker?.DisplayName ?? "?", region?.DisplayName ?? "?", purpose, offer.Years, offer.RentPerYear)
                : Simulation.Loc.Format("Lease request · {0}\nIt asks to hold {1} as a {2} for {3} years at {4:0.000} bn a year. The land stays ours in law.",
                    asker?.DisplayName ?? "?", region?.DisplayName ?? "?", purpose, offer.Years, offer.RentPerYear), 14));
            var buttons = new HBoxContainer();
            buttons.AddThemeConstantOverride("separation", 6);
            var id = offer.Id;
            buttons.AddChild(ActionButton("Accept", () => AnswerLease(id, true), 110, 30, true));
            buttons.AddChild(ActionButton("Refuse", () => AnswerLease(id, false), 110, 30));
            letter.AddChild(buttons);
            letter.AddChild(new HSeparator());
            _dealInboxRows.AddChild(letter);
        }
    }

    private void AnswerLease(int offerId, bool accept)
    {
        var command = new LandArrangementCommand
        {
            IssuerId = _campaign.Player.Id, Action = accept ? "accept_offer" : "refuse_offer", OfferId = offerId
        };
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        RefreshNegotiationNotifications();
        RefreshDealInbox(force: true);
    }
}
