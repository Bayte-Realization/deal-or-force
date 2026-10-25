using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The conflicts screen: the world's hot spots, its wars and the backers behind them.
///
/// Three lists, one page. Hot spots are the pairs of countries with tension between them -
/// friction, crisis, armed clashes - with what puts them there and what has happened between
/// them lately. Wars are every war being fought, how large it has grown, and who is arming
/// each side from outside. Ours is the player's own: the countries it is in a standoff with
/// and the wars it is backing. The page on the right explains the selected row and holds
/// what can be done about it: open talks to bring a crisis down, back a side of a war or stop.
/// </summary>
public partial class DealOrForceGame
{
    private Control _conflictsLayer;
    private VBoxContainer _conflictsList;
    private VBoxContainer _conflictsPage;
    private Label _conflictsSummary;
    private Label _conflictsFeedback;
    private readonly Dictionary<int, Button> _conflictTabs = new();
    private int _conflictTab;
    private long _conflictPair = -1;
    private int _conflictWar = -1;
    private int _conflictsBuiltDay = int.MinValue;

    private static readonly string[] ConflictTabNames = { "Hot spots", "Wars and backers", "Our conflicts", "Territorial disputes", "Organizations", "The world" };
    /// <summary>The order the tabs stand in: tension and wars first, then the order of the world.</summary>
    private static readonly int[] ConflictTabOrder = { 0, 1, 2, 5, 3, 4 };
    private int _territoryRegion = -1, _territoryClaimant = -1;

    private void BuildConflictsScreen()
    {
        _conflictsLayer = FullLayer("Conflicts");
        _conflictsLayer.Visible = false;
        _hudLayer.AddChild(_conflictsLayer);
        var shade = new ColorRect { Color = new Color(Ui.Colour("surface.background"), .72f), MouseFilter = MouseFilterEnum.Stop };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _conflictsLayer.AddChild(shade);

        var panel = TexturedPanel(_conflictsLayer);
        panel.Name = "ConflictsPanel";
        Ui.Place(panel, "modal.conflicts");
        var rows = PanelRows(panel, 22, 16, 9);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        rows.AddChild(header);
        var title = Title("CONFLICTS", 25, Ui.Colour("text.accent"), upper: true);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(title);
        header.AddChild(HandbookButton("tensions"));
        var close = ActionButton("×", CloseConflictsScreen, 38, 32);
        close.Name = "CloseConflicts";
        close.TooltipText = Ui.Tip(Tr("Close"));
        header.AddChild(close);

        _conflictsSummary = Title("", 13, Ui.Colour("text.body_soft"));
        rows.AddChild(_conflictsSummary);

        var tabs = new HFlowContainer { Name = "ConflictNavigation" };
        tabs.AddThemeConstantOverride("h_separation", 6);
        tabs.AddThemeConstantOverride("v_separation", 4);
        rows.AddChild(tabs);
        foreach (var index in ConflictTabOrder)
        {
            var captured = index;
            var tab = ActionButton(ConflictTabNames[index], () => ChangeConflictTab(captured), 0, 32);
            tab.Name = "ConflictTab" + index;
            tab.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.tab", 13));
            tab.TooltipText = Ui.Tip(Tr(ConflictTabNames[index]));
            _conflictTabs[index] = tab;
            tabs.AddChild(tab);
        }

        var body = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 16);
        rows.AddChild(body);

        var listDeck = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(310, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = .65f, SizeFlagsVertical = SizeFlags.ExpandFill
        };
        body.AddChild(listDeck);
        BuildConflictSearch(listDeck);
        _conflictsListScroll = new ScrollContainer
        {
            Name = "ConflictListScroll", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        listDeck.AddChild(_conflictsListScroll);
        _conflictsList = new VBoxContainer { Name = "ConflictsList", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _conflictsList.AddThemeConstantOverride("separation", 3);
        _conflictsListScroll.AddChild(_conflictsList);
        BuildConflictPager(listDeck);

        var pagePanel = new PanelContainer
        {
            Name = "ConflictDetailPanel",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        pagePanel.SetMeta("atlas_section_ground", "surface.inset");
        pagePanel.AddThemeStyleboxOverride("panel", AtlasSurfaceSkin.Section("surface.inset"));
        AtlasSurfaceSkin.Attach(pagePanel, AtlasSurfaceSkin.Level.Inset, "surface.inset");
        body.AddChild(pagePanel);

        _conflictsPageScroll = new ScrollContainer
        {
            Name = "ConflictDetailScroll",
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        pagePanel.AddChild(_conflictsPageScroll);
        _conflictsPage = new VBoxContainer { Name = "ConflictsPage", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _conflictsPage.AddThemeConstantOverride("separation", 10);
        _conflictsPageScroll.AddChild(_conflictsPage);

        _conflictsFeedback = Title("", 13, Ui.Colour("text.accent"));
        rows.AddChild(_conflictsFeedback);
    }

    private void OpenConflictsScreen()
    {
        _openScreen = "Conflicts";
        _sidePanel.Visible = false;
        RaiseWorkspaceLayer(_conflictsLayer);
        _conflictsFeedback.Text = "";
        _conflictsSummary.Text = Tr("Reading the world's conflicts.");
        UpdateScreenButtons();
        Callable.From(() =>
        {
            if (_conflictsLayer is { Visible: true }) RefreshConflictsScreen(force: true);
        }).CallDeferred();
    }

    private void CloseConflictsScreen()
    {
        if (_conflictsLayer is null) return;
        _conflictsLayer.Visible = false;
        if (_openScreen == "Conflicts") _openScreen = "";
        UpdateScreenButtons();
    }

    /// <summary>Redraw on a new day, or now when something was changed from the page.</summary>
    private void RefreshConflictsScreen(bool force = false)
    {
        if (_conflictsLayer is not { Visible: true } || _campaign?.Player is null) return;
        if (!force && InterfaceInteractionGuard.Busy(_conflictsLayer)) return;
        if (!force && _conflictsBuiltDay == _campaign.ElapsedDays) return;
        _conflictsBuiltDay = _campaign.ElapsedDays;
        foreach (var (index, tab) in _conflictTabs) StyleButton(tab, index == _conflictTab);

        var hot = Escalation.Hottest(_campaign, _campaign.Defines.TensionFrictionAt).ToList();
        var crises = hot.Count(r => Escalation.StageOf(r) == TensionStage.Crisis);
        var clashes = hot.Count(r => Escalation.StageOf(r) == TensionStage.Clashes);
        var wars = _campaign.Wars.Where(w => !w.Ended).ToList();
        var backedWarIds = ProxyWars.Active(_campaign).Select(s => s.WarId).ToHashSet();
        var backed = wars.Count(w => backedWarIds.Contains(w.Id));
        _conflictsSummary.Text = Simulation.Loc.Format(
            "{0} standoffs in the world: {1} in crisis, {2} with armed clashes. {3} wars, {4} of them with foreign backers.",
            hot.Count, crises, clashes, wars.Count, backed);

        var scroll = _conflictsListScroll.ScrollVertical;
        if (_conflictTab is 0 or 1 or 2) RefreshConflictIndex(hot, wars);
        else if (_conflictTab == 5) ListWorldOrder();
        else
        {
            _conflictRenderedTab = -1;
            _conflictRows.Clear();
            if (_conflictTab == 4) RefreshOrganizationIndex();
            else
            {
                _organizationIndexReady = false;
                ClearRuntimeChildren(_conflictsList);
                ListTerritorialDisputes();
            }
        }
        _conflictSearch.Visible = _conflictPager.Visible = _conflictTab is 0 or 1 or 2;
        _conflictListHeading.Visible = _conflictTab == 5;
        _conflictsListScroll.SetDeferred(ScrollContainer.PropertyName.ScrollVertical, scroll);
        ShowConflictPage();
    }

    // ------------------------------------------------------------------- the lists

    private Label Caption(string text)
    {
        var label = Title(text, 11, Ui.Colour("text.caption"), upper: true);
        label.CustomMinimumSize = new Vector2(0, 22);
        label.VerticalAlignment = VerticalAlignment.Bottom;
        return label;
    }

    private Button ConflictRowButton(bool selected, float height = 34)
    {
        var button = new ConflictEntryButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, height) };
        button.AddThemeStyleboxOverride("normal", ShellRowStyle(selected));
        button.AddThemeStyleboxOverride("hover", ShellRowStyle(true));
        button.AddThemeStyleboxOverride("pressed", ShellRowStyle(true));
        button.AddThemeStyleboxOverride("focus", ShellRowStyle(selected));
        return button;
    }

    private static Color StageColour(TensionStage stage) => stage switch
    {
        TensionStage.Clashes => Ui.Colour("state.critical"),
        TensionStage.Crisis => Ui.Colour("state.warning"),
        TensionStage.Friction => Ui.Colour("text.accent_dim"),
        TensionStage.War => Ui.Colour("state.negative"),
        _ => Ui.Colour("state.positive")
    };

    private static string StageName(TensionStage stage) => stage switch
    {
        TensionStage.Clashes => Tr("Armed clashes"),
        TensionStage.Crisis => Tr("Crisis"),
        TensionStage.Friction => Tr("Friction"),
        TensionStage.War => Tr("At war"),
        _ => Tr("Calm")
    };

    /// <summary>A horizontal bar from 0 to 100, in the colour of what it measures.</summary>
    private static Control Gauge(float value, Color colour, float width = 110f)
    {
        var holder = new Control { CustomMinimumSize = new Vector2(width, 10), MouseFilter = MouseFilterEnum.Ignore };
        holder.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        var track = new ColorRect { Color = Ui.Colour("war.track"), MouseFilter = MouseFilterEnum.Ignore };
        track.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        holder.AddChild(track);
        var fill = new ColorRect { Color = colour, MouseFilter = MouseFilterEnum.Ignore };
        fill.AnchorLeft = 0; fill.AnchorTop = 0; fill.AnchorBottom = 1;
        fill.AnchorRight = Math.Clamp(value / 100f, 0f, 1f);
        holder.AddChild(fill);
        return holder;
    }

    /// <summary>Apply a command now, the way the other screens do, so the page answers at once.</summary>
    private void Submit(ICampaignCommand command)
    {
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
    }

    private static long PairKey(DiplomaticRelation relation) => ((long)relation.A << 32) | (uint)relation.B;

    private string TensionTip(DiplomaticRelation relation)
    {
        if (CampaignDiplomacy.AtWar(_campaign, relation.A, relation.B)) return Tr("At war");
        var d = Escalation.Drivers(_campaign, relation);
        var lines = new List<string> { Simulation.Loc.Format("Tension {0:0} of 100, heading for {1:0}.", relation.Tension, d.Target) };
        void Add(string what, float value) { if (value >= 0.5f) lines.Add($"  {Tr(what)}: +{value:0}"); }
        Add("Standing dispute", d.Structural);
        Add("Mutual hostility", d.Hostility);
        Add("Grievance from past wars", d.Grievance);
        Add("Land one holds of the other's", d.LostLand);
        Add("Declared rivalry", d.Rivalry);
        Add("Opposite sides of other wars", d.Proxy);
        Add("Opposed politics", d.Ideology);
        lines.Add(Simulation.Loc.Format("Distance and treaties multiply it by {0:0.00}.", d.Factor));
        if (MathF.Abs(d.Baseline) >= 0.5f)
            lines.Add($"  {Tr("Where it stood in January 2024 (fading)")}: {(d.Baseline > 0f ? "+" : "")}{d.Baseline:0}");
        if (Escalation.StageOf(relation) < TensionStage.Clashes &&
            Escalation.Deterred(_campaign, _campaign.NationById(relation.A), _campaign.NationById(relation.B), refreshAssessment: false))
            lines.Add(Tr("Neither side would fire first - outmatched by the other and the friends behind it, held back by its own government, or busy with a war elsewhere - so it stops short of shooting."));
        return string.Join("\n", lines);
    }

    // -------------------------------------------------------------------- the page

    private void ShowConflictPage()
    {
        var scroll = _conflictsPageScroll.ScrollVertical;
        ClearRuntimeChildren(_conflictsPage);
        _conflictsPageScroll.SetDeferred(ScrollContainer.PropertyName.ScrollVertical, scroll);
        if (_conflictTab == 3) { ShowTerritoryPage(); return; }
        if (_conflictTab == 4) { ShowOrganizationPage(); return; }
        if (_conflictTab == 5)
        {
            if (_campaign.Relations.Values.FirstOrDefault(r => PairKey(r) == _conflictPair) is { } rivalry) ShowTensionPage(rivalry);
            else ShowWorldOrderPage();
            return;
        }
        if (_conflictWar >= 0 && _campaign.Wars.FirstOrDefault(w => w.Id == _conflictWar && !w.Ended) is { } war)
        {
            ShowWarPage(war);
            return;
        }
        var relation = _campaign.Relations.Values.FirstOrDefault(r => PairKey(r) == _conflictPair);
        if (relation is not null)
        {
            ShowTensionPage(relation);
            return;
        }
        _conflictsPage.AddChild(Title("READ THE SITUATION", 22, Ui.Colour("text.accent")));
        _conflictsPage.AddChild(Title("Choose a conflict on the left to see its causes, recent events and available actions. Search works with country names, tags and war names.",
            14, Ui.Colour("text.body_soft")));
        _conflictsPage.AddChild(Caption("TENSION IS NOT WAR"));
        _conflictsPage.AddChild(Title("Friction can grow into a crisis or armed clashes. Hostility, lost land, rivalry and competing foreign backers all matter; deterrence and talks can hold it back.", 14, Ui.Colour("text.body")));
        _conflictsPage.AddChild(Caption("SUPPORT WITHOUT INTERVENTION"));
        _conflictsPage.AddChild(Title("A backer sends money, equipment or volunteers without becoming a belligerent. Open a war to review its sides, costs and support options.", 14, Ui.Colour("text.body")));
        _conflictsPage.AddChild(Caption("YOUR COUNTRY FIRST"));
        _conflictsPage.AddChild(Title("Our conflicts brings together your standoffs, the wars you fight, the sides you support and those who arm you.", 14, Ui.Colour("text.body")));
    }

    private void ShowTensionPage(DiplomaticRelation relation)
    {
        // Scenario wars can precede the first escalation review. Never call an active
        // war "calm" just because its stored peacetime tension has not ticked yet.
        var ongoing = _campaign.Wars.FirstOrDefault(w => !w.Ended &&
            (w.IsAttacking(relation.A) && w.IsDefending(relation.B) || w.IsAttacking(relation.B) && w.IsDefending(relation.A)));
        if (ongoing is not null) { ShowWarPage(ongoing); return; }
        var a = _campaign.NationById(relation.A);
        var b = _campaign.NationById(relation.B);
        if (a is null || b is null) return;
        var stage = Escalation.StageOf(relation);

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 10);
        head.AddChild(FlagBadge(a.Tag, 28));
        var name = Title($"{a.DisplayName} — {b.DisplayName}", 22, Ui.Colour("text.bright"));
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(name);
        head.AddChild(FlagBadge(b.Tag, 28));
        _conflictsPage.AddChild(head);

        var gaugeRow = new VBoxContainer();
        gaugeRow.AddThemeConstantOverride("separation", 5);
        gaugeRow.AddChild(Title(Simulation.Loc.Format("{0:0} / 100 · {1}", relation.Tension, StageName(stage)), 15, StageColour(stage)));
        var gauge = Gauge(relation.Tension, StageColour(stage), 0);
        gauge.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        gaugeRow.AddChild(gauge);
        _conflictsPage.AddChild(gaugeRow);

        _conflictsPage.AddChild(Caption("WHY"));
        _conflictsPage.AddChild(Title(TensionTip(relation), 14, Ui.Colour("text.body")));

        _conflictsPage.AddChild(Caption("WHAT THEY HOLD AGAINST EACH OTHER"));
        _conflictsPage.AddChild(Title(Simulation.Loc.Format("{0}: grievance {1:0}. {2}: grievance {3:0}. A grievance halves in about thirty years.",
            a.DisplayName, relation.GrievanceA, b.DisplayName, relation.GrievanceB), 14, Ui.Colour("text.body")));
        if (relation.ClashLosses > 0f)
            _conflictsPage.AddChild(Title(Simulation.Loc.Format("Lost to shooting short of war: {0:N0} people.", relation.ClashLosses), 14, Ui.Colour("text.body")));

        _conflictsPage.AddChild(Caption("LATELY"));
        // Retain the local branch's bounded reverse walk for long campaigns.
        var recent = new List<ChronicleEntry>();
        var chronicle = _campaign.Chronicle.Entries;
        for (var index = chronicle.Count - 1; index >= 0 && recent.Count < 8; index--)
        {
            var entry = chronicle[index];
            if (!entry.Public) continue;
            if (entry.ActorId == a.Id && entry.TargetId == b.Id || entry.ActorId == b.Id && entry.TargetId == a.Id)
                recent.Add(entry);
        }
        if (recent.Count == 0) _conflictsPage.AddChild(Title("Nothing has happened between them yet.", 13, Ui.Colour("text.caption")));
        var recentSequences = recent.Select(entry => entry.Sequence).ToHashSet();
        foreach (var line in NewsFeed.Lines(_campaign).Where(l => recentSequences.Contains(l.Sequence)))
            _conflictsPage.AddChild(Title(Simulation.Loc.Format("{0} days ago · {1}", _campaign.ElapsedDays - line.Day, line.Text), 13, Ui.Colour("text.body")));

        var player = _campaign.Player;
        if (player.Id == a.Id || player.Id == b.Id)
        {
            var other = player.Id == a.Id ? b : a;
            _conflictsPage.AddChild(Caption("WHAT YOU CAN DO"));
            var block = Escalation.TalksBlock(_campaign, player, other);
            var talks = ActionButton(Simulation.Loc.Format("Hold talks ({0})", Fmt.Money(_campaign.Defines.TalksCost)), () =>
            {
                Submit(new HoldTalksCommand { IssuerId = player.Id, TargetId = other.Id });
                _conflictsFeedback.Text = Tr("Talks opened. They take tension off every week while they last.");
                RefreshConflictsScreen(force: true);
            }, 0, 36, primary: true);
            talks.Disabled = block.Length > 0;
            talks.TooltipText = Ui.Tip(block.Length > 0 ? block : Tr("A round of talks for four months: tension falls every week while it lasts, and they will think a little better of you."));
            _conflictsPage.AddChild(talks);
            var diplomacy = ActionButton("Open diplomacy", () => { _selectedNation = other; CloseConflictsScreen(); OpenDiplomacyScreen(); }, 0, 34);
            _conflictsPage.AddChild(diplomacy);
        }
        else AddThirdPartyCrisisActions(a, b);
    }

    private void ShowWarPage(StrategicWar war)
    {
        var attacker = _campaign.NationById(war.AttackerId);
        var defender = _campaign.NationById(war.DefenderId);
        if (attacker is null || defender is null) return;
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 10);
        head.AddChild(FlagBadge(attacker.Tag, 28));
        var name = Title(WarNames.Of(_campaign, war), 22, Ui.Colour("text.bright"));
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(name);
        head.AddChild(FlagBadge(defender.Tag, 28));
        _conflictsPage.AddChild(head);

        var age = _campaign.ElapsedDays - war.StartedDay;
        var size = war.Intensity >= 2 ? Tr("A total war") : war.Intensity == 1 ? Tr("A major war") : Tr("A limited war");
        _conflictsPage.AddChild(Title(Simulation.Loc.Format("{0}, {1} days old. Score {2:+0;-0;0} for {3}. Losses {4:N0} and {5:N0}.",
            size, age, war.WarScore, attacker.DisplayName, war.AttackerLosses, war.DefenderLosses), 14, Ui.Colour("text.body")));
        if (war.CivilConflict)
            _conflictsPage.AddChild(Title("A civil war: it is locked against early talks and takes longer to be given up on.", 13, Ui.Colour("text.caption")));
        if (war.Limited)
        {
            var due = Title(Simulation.Loc.Format("Border war: the ceasefire on the old lines falls due in {0} days.",
                Math.Max(0, war.LimitedUntilDay - _campaign.ElapsedDays)), 13, Ui.Colour("state.warning"));
            due.TooltipText = Ui.Tip(Tr("A border war: fought by the two of them alone, with nobody's allies called, and ended by a ceasefire on the old lines when it falls due - unless somebody refuses it."));
            due.MouseFilter = MouseFilterEnum.Stop;
            _conflictsPage.AddChild(due);
        }

        foreach (var attackers in new[] { true, false })
        {
            var leader = attackers ? attacker : defender;
            _conflictsPage.AddChild(Caption(Simulation.Loc.Format("BACKING {0}", leader.DisplayName.ToUpperInvariant())));
            var backers = ProxyWars.Backing(_campaign, war, attackers).ToList();
            if (backers.Count == 0) _conflictsPage.AddChild(Title("Nobody.", 13, Ui.Colour("text.caption")));
            foreach (var s in backers)
            {
                var sponsor = _campaign.NationById(s.SponsorId);
                if (sponsor is null) continue;
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 8);
                row.AddChild(FlagBadge(sponsor.Tag, 16));
                var description = Title(Simulation.Loc.Format("{0}: level {1}, {2} a week, {3} sent so far", sponsor.DisplayName, s.Level,
                    Fmt.Money(s.LastWeekCost), Fmt.Money(s.Delivered)), 13, Ui.Colour("text.body"));
                description.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                row.AddChild(description);
                _conflictsPage.AddChild(row);
            }
        }

        var player = _campaign.Player;
        if (war.Involves(player.Id)) return;
        _conflictsPage.AddChild(Caption("BACK A SIDE"));
        _conflictsPage.AddChild(Title("Arm one side without going to war: a weekly cost, part of it money for them and the rest weapons for their divisions. The side you arm against will not forget it.",
            13, Ui.Colour("text.body_soft")));
        foreach (var attackers in new[] { true, false })
        {
            var client = attackers ? attacker : defender;
            var current = ProxyWars.Active(_campaign).FirstOrDefault(s => s.SponsorId == player.Id && s.WarId == war.Id && s.ClientId == client.Id);
            var row = new HFlowContainer();
            row.AddThemeConstantOverride("h_separation", 6);
            row.AddChild(Fixed(Title(client.DisplayName, 13, Ui.Colour("text.bright")), 180));
            for (var level = 1; level <= 3; level++)
            {
                var captured = level;
                var block = ProxyWars.Block(_campaign, player, war, client, level);
                var button = ActionButton(Simulation.Loc.Format("Level {0} · {1}/wk", level, Fmt.Money(ProxyWars.WeeklyCost(_campaign, player, level))),
                    () =>
                    {
                        Submit(new SponsorWarCommand { IssuerId = player.Id, WarId = war.Id, ClientId = client.Id, Level = captured });
                        _conflictsFeedback.Text = Simulation.Loc.Format("You now back {0} at level {1}.", client.DisplayName, captured);
                        RefreshConflictsScreen(force: true);
                    }, 0, 32, primary: current?.Level == level);
                button.Disabled = block.Length > 0 && current?.Level != level;
                button.TooltipText = Ui.Tip(block.Length > 0 ? block : captured switch
                {
                    1 => Tr("Advisers, money and spare parts."),
                    2 => Tr("Weapons and ammunition every week."),
                    _ => Tr("Weapons at scale and volunteers.")
                });
                row.AddChild(button);
            }
            if (current is not null)
            {
                var stop = ActionButton("Stop", () =>
                {
                    Submit(new SponsorWarCommand { IssuerId = player.Id, WarId = war.Id, ClientId = client.Id, Level = 0 });
                    _conflictsFeedback.Text = Simulation.Loc.Format("You no longer back {0}.", client.DisplayName);
                    RefreshConflictsScreen(force: true);
                }, 0, 32);
                row.AddChild(stop);
            }
            _conflictsPage.AddChild(row);
        }
    }
}
