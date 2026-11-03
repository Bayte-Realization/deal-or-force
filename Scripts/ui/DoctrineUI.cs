using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    // A paused order, host reply, country switch or save reload need not change the day.
    // Compare the actual inputs used by both doctrine hosts, without fingerprinting the world.
    private readonly record struct DoctrinePresentationState(
        Nation Nation, string Language, string Book, string View,
        string Branch, string Known, string LearningBranch, string LearningNode, int DoneDay,
        string Strategy, float Army, float Air, float Navy, float Political, float RewriteCost,
        int TacticalRevision, int StrategicRevision, bool TacticalEnabled, bool StrategicEnabled);

    private DoctrinePresentationState DoctrinePresentation(Nation n)
    {
        TacticalDoctrines.EnsureShelves(n);
        var books = string.Join(';', DoctrineSlots.All.Select(slot =>
        {
            var track = n.TacticalDoctrine.Tracks.GetValueOrDefault(slot);
            return track is null
                ? slot + "="
                : slot + "=" + track.Branch + "/" + string.Join(',', track.Known) + "/" +
                  track.LearningNode + "/" + track.LearningDoneDay;
        }));
        return new DoctrinePresentationState(
            n, Loc.Language, _doctrineBook, _doctrineView,
            books, "", "", "", -1,
            n.StrategicDoctrine.Id, n.ArmyExperience, n.AirExperience, n.NavalExperience, n.PoliticalPoints,
            _campaign.Defines.DoctrineRewritePoliticalCost, TacticalDoctrineCatalog.Revision,
            StrategicDoctrineCatalog.Revision, _campaign.FeatureEnabled("tactical_doctrines"),
            _campaign.FeatureEnabled("strategic_doctrines"));
    }

    /// <summary>
    /// The page behind the Doctrines tab (Laws workspace): the country's book of war, the grand
    /// strategy above it, and the four currencies both are paid in.
    ///
    /// Every button reads the same refusal the simulation will answer with - TacticalDoctrines
    /// .Refusal and StrategicDoctrines.Refusal are the single door the command, the AI and this
    /// screen all come through - so a disabled button here says what would happen if it were
    /// pressed, and never disagrees with what pressing it does.
    /// </summary>
    private void ShowDoctrineWorkspace(Nation n, VBoxContainer rows = null)
    {
        rows ??= _policyOptions;
        _policyConfirmation = "";
        if (_campaign is null || n is null) return;
        if (!_campaign.FeatureEnabled("tactical_doctrines") && !_campaign.FeatureEnabled("strategic_doctrines"))
        {
            PolicyHeading(rows, "DOCTRINES");
            PolicyText(rows, Loc.Get("This mechanic is disabled in this scenario."));
            return;
        }
        CurrencyStrip(rows, n, false);
        if (_campaign.FeatureEnabled("tactical_doctrines")) TacticalDoctrinePage(n, rows);
        if (_campaign.FeatureEnabled("strategic_doctrines")) StrategicDoctrinePage(n, rows);
    }

    /// <summary>
    /// The four currencies, each with the number a player actually wants beside it: what the next
    /// article costs, how many of his own are earning for the other two, and what a week of
    /// government adds. The same helpers the top bar uses, so the two cannot drift apart.
    /// </summary>
    private void CurrencyStrip(VBoxContainer rows, Nation n, bool withPolitical = true, int columns = 0)
    {
        var strip = new PanelContainer { Name = "DoctrineCurrencies" };
        strip.AddThemeStyleboxOverride("panel", WarInsetStyle(12, 8));
        rows.AddChild(strip);
        Container line;
        if (columns > 0)
        {
            var grid = new GridContainer { Columns = columns };
            grid.AddThemeConstantOverride("h_separation", 12);
            grid.AddThemeConstantOverride("v_separation", 8);
            line = grid;
        }
        else
        {
            var hbox = new HBoxContainer();
            hbox.AddThemeConstantOverride("separation", 10);
            line = hbox;
        }
        strip.AddChild(line);
        void Currency(string icon, string caption, float value, string note)
        {
            var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            var head = new HBoxContainer();
            head.AddThemeConstantOverride("separation", 5);
            head.AddChild(new TextureRect
            {
                Texture = Art("icon_" + icon, "res://game/gfx/interface/icons/icon_" + icon + ".png"),
                CustomMinimumSize = new Vector2(16, 16),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                Modulate = ArtColour("icon_" + icon, "text.accent")
            });
            var headLabel = Title(Tr(caption), 11, Ui.Colour("text.caption"));
            headLabel.AutowrapMode = TextServer.AutowrapMode.Off;
            head.AddChild(headLabel);
            box.AddChild(head);
            var valueLabel = Title(Fmt.Number(value), 16, Ui.Colour("text.bright"));
            valueLabel.AutowrapMode = TextServer.AutowrapMode.Off;
            valueLabel.AddThemeFontOverride("font", PresentationFonts.Tabular());
            box.AddChild(valueLabel);
            var noteLabel = Title(note, 11, Ui.Colour("text.body_soft"));
            noteLabel.AutowrapMode = TextServer.AutowrapMode.Off;
            box.AddChild(noteLabel);
            line.AddChild(box);
        }

        var next = NextArticlePrice(n, DoctrineSlots.Ground);
        Currency("military", "ARMY XP", n.ArmyExperience,
            next > 0f ? Loc.Format("next article {0}", Fmt.Number(next)) : Loc.Get("no article open"));
        Currency("air", "AIR XP", n.AirExperience, Loc.Format("{0} flying", WingsFlying(n.Id)));
        Currency("navy", "NAVY XP", n.NavalExperience, Loc.Format("{0} at sea", ShipsAtSea(n.Id)));
        if (!withPolitical) return;
        var weekly = _campaign.Defines.PoliticalPointsPerWeek +
            _campaign.Defines.PoliticalPointsStabilityWeight * n.Stability;
        // Political credit is also the general staff's purse: the one shelf that is not bought
        // with a service's experience.
        var generalNext = NextArticlePrice(n, DoctrineSlots.General);
        Currency("agenda", "POLITICAL CAPITAL", n.PoliticalPoints,
            generalNext > 0f
                ? Loc.Format("{0}/week · next staff article {1}", Fmt.Number(weekly), Fmt.Number(generalNext))
                : Loc.Format("{0}/week", Fmt.Number(weekly)));
    }

    // The tactical book is drawn as what it is - a tree of articles - in DoctrineTreeUI, where
    // the canvas, the book tabs and the pan/zoom controls live. This file keeps the currencies
    // above it and the course below it.

    // One article is a card on the canvas now: see DoctrineTreeUI.DoctrineNodeCard.


    /// <summary>
    /// The seven courses, and the one in force. A course has no tree to study: it is a choice
    /// about how the country means to win, taken once, changed for political credit in peace.
    /// </summary>
    private void StrategicDoctrinePage(Nation n, VBoxContainer rows = null)
    {
        rows ??= _policyOptions;
        PolicyHeading(rows, "STRATEGIC DOCTRINE");
        PolicyText(rows, Loc.Get(
            "One course per country: how the state means to win, not how its divisions fight. The first course is free. Changing it costs political credit, and a government does not change course in the middle of a war."));
        foreach (var course in StrategicDoctrineCatalog.All)
        {
            var card = new PanelContainer { Name = "Strategy_" + course.Id };
            card.AddThemeStyleboxOverride("panel", WarInsetStyle(12, 10));
            AtlasSurfaceSkin.Attach(card, AtlasSurfaceSkin.Level.Inset, "surface.inset");
            rows.AddChild(card);
            var body = new VBoxContainer();
            body.AddThemeConstantOverride("separation", 5);
            card.AddChild(body);

            var inForce = string.Equals(n.StrategicDoctrine.Id, course.Id, StringComparison.Ordinal);
            body.AddChild(Title(Tr(course.Name), 15, Ui.Colour(inForce ? "text.accent" : "text.bright")));
            body.AddChild(Title(Tr(course.Description), 12, Ui.Colour("text.body_soft")));
            var effects = NationalModifiers.Block(course.Modifiers);
            if (!string.IsNullOrEmpty(effects)) body.AddChild(Title(effects, 12, Ui.Colour("text.body")));

            var learning = Loc.Format("Drill ×{0}, the line ×{1}, the air ×{2}, the sea ×{3}",
                Fmt.Number(course.DrillExperience), Fmt.Number(course.BattleExperience),
                Fmt.Number(course.AirExperience), Fmt.Number(course.NavalExperience));
            if (Math.Abs(course.TrainingSlots - 1f) > 0.001f)
                learning += " · " + Loc.Format("{0} formations on the drill square", Fmt.Number(course.TrainingSlots));
            if (course.TacticalBias.Length > 0 && TacticalDoctrineCatalog.Find(course.TacticalBias) is { } leaning)
                learning += " · " + Loc.Format("leans on {0}", Tr(leaning.Name));
            body.AddChild(Title(learning, 12, Ui.Colour("text.caption")));

            var refusal = StrategicDoctrines.Refusal(_campaign, n, course.Id);
            var button = ActionButton(
                inForce ? Loc.Get("in force")
                    : n.StrategicDoctrine.Id.Length == 0 ? Loc.Get("take this course")
                    : Loc.Format("change course ({0})", Fmt.Number(course.PoliticalCost)),
                () =>
                {
                    if (_campaign?.Player is not { } player) return;
                    ApplyDoctrineCommand(new AdoptStrategicDoctrineCommand
                        { IssuerId = player.Id, DoctrineId = course.Id });
                }, 0, 30, false, "res://game/gfx/interface/icons/icon_agenda.png");
            button.Name = "AdoptStrategy_" + course.Id;
            button.Disabled = inForce || refusal.Length > 0;
            button.TooltipText = Ui.Tip(refusal.Length > 0 ? refusal : Tr(course.Description));
            body.AddChild(button);
        }
    }

    /// <summary>Like every other immediate campaign action, commit through the authoritative
    /// queue before reading its result back. Enqueue alone waits for the next simulation
    /// step, which leaves the book and its prices unchanged for as long as the game is paused.
    /// The queue retains transport interception and finishes an existing staged boundary;
    /// this does not tick the clock or fast-forward a patiently studied article.</summary>
    private void ApplyDoctrineCommand(ICampaignCommand command)
    {
        if (_campaign?.Player is null || command is null) return;
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        _doctrineScreenDay = -1;
        _armyDoctrineDay = -1;
        PlayStrategicCue("ui_select");
        RedrawDoctrinePage();
        Refresh();
    }

    /// <summary>
    /// The right-hand column: what the army is reading, what the state intends, and what buys it.
    /// The figures are live - the same helpers the top bar uses - so this is a page to plan from
    /// rather than a description of a system.
    /// </summary>
    private void ShowDoctrinePreview(Nation n, VBoxContainer host = null)
    {
        var rows = host ?? _policyPreview;
        PolicyHeading(rows, "WHAT THE ARMY IS READING");
        TacticalDoctrines.EnsureShelves(n);
        foreach (var slot in DoctrineSlots.All)
        {
            var track = n.TacticalDoctrine.Track(slot);
            var book = TacticalDoctrines.BookOn(n, slot);
            var shelf = Title(Tr(DoctrineSlots.Name(slot)).ToUpperInvariant(), 12, Ui.Colour("text.caption"));
            rows.AddChild(shelf);
            if (book is null)
            {
                PolicyText(rows, Loc.Get("Shelf empty. The first article of any book costs nothing political; abandoning one later does."));
                continue;
            }
            PolicyText(rows, Loc.Format("{0} · {1}/{2}", Tr(book.Name), track.Known.Count, book.Nodes.Count),
                "text.accent");
            var written = 0;
            foreach (var node in book.Nodes)
            {
                if (!track.Known.Contains(node.Id)) continue;
                written++;
                var effects = NationalModifiers.Block(node.Modifiers);
                PolicyText(rows, "· " + Tr(node.Name) + (string.IsNullOrEmpty(effects) ? "" : " — " + effects));
            }
            if (written == 0) PolicyText(rows, Loc.Get("Nothing of it is drilled in yet."));
            var companion = track.Companion;
            var second = companion is { Branch.Length: > 0 } ? TacticalDoctrineCatalog.Find(companion.Branch) : null;
            if (second is null) continue;
            PolicyText(rows, Loc.Format("{0} · {1}/{2}", Tr(second.Name), companion.Known.Count, second.Nodes.Count),
                "text.accent");
            var drilled = 0;
            foreach (var node in second.Nodes)
            {
                if (!companion.Known.Contains(node.Id)) continue;
                drilled++;
                var effects = NationalModifiers.Block(node.Modifiers);
                PolicyText(rows, "· " + Tr(node.Name) + (string.IsNullOrEmpty(effects) ? "" : " — " + effects));
            }
            if (drilled == 0) PolicyText(rows, Loc.Get("Nothing of it is drilled in yet."));
        }
        // A country's very first article is already being studied before it has a book in
        // force. Its progress belongs here too, on every shelf that is studying something.
        foreach (var slot in DoctrineSlots.All)
        {
            var track = n.TacticalDoctrine.Track(slot);
            if (track.Companion is { LearningNode.Length: > 0 } extra)
            {
                var other = TacticalDoctrineCatalog.Find(extra.LearningBranch)?.Find(extra.LearningNode);
                PolicyText(rows, Loc.Format("{0}: {1} days left.",
                    other is null ? extra.LearningNode : Tr(other.Name),
                    Math.Max(0, extra.LearningDoneDay - _campaign.ElapsedDays)));
            }
            if (track.LearningNode.Length == 0) continue;
            var studying = TacticalDoctrineCatalog.Find(track.LearningBranch)?.Find(track.LearningNode);
            PolicyText(rows, Loc.Format("{0}: {1} days left.",
                Tr(studying?.Name ?? track.LearningNode),
                Math.Max(0, track.LearningDoneDay - _campaign.ElapsedDays)), "text.accent");
        }

        PolicyHeading(rows, "WHAT THE STATE INTENDS");
        var course = StrategicDoctrines.Of(n);
        PolicyText(rows, course is null
            ? Loc.Get("No course chosen: the country fights every war the way it always has.")
            : Tr(course.Name));
        if (course is not null) PolicyText(rows, Tr(course.Description));

        PolicyHeading(rows, "WHAT BUYS IT");
        PolicyText(rows, Loc.Format("{0} divisions in the line, {1} on the drill square.",
            DivisionsEngaged(n.Id), DivisionsDrilling(n.Id)));
        PolicyText(rows, Loc.Format("{0} wings flying, {1} hulls at sea.",
            WingsFlying(n.Id), ShipsAtSea(n.Id)));
        var next = NextArticlePrice(n);
        PolicyText(rows, next > 0f
            ? Loc.Format("Next article open: {0}.", Fmt.Number(next))
            : Loc.Get("Every article open to this book is already written."));
        var weekly = _campaign.Defines.PoliticalPointsPerWeek +
            _campaign.Defines.PoliticalPointsStabilityWeight * n.Stability;
        PolicyText(rows, Loc.Format("Political credit: {0} a week at this stability.", Fmt.Number(weekly)));
        if (course is { TacticalBias.Length: > 0 } && TacticalDoctrineCatalog.Find(course.TacticalBias) is { } leaning)
            PolicyText(rows, Loc.Format("The course in force leans on {0}.", Tr(leaning.Name)));
    }

    /// <summary>Divisions in the line and on the drill square - the same rules MilitaryExperience uses.</summary>
    private int DivisionsEngaged(int nationId) =>
        _campaign.Armies.Count(army => army.NationId == nationId && army.InBattle);

    private int DivisionsDrilling(int nationId) =>
        _campaign.Armies.Count(army => army.NationId == nationId && army.Training && !army.InBattle);
}
