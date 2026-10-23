using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private const int ConflictPageSize = 18;
    private ScrollContainer _conflictsListScroll, _conflictsPageScroll;
    private LineEdit _conflictSearch;
    private HBoxContainer _conflictPager;
    private Label _conflictPageReadout;
    private Label _conflictListHeading;
    private Button _conflictPrevious, _conflictNext;
    private int _conflictListPage, _conflictRenderedTab = -1;
    private readonly Dictionary<string, ConflictRow> _conflictRows = new();

    private sealed record ConflictEntry(string Key, string A, string B, string Names, string Search,
        string Detail, Color Tint, float Tension = -1, long Pair = -1, int War = -1);
    private sealed record ConflictRow(ConflictEntryButton Button, Label Names, Label Detail, Control Gauge, Control FlagA, Control FlagB);

    private void BuildConflictSearch(VBoxContainer deck)
    {
        _conflictSearch = Placeholder(new LineEdit { Name = "ConflictSearch", MaxLength = 100, ClearButtonEnabled = true },
            "Find a country, tag or war...");
        deck.AddChild(_conflictSearch);
        _conflictListHeading = Caption("RIVALRIES OF THE GREAT POWERS");
        deck.AddChild(_conflictListHeading);
        var debounce = new Timer { OneShot = true, WaitTime = .15 };
        deck.AddChild(debounce);
        _conflictSearch.TextChanged += _ => { _conflictListPage = 0; debounce.Start(); };
        debounce.Timeout += () =>
        {
            if (_conflictsLayer is not { Visible: true }) return;
            _conflictsListScroll.ScrollVertical = 0;
            RefreshConflictsScreen(true);
        };
    }

    private void BuildConflictPager(VBoxContainer deck)
    {
        _conflictPager = new HBoxContainer { Name = "ConflictPager" };
        _conflictPrevious = ActionButton("‹", () => TurnConflictPage(-1), 36, 32);
        _conflictPrevious.Name = "ConflictPrevious";
        _conflictPrevious.TooltipText = Ui.Tip(Tr("Previous page"));
        _conflictNext = ActionButton("›", () => TurnConflictPage(1), 36, 32);
        _conflictNext.Name = "ConflictNext";
        _conflictNext.TooltipText = Ui.Tip(Tr("Next page"));
        _conflictPageReadout = Title("", 12, Ui.Colour("text.caption"));
        _conflictPageReadout.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _conflictPageReadout.HorizontalAlignment = HorizontalAlignment.Center;
        _conflictPager.AddChild(_conflictPrevious);
        _conflictPager.AddChild(_conflictPageReadout);
        _conflictPager.AddChild(_conflictNext);
        deck.AddChild(_conflictPager);
    }

    private void TurnConflictPage(int delta)
    {
        _conflictListPage = Math.Max(0, _conflictListPage + delta);
        _conflictsListScroll.ScrollVertical = 0;
        RefreshConflictsScreen(true);
    }

    private void ChangeConflictTab(int tab)
    {
        if (_conflictTab == tab) return;
        _conflictTab = tab;
        _conflictPair = -1; _conflictWar = -1;
        _conflictListPage = 0;
        _conflictSearch.Text = "";
        _conflictsListScroll.ScrollVertical = 0;
        _conflictsPageScroll.ScrollVertical = 0;
        RefreshConflictsScreen(true);
    }

    private ConflictEntry ConflictEntryFor(DiplomaticRelation relation)
    {
        var a = _campaign.NationById(relation.A); var b = _campaign.NationById(relation.B);
        if (a is null || b is null) return null;
        var fighting = CampaignDiplomacy.AtWar(_campaign, relation.A, relation.B);
        var stage = fighting ? TensionStage.War : Escalation.StageOf(relation);
        var tension = fighting ? 100 : relation.Tension;
        var names = $"{a.DisplayName} — {b.DisplayName}";
        return new($"Pair_{PairKey(relation)}", a.Tag, b.Tag, names, $"{names} {a.Tag} {b.Tag}",
            Loc.Format("{0} · tension {1:0}/100", StageName(stage), tension), StageColour(stage), tension, PairKey(relation));
    }

    private ConflictEntry ConflictEntryFor(StrategicWar war, Dictionary<int, Sponsorship[]> sponsorships)
    {
        var a = _campaign.NationById(war.AttackerId); var b = _campaign.NationById(war.DefenderId);
        if (a is null || b is null) return null;
        var names = $"{a.DisplayName} — {b.DisplayName}";
        var tags = new List<string>();
        if (war.Involves(_campaign.Player.Id)) tags.Add(Tr("we are fighting"));
        if (war.CivilConflict) tags.Add(Tr("civil war"));
        if (war.Limited) tags.Add(Tr("border war"));
        tags.Add(Tr(war.Intensity >= 2 ? "total war" : war.Intensity == 1 ? "major war" : "limited war"));
        var backers = sponsorships.GetValueOrDefault(war.Id) ?? Array.Empty<Sponsorship>();
        if (backers.Any(s => s.SponsorId == _campaign.Player.Id)) tags.Add(Tr("we provide support"));
        if (backers.Any(s => s.ClientId == _campaign.Player.Id)) tags.Add(Tr("foreign support to us"));
        if (backers.Any(s => war.IsAttacking(s.ClientId)) && backers.Any(s => !war.IsAttacking(s.ClientId))) tags.Add(Tr("proxy confrontation"));
        else if (backers.Length > 0) tags.Add(Loc.Format("{0} backer(s)", backers.Length));
        return new($"War_{war.Id}", a.Tag, b.Tag, names, $"{names} {a.Tag} {b.Tag} {WarNames.Of(_campaign, war)}",
            string.Join(" · ", tags), Ui.Colour(war.Intensity >= 2 ? "state.critical" : "text.caption"), War: war.Id);
    }

    /// <summary>Only the visible page owns controls. Existing rows keep their identities,
    /// keyboard focus and scroll position when the clock advances. This is a UI projection,
    /// never an authoritative cache of war, aid or budget rules.</summary>
    private void RefreshConflictIndex(List<DiplomaticRelation> hot, List<StrategicWar> wars)
    {
        var sponsorships = ProxyWars.Active(_campaign).Where(s => s.WarId >= 0).GroupBy(s => s.WarId)
            .ToDictionary(g => g.Key, g => g.ToArray());
        var playerId = _campaign.Player.Id;
        var entries = new List<ConflictEntry>();
        if (_conflictTab != 1)
            foreach (var relation in hot)
                if (_conflictTab != 2 || relation.A == playerId || relation.B == playerId)
                    if (ConflictEntryFor(relation) is { } entry) entries.Add(entry);
        if (_conflictTab != 0)
            foreach (var war in wars.OrderByDescending(w => w.Involves(playerId))
                         .ThenByDescending(w => sponsorships.GetValueOrDefault(w.Id)?.Length ?? 0)
                         .ThenByDescending(w => w.AttackerLosses + w.DefenderLosses).ThenBy(w => w.Id))
                if (_conflictTab != 2 || war.Involves(playerId) ||
                    sponsorships.TryGetValue(war.Id, out var aid) && aid.Any(s => s.SponsorId == playerId || s.ClientId == playerId))
                    if (ConflictEntryFor(war, sponsorships) is { } entry) entries.Add(entry);
        var query = _conflictSearch.Text.Trim();
        var found = entries.Where(e => query.Length == 0 || e.Search.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            e.Detail.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        var pageCount = Math.Max(1, (found.Count + ConflictPageSize - 1) / ConflictPageSize);
        _conflictListPage = Math.Clamp(_conflictListPage, 0, pageCount - 1);
        var visible = found.Skip(_conflictListPage * ConflictPageSize).Take(ConflictPageSize).ToList();
        RenderConflictEntries(visible, query.Length > 0 ? "No matching conflicts. Try a country name or tag." :
            _conflictTab == 0 ? "The world is calm." : _conflictTab == 1 ? "Nobody is at war." : "You have no active standoffs, wars or war support.");
        _conflictPrevious.Disabled = _conflictListPage == 0;
        _conflictNext.Disabled = _conflictListPage + 1 >= pageCount;
        _conflictPageReadout.Text = Loc.Format("{0} results · page {1}/{2}", found.Count, _conflictListPage + 1, pageCount);
    }

    private void RenderConflictEntries(IReadOnlyList<ConflictEntry> visible, string emptyMessage)
    {
        if (_conflictRenderedTab != _conflictTab || _conflictRows.Count == 0)
        {
            _organizationIndexReady = false;
            _conflictRows.Clear(); ClearRuntimeChildren(_conflictsList);
        }
        _conflictRenderedTab = _conflictTab;
        var wanted = visible.Select(e => e.Key).ToHashSet();
        foreach (var stale in _conflictRows.Keys.Where(k => !wanted.Contains(k)).ToArray())
        {
            var row = _conflictRows[stale]; _conflictsList.RemoveChild(row.Button); row.Button.QueueFree();
            _conflictRows.Remove(stale);
        }
        for (var index = 0; index < visible.Count; index++)
        {
            var entry = visible[index];
            AddConflictEntry(entry);
            _conflictsList.MoveChild(_conflictRows[entry.Key].Button, index);
        }
        if (visible.Count == 0)
        {
            ClearRuntimeChildren(_conflictsList);
            _conflictsList.AddChild(Title(emptyMessage, 14, Ui.Colour("text.caption")));
        }
    }

    private void AddConflictEntry(ConflictEntry entry)
    {
        var selected = entry.War >= 0 ? entry.War == _conflictWar : entry.Pair == _conflictPair && _conflictWar < 0;
        if (!_conflictRows.TryGetValue(entry.Key, out var row))
        {
            var button = (ConflictEntryButton)ConflictRowButton(selected, entry.War >= 0 ? 94 : 78);
            button.Name = entry.Key;
            var content = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            content.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            content.OffsetLeft = 10; content.OffsetRight = -10; content.OffsetTop = 6; content.OffsetBottom = -6;
            content.AddThemeConstantOverride("separation", 4); button.AddChild(content);
            var head = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            head.AddThemeConstantOverride("separation", 7); content.AddChild(head);
            var flagA = FlagBadge(entry.A, 18);
            var flagB = FlagBadge(entry.B, 18);
            head.AddChild(flagA);
            var names = Fixed(Title(entry.Names, 14, Ui.Colour("text.body")));
            names.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            names.ClipText = true; names.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            head.AddChild(names); head.AddChild(flagB);
            var detail = Title("", 12, entry.Tint); detail.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            content.AddChild(detail);
            var gauge = Gauge(0, entry.Tint, 0); gauge.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            content.AddChild(gauge);
            var pair = entry.Pair; var war = entry.War;
            button.Pressed += () =>
            {
                _conflictPair = pair; _conflictWar = war;
                _conflictsPageScroll.ScrollVertical = 0;
                RefreshConflictsScreen(true);
            };
            button.TooltipText = entry.Names;
            button.Explain = () => pair >= 0 && _campaign.Relations.Values.FirstOrDefault(r => PairKey(r) == pair) is { } relation
                ? $"{_campaign.NationById(relation.A)?.DisplayName} — {_campaign.NationById(relation.B)?.DisplayName}\n{TensionTip(relation)}" :
                _campaign.Wars.FirstOrDefault(w => w.Id == war) is { } conflict ? WarNames.Of(_campaign, conflict) : "";
            row = new(button, names, detail, gauge, flagA, flagB); _conflictRows.Add(entry.Key, row);
            _conflictsList.AddChild(button);
        }
        row.Names.Text = entry.Names; row.Detail.Text = entry.Detail; row.Button.TooltipText = entry.Names;
        SetFlag(row.FlagA, entry.A, 18); SetFlag(row.FlagB, entry.B, 18);
        row.Detail.AddThemeColorOverride("font_color", entry.Tint);
        row.Gauge.Visible = entry.Tension >= 0;
        var fill = (ColorRect)row.Gauge.GetChild(1);
        fill.AnchorRight = Math.Clamp(entry.Tension / 100f, 0, 1); fill.Color = entry.Tint;
        row.Button.AddThemeStyleboxOverride("normal", ShellRowStyle(selected));
        row.Button.AddThemeStyleboxOverride("focus", ShellRowStyle(selected));
    }
}
