using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private PanelContainer _campaignAlertStrip, _campaignAlertPopup;
    private VBoxContainer _campaignAlertList;
    private Label _campaignAlertHeading, _campaignAlertEmpty;
    private Button _campaignAlertRestore;
    private readonly Dictionary<CampaignAlertGroup, Button> _campaignAlertGroups = new();
    private readonly Dictionary<string, Button> _campaignAlertRows = new();
    private readonly Dictionary<CampaignAlertGroup, HBoxContainer> _campaignAlertClusters = new();
    private readonly Dictionary<string, Button> _campaignAlertIcons = new();
    private readonly CampaignAlertSnoozes _campaignAlertSnoozes = new();
    private IReadOnlyList<CampaignAlert> _campaignAlerts = Array.Empty<CampaignAlert>();
    private Campaign _campaignAlertsWorld;
    private int _campaignAlertsSeat = -1;
    private CampaignAlertGroup _campaignAlertGroup;

    private static string AlertColour(CampaignAlertGroup group) => group switch
    {
        CampaignAlertGroup.Must => "state.negative", CampaignAlertGroup.Should => "state.warning",
        CampaignAlertGroup.Could => "state.positive", _ => "state.info"
    };

    private void BuildCampaignAlerts()
    {
        _campaignAlertStrip = new PanelContainer { Name = "CampaignAlerts", MouseFilter = MouseFilterEnum.Stop };
        _campaignAlertStrip.AddThemeStyleboxOverride("panel", ShellPanelStyle()); _hudLayer.AddChild(_campaignAlertStrip);
        var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 3); _campaignAlertStrip.AddChild(row);
        foreach (var group in Enum.GetValues<CampaignAlertGroup>())
        {
            var cluster = new HBoxContainer { Name = "HintCluster" + group, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            cluster.AddThemeConstantOverride("separation", 3); row.AddChild(cluster); _campaignAlertClusters[group] = cluster;
            var captured = group;
            var button = new CampaignHintButton { CustomMinimumSize = new(42,42) };
            button.Pressed += () =>
            {
                var same = _campaignAlertGroup == captured && _campaignAlertPopup.Visible;
                _campaignAlertGroup = captured; _campaignAlertPopup.Visible = !same;
                RefreshCampaignAlertRows(); LayoutCampaignAlerts();
            };
            button.Name = "CampaignAlertGroup" + group; button.ClipText = true;
            button.AddThemeFontSizeOverride("font_size", 12);
            button.AddThemeColorOverride("font_color", Ui.Colour(AlertColour(group)));
            cluster.AddChild(button); _campaignAlertGroups[group] = button;
        }
        _campaignAlertPopup = new PanelContainer { Name = "CampaignAlertPopup", Visible = false };
        _campaignAlertPopup.AddThemeStyleboxOverride("panel", ShellPanelStyle()); _hudLayer.AddChild(_campaignAlertPopup);
        var body = new VBoxContainer(); body.AddThemeConstantOverride("separation", 5); _campaignAlertPopup.AddChild(body);
        var head = new HBoxContainer(); body.AddChild(head);
        _campaignAlertHeading = Title("", 15, Ui.Colour("text.accent"));
        _campaignAlertHeading.SizeFlagsHorizontal = SizeFlags.ExpandFill; head.AddChild(_campaignAlertHeading);
        head.AddChild(ActionButton("×", () => _campaignAlertPopup.Hide(), 32, 28));
        var scroll = new ScrollContainer { Name = "CampaignAlertsScroll", CustomMinimumSize = new(0, 220),
            SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        body.AddChild(scroll);
        _campaignAlertList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _campaignAlertList.AddThemeConstantOverride("separation", 4); scroll.AddChild(_campaignAlertList);
        _campaignAlertEmpty = Title("", 13, Ui.Colour("text.body_soft")); _campaignAlertList.AddChild(_campaignAlertEmpty);
        _campaignAlertRestore = ActionButton("Show hidden hints", () =>
        { _campaignAlertSnoozes.Clear(); RefreshCampaignAlerts(_situations); }, 0, 28);
        _campaignAlertRestore.Name = "RestoreCampaignAlerts"; body.AddChild(_campaignAlertRestore);
    }

    private void LayoutCampaignAlerts()
    {
        if (_campaignAlertStrip is null) return;
        var iconCount = Enum.GetValues<CampaignAlertGroup>().Sum(g => Math.Min(3,
            _campaignAlerts.Count(a => a.Group == g && !_campaignAlertSnoozes.Hidden(a,_campaign.ElapsedDays))));
        var width = MathF.Min(820, MathF.Min(MathF.Max(400,Size.X-440), 196+iconCount*46));
        _campaignAlertStrip.Position = new((Size.X - width) * .5f, _hudTopPanel.OffsetBottom + 6);
        _campaignAlertStrip.Size = new(width, 46);
        _campaignAlertPopup.Position = _campaignAlertStrip.Position + new Vector2(0, 50);
        _campaignAlertPopup.Size = new(MathF.Min(width, 570), MathF.Min(355, MathF.Max(260, Size.Y - _campaignAlertPopup.Position.Y - 70)));
    }

    private void RefreshCampaignAlerts(IReadOnlyList<SituationItem> report)
    {
        if (_campaignAlertStrip is null) BuildCampaignAlerts();
        if (_campaignAlertsWorld != _campaign || _campaignAlertsSeat != _campaign.Player.Id)
        {
            _campaignAlertSnoozes.Clear(); _campaignAlertPopup.Hide();
            _campaignAlertAnnounced.Clear(); _campaignAlertAnnouncementsArmed = false;
            _campaignAlertsWorld = _campaign; _campaignAlertsSeat = _campaign.Player.Id;
        }
        _campaignAlerts = CampaignAlerts.Read(_campaign, _campaign.Player, report, _observerMode);
        _campaignAlertSnoozes.Reconcile(_campaignAlerts, _campaign.ElapsedDays);
        AnnounceCampaignAlertChanges();
        _campaignAlertStrip.Visible = !_observerMode && _campaign.Defines.CampaignAlertsEnabled;
        if (!_campaignAlertStrip.Visible) _campaignAlertPopup.Hide();
        foreach (var (group, button) in _campaignAlertGroups)
        {
            var count = _campaignAlerts.Count(a => a.Group == group && !_campaignAlertSnoozes.Hidden(a, _campaign.ElapsedDays));
            ((CampaignHintButton)button).Present(CampaignHintArt.Of(group.ToString().ToLowerInvariant()), Ui.Colour(AlertColour(group)), count.ToString());
            button.Text = "";
            button.TooltipText = Ui.Tip(Tr(CampaignAlerts.GroupName(group)) + "\n" + Tr("Left-click a hint to open its destination. Right-click a hint to hide it temporarily."));
            StyleButton(button, _campaignAlertPopup.Visible && _campaignAlertGroup == group);
        }
        RefreshCampaignAlertIcons();
        LayoutCampaignAlerts();
        if (_campaignAlertPopup.Visible) RefreshCampaignAlertRows();
    }

    private static string CampaignHintIcon(CampaignAlert a) => a.Situation?.Good is { } good ? CommodityCatalog.IconNameOf(good) : a.Route switch
    {
        CampaignAlertRoute.Event => "alert_event", CampaignAlertRoute.Elections => "elections", CampaignAlertRoute.Personnel => "personnel",
        CampaignAlertRoute.Research => "research", CampaignAlertRoute.Decisions => "decision",
        CampaignAlertRoute.Battle => "alert_battle", CampaignAlertRoute.War => "wars",
        CampaignAlertRoute.Doctrine or CampaignAlertRoute.Army => "wars",
        _ => a.Situation?.Route switch
        {
            SituationRoute.Finance => "treasury", SituationRoute.Market => "trade", SituationRoute.Army => "alert_supply",
            SituationRoute.Construction => "alert_construction", SituationRoute.PeaceOffer => "alert_peace",
            SituationRoute.Crises => "unrest", _ => "diplomacy"
        }
    };

    private void HideCampaignAlert(string id)
    {
        var current = _campaignAlerts.FirstOrDefault(a => a.Id == id);
        if (current is not null) _campaignAlertSnoozes.Hide(current, _campaign.ElapsedDays, _campaign.Defines.CampaignAlertSnoozeDays);
        RefreshCampaignAlerts(_situations);
    }

    // A hint that arrives speaks once, the way a courier does: the urgent group gets the
    // situation horn, the advisory one the softer paper cue. Nothing plays on the first
    // refresh after a load or a seat change, and a hint that was merely snoozed is not news.
    private readonly HashSet<string> _campaignAlertAnnounced = new(StringComparer.Ordinal);
    private bool _campaignAlertAnnouncementsArmed;

    private void AnnounceCampaignAlertChanges()
    {
        if (!_campaignAlertStrip.Visible) { _campaignAlertAnnounced.Clear(); _campaignAlertAnnouncementsArmed = false; return; }
        var seen = _campaignAlertAnnounced;
        seen.IntersectWith(_campaignAlerts.Select(a=>a.Id));
        string urgent = null, advisory = null;
        foreach (var alert in _campaignAlerts)
        {
            if (alert.Group is not (CampaignAlertGroup.Must or CampaignAlertGroup.Should)) continue;
            if (!seen.Add(alert.Id)) continue;
            if (_campaignAlertSnoozes.Hidden(alert, _campaign.ElapsedDays)) continue;
            if (alert.Group == CampaignAlertGroup.Must) urgent ??= alert.Id;
            else advisory ??= alert.Id;
        }
        if (!_campaignAlertAnnouncementsArmed)
        {
            // First refresh of a seat: everything is old news. Remember it, stay quiet.
            _campaignAlertAnnouncementsArmed = true;
            return;
        }
        if (urgent is not null) PlayStrategicCue("situation_alert");
        else if (advisory is not null) PlayStrategicCue("deal_received");
    }

    private void RefreshCampaignAlertIcons()
    {
        foreach (var id in _campaignAlertIcons.Keys.Where(id => !_campaignAlerts.Any(a => a.Id == id)).ToArray())
        { var old = _campaignAlertIcons[id]; old.GetParent().RemoveChild(old); old.QueueFree(); _campaignAlertIcons.Remove(id); }
        foreach (var old in _campaignAlertIcons.Values) old.Hide();
        foreach (var group in Enum.GetValues<CampaignAlertGroup>())
        foreach (var alert in _campaignAlerts.Where(a => a.Group == group && !_campaignAlertSnoozes.Hidden(a,_campaign.ElapsedDays)).Take(3))
        {
            if (!_campaignAlertIcons.TryGetValue(alert.Id,out var button))
            {
                var id = alert.Id;
                button = new CampaignHintButton { Name="CampaignIcon_"+id.Replace(':','_'), CustomMinimumSize=new(42,42) };
                button.Pressed += () => FollowCampaignAlert(id);
                button.GuiInput += input =>
                {
                    if (input is not InputEventMouseButton { Pressed:true,ButtonIndex:MouseButton.Right }) return;
                    button.AcceptEvent(); HideCampaignAlert(id);
                };
                _campaignAlertClusters[group].AddChild(button); _campaignAlertIcons[id]=button;
            }
            if (button.GetParent()!=_campaignAlertClusters[group]) button.Reparent(_campaignAlertClusters[group]);
            var explanation=alert.Situation is { } situation ? "\n\n"+string.Join("\n",situation.Facts.Take(3).Select(f=>f.Label+": "+f.Value))+
                "\n\n"+string.Join("\n",situation.Consequences.Take(1)) : "";
            button.TooltipText = Tr(CampaignAlerts.GroupName(group))+"\n"+alert.Title+"\n\n"+alert.Summary+explanation+"\n\n"+
                Tr("Left-click: open. Right-click: hide temporarily (urgent hints return tomorrow).");
            button.AccessibilityName=alert.Title+". "+alert.Summary;
            ((CampaignHintButton)button).Present(CampaignHintArt.Of(CampaignHintIcon(alert)), Ui.Colour(AlertColour(group)));
            StyleCampaignHint(button, group); button.Visible=true;
        }
    }

    private void RefreshCampaignAlertRows()
    {
        _campaignAlertHeading.Text = Tr(CampaignAlerts.GroupName(_campaignAlertGroup));
        var live = _campaignAlerts.ToDictionary(a => a.Id, StringComparer.Ordinal);
        foreach (var id in _campaignAlertRows.Keys.Where(id => !live.ContainsKey(id)).ToArray())
        { var old = _campaignAlertRows[id]; _campaignAlertList.RemoveChild(old); old.QueueFree(); _campaignAlertRows.Remove(id); }
        foreach (var old in _campaignAlertRows.Values) old.Hide();
        var index = 0;
        foreach (var alert in _campaignAlerts.Where(a => a.Group == _campaignAlertGroup && !_campaignAlertSnoozes.Hidden(a, _campaign.ElapsedDays)))
        {
            if (!_campaignAlertRows.TryGetValue(alert.Id, out var button))
            {
                var id = alert.Id;
                button = new CampaignHintButton { CustomMinimumSize = new(0,48), GlyphOnLeft = true };
                button.Pressed += () => FollowCampaignAlert(id);
                button.Name = "CampaignHint_" + id.Replace(':', '_');
                button.SizeFlagsHorizontal = SizeFlags.ExpandFill; button.Alignment = HorizontalAlignment.Left;
                button.ClipText = true;
                button.GuiInput += input =>
                {
                    if (input is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }) return;
                    button.AcceptEvent(); HideCampaignAlert(id);
                };
                _campaignAlertRows.Add(id, button); _campaignAlertList.AddChild(button);
            }
            button.Text = alert.Title; button.AccessibilityName = alert.Title + ". " + alert.Summary;
            button.TooltipText = Ui.Tip(alert.Title + "\n" + alert.Summary + "\n\n" +
                Tr("Left-click: open. Right-click: hide temporarily (urgent hints return tomorrow)."));
            ((CampaignHintButton)button).Present(CampaignHintArt.Of(CampaignHintIcon(alert)), Ui.Colour(AlertColour(alert.Group)));
            StyleCampaignHint(button, alert.Group, row: true);
            button.Visible = true; _campaignAlertList.MoveChild(button, index++);
        }
        _campaignAlertEmpty.Text = Tr("No visible hints in this group."); _campaignAlertEmpty.Visible = index == 0;
        _campaignAlertList.MoveChild(_campaignAlertEmpty, index);
        _campaignAlertRestore.Text = Loc.Format("Show hidden hints · {0}", _campaignAlertSnoozes.Count);
        _campaignAlertRestore.Disabled = _campaignAlertSnoozes.Count == 0;
    }

    private static void StyleCampaignHint(Button button, CampaignAlertGroup group, bool row = false)
    {
        if (button.GetThemeStylebox("normal") is StyleBoxFlat current &&
            current.BorderColor == Ui.Colour(AlertColour(group),.65f) && current.BgColor == Ui.Colour("shell.background") &&
            current.ContentMarginLeft == (row ? 50 : 5)) return;
        foreach (var state in new[] { "normal", "hover", "pressed", "focus" })
        {
            var style = new StyleBoxFlat { BgColor = Ui.Colour(state == "normal" ? "shell.background" : "war.card"),
                BorderColor = Ui.Colour(AlertColour(group), state == "normal" ? .65f : 1),
                BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = state == "normal" ? 1 : 2, BorderWidthBottom = 2 };
            style.SetCornerRadiusAll(4); style.SetContentMarginAll(5);
            if (row) style.ContentMarginLeft = 50;
            button.AddThemeStyleboxOverride(state, style);
        }
    }

    private void FollowCampaignAlert(string id)
    {
        // Never execute the action attached to an old card. Navigation is not consent
        // to send a territorial offer, accept an election or spend political credit.
        var alert = CampaignAlerts.Read(_campaign, _campaign.Player, observer: _observerMode).FirstOrDefault(a => a.Id == id);
        if (alert is null) { RefreshSituationRoom(force: true); return; }
        _campaignAlertPopup.Hide();
        switch (alert.Route)
        {
            case CampaignAlertRoute.Situation:
                if (_situationLayer is null) BuildSituationRoom();
                if (alert.Situation is null) { OpenSituationRoom(); break; }
                if (!SituationDestinationEnabled(alert.Situation)) { OpenSituationRoom(); break; }
                if (id is "detached_land" or "enclave_swap" or "border_pore")
                { OpenSituationRoom(); _situationSelected = id; RefreshSituationList(); break; }
                _situationSelected = id; FollowSituation(); break;
            case CampaignAlertRoute.Event: RefreshEventInterface(); break;
            case CampaignAlertRoute.Elections:
            case CampaignAlertRoute.Personnel: if (MechanicFeatures.ScreenEnabled(_campaign, "State Affairs")) OpenPoliticalAppointments(); break;
            case CampaignAlertRoute.Research: if (_campaign.FeatureEnabled("research")) OpenResearchScreen(); break;
            case CampaignAlertRoute.Doctrine: OpenDoctrineScreen(); break;
            case CampaignAlertRoute.Army: OpenScreen("Military"); break;
            case CampaignAlertRoute.Decisions: OpenScreen("Decisions"); break;
            case CampaignAlertRoute.Battle: OpenFirstPlayerBattle(); break;
            case CampaignAlertRoute.War: OpenWarLedger(); ShowWarLedgerDetail(alert.TargetId); break;
        }
        UpdateScreenButtons();
    }
}

