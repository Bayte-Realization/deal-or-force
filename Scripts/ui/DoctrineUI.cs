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
    /// Executive Doctrine Summary for the narrow Army/Laws Doctrines tab:
    /// 4-currency strip, prominent Doctrine Hub CTA, Active Grand Strategy card,
    /// 4 clickable Service Shelf summary cards, and a compact Strategic Course switcher.
    /// </summary>
    private void ShowDoctrineWorkspace(Nation n, VBoxContainer rows = null)
    {
        rows ??= _policyOptions;
        _policyConfirmation = "";
        if (_campaign is null || n is null) return;
        var hasTactical = _campaign.FeatureEnabled("tactical_doctrines");
        var hasStrategic = _campaign.FeatureEnabled("strategic_doctrines");
        if (!hasTactical && !hasStrategic)
        {
            PolicyHeading(rows, "DOCTRINES");
            PolicyText(rows, Loc.Get("This mechanic is disabled in this scenario."));
            return;
        }

        CurrencyStrip(rows, n, withPolitical: true, columns: 2);

        var openHub = ActionButton(Loc.Get("doctrine.ui.open_hub"), OpenDoctrineScreen, 0, 36, true,
            "res://game/gfx/interface/icons/icon_laws.png");
        openHub.Name = "OpenDoctrineScreen";
        openHub.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        openHub.TooltipText = Ui.Tip(TreeViewport.Hint);
        rows.AddChild(openHub);

        if (hasStrategic) BuildGrandStrategySummaryCard(n, rows);
        if (hasTactical) BuildServiceShelvesSummary(n, rows);
        if (hasStrategic) BuildCompactStrategicCourseSwitcher(n, rows);
    }

    private void BuildGrandStrategySummaryCard(Nation n, VBoxContainer rows)
    {
        var course = StrategicDoctrines.Of(n);
        var card = new PanelContainer { Name = "DoctrineGrandStrategySummary", ClipContents = true };
        card.AddThemeStyleboxOverride("panel", Ui.ChromeBox(
            "surface.inset", 1f,
            course is not null ? "text.accent" : "surface.line_faint",
            course is not null ? 0.88f : 0.75f,
            cornerRadius: 4, borderWidth: 1, padX: 10, padY: 8,
            leftBorderWidth: course is not null ? 3 : 1));
        rows.AddChild(card);

        var col = new VBoxContainer { ClipContents = true };
        col.AddThemeConstantOverride("separation", 4);
        card.AddChild(col);

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 6);
        col.AddChild(head);

        var hdr = Title(Loc.Get("doctrine.ui.grand_strategy_hdr"), 11, Ui.Colour("text.caption"));
        hdr.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(hdr);

        var changeBtn = ActionButton(Loc.Get("doctrine.ui.change_course_btn"), () => OpenDoctrineScreenOnView("course"),
            0, 26, false, "res://game/gfx/interface/icons/icon_agenda.png");
        changeBtn.Name = "DoctrineChangeCourseQuick";
        head.AddChild(changeBtn);

        if (course is null)
        {
            col.AddChild(Title(Loc.Get("doctrine.ui.no_course_short"), 13, Ui.Colour("text.body_soft")));
            return;
        }

        var nameLbl = Title(Tr(course.Name), 14, Ui.Colour("text.accent"));
        col.AddChild(nameLbl);

        var effects = NationalModifiers.Block(course.Modifiers);
        if (!string.IsNullOrEmpty(effects))
        {
            var effLbl = Title(effects, 11, Ui.Colour("text.body"));
            effLbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            col.AddChild(effLbl);
        }

        var learning = FormatCourseLearningLine(course);
        var learnLbl = Title(learning, 11, Ui.Colour("text.caption"));
        learnLbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        learnLbl.AddThemeFontOverride("font", PresentationFonts.Tabular());
        col.AddChild(learnLbl);
    }

    private void BuildServiceShelvesSummary(Nation n, VBoxContainer rows)
    {
        TacticalDoctrines.EnsureShelves(n);
        SectionRule(rows, Loc.Get("doctrine.ui.service_shelves_hdr"), Loc.Format("{0} / {1} in force",
            TacticalDoctrines.Books(n).Count(), TacticalDoctrineCatalog.All.Count()));

        var grid = new GridContainer { Name = "DoctrineServiceShelvesGrid", Columns = 2, ClipContents = true };
        grid.AddThemeConstantOverride("h_separation", 8);
        grid.AddThemeConstantOverride("v_separation", 8);
        rows.AddChild(grid);

        foreach (var slot in DoctrineSlots.All)
        {
            var capturedSlot = slot;
            var track = n.TacticalDoctrine.Track(slot);
            var book = TacticalDoctrines.BookOn(n, slot);
            var studying = ResolveShelfStudyInfo(track);

            var card = new PanelContainer
            {
                Name = "DoctrineShelfCard_" + slot,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                ClipContents = true
            };
            var borderTok = studying.HasValue ? "text.accent" : book is not null ? "state.positive" : "surface.line_faint";
            card.AddThemeStyleboxOverride("panel", Ui.ChromeBox(
                "surface.inset", 0.96f, borderTok, book is not null || studying.HasValue ? 0.80f : 0.65f,
                cornerRadius: 3, borderWidth: 1, padX: 8, padY: 7,
                leftBorderWidth: book is not null || studying.HasValue ? 3 : 1));
            grid.AddChild(card);

            var col = new VBoxContainer { ClipContents = true };
            col.AddThemeConstantOverride("separation", 3);
            card.AddChild(col);

            var head = new HBoxContainer();
            head.AddThemeConstantOverride("separation", 5);
            col.AddChild(head);

            head.AddChild(new TextureRect
            {
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                Texture = Art("icon_" + DoctrineServiceIcon(slot), "res://game/gfx/interface/icons/icon_" + DoctrineServiceIcon(slot) + ".png"),
                CustomMinimumSize = new Vector2(14, 14),
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
                Modulate = ArtColour("icon_" + DoctrineServiceIcon(slot), "text.accent")
            });

            var slotLbl = Title(Tr(DoctrineSlots.Name(slot)).ToUpperInvariant(), 11, Ui.Colour("text.caption"));
            slotLbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            slotLbl.ClipText = true;
            slotLbl.AutowrapMode = TextServer.AutowrapMode.Off;
            slotLbl.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            head.AddChild(slotLbl);

            if (book is not null)
            {
                var prog = Title(Loc.Format("doctrine.ui.shelf_progress_fmt", track.Known.Count, book.Nodes.Count), 11, Ui.Colour("state.positive"));
                prog.AddThemeFontOverride("font", PresentationFonts.Tabular());
                prog.AutowrapMode = TextServer.AutowrapMode.Off;
                head.AddChild(prog);
            }

            var bookLbl = Title(book is not null ? Tr(book.Name) : Loc.Get("doctrine.ui.shelf_empty_short"),
                12, Ui.Colour(book is not null ? "text.bright" : "text.body_soft"));
            bookLbl.ClipText = true;
            bookLbl.AutowrapMode = TextServer.AutowrapMode.Off;
            bookLbl.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            col.AddChild(bookLbl);

            var comp = track.Companion;
            if (comp is { Branch.Length: > 0 } && TacticalDoctrineCatalog.Find(comp.Branch) is { } secondBook)
            {
                var compLbl = Title(Loc.Format("doctrine.ui.shelf_companion_fmt", Tr(secondBook.Name), comp.Known.Count, secondBook.Nodes.Count),
                    11, Ui.Colour("state.info"));
                compLbl.AddThemeFontOverride("font", PresentationFonts.Tabular());
                compLbl.ClipText = true;
                compLbl.AutowrapMode = TextServer.AutowrapMode.Off;
                compLbl.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
                col.AddChild(compLbl);
            }

            if (studying is { } activeStudy)
            {
                var studyLbl = Title(Loc.Format("doctrine.ui.shelf_studying_fmt", activeStudy.ArticleName, activeStudy.DaysLeft),
                    11, Ui.Colour("text.accent"));
                studyLbl.AddThemeFontOverride("font", PresentationFonts.Tabular());
                studyLbl.ClipText = true;
                studyLbl.AutowrapMode = TextServer.AutowrapMode.Off;
                studyLbl.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
                col.AddChild(studyLbl);
            }

            var inspect = ActionButton(Loc.Get("doctrine.ui.inspect_shelf_btn"), () => OpenDoctrineScreenOnService(capturedSlot), 0, 26);
            inspect.Name = "InspectShelf_" + slot;
            inspect.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            col.AddChild(inspect);
        }
    }

    private (string ArticleName, int DaysLeft)? ResolveShelfStudyInfo(DoctrineTrack track)
    {
        if (track is null) return null;
        if (track.LearningNode.Length > 0)
        {
            var node = TacticalDoctrineCatalog.Find(track.LearningBranch)?.Find(track.LearningNode);
            return (Tr(node?.Name ?? track.LearningNode), Math.Max(0, track.LearningDoneDay - _campaign.ElapsedDays));
        }
        if (track.Companion is { LearningNode.Length: > 0 } extra)
        {
            var node = TacticalDoctrineCatalog.Find(extra.LearningBranch)?.Find(extra.LearningNode);
            return (Tr(node?.Name ?? extra.LearningNode), Math.Max(0, extra.LearningDoneDay - _campaign.ElapsedDays));
        }
        return null;
    }

    private void BuildCompactStrategicCourseSwitcher(Nation n, VBoxContainer rows)
    {
        SectionRule(rows, Loc.Get("doctrine.ui.course_switcher_hdr"), StrategicDoctrines.Of(n) is { } cur ? Tr(cur.Name) : "");
        foreach (var course in StrategicDoctrineCatalog.All)
        {
            var inForce = string.Equals(n.StrategicDoctrine.Id, course.Id, StringComparison.Ordinal);
            var refusal = StrategicDoctrines.Refusal(_campaign, n, course.Id);
            var card = new PanelContainer { Name = "Strategy_" + course.Id, ClipContents = true };
            card.AddThemeStyleboxOverride("panel", Ui.ChromeBox(
                "surface.inset", inForce ? 1f : 0.90f,
                inForce ? "text.accent" : "surface.line_faint",
                inForce ? 0.90f : 0.65f,
                cornerRadius: 3, borderWidth: 1, padX: 8, padY: 6,
                leftBorderWidth: inForce ? 3 : 1));
            rows.AddChild(card);

            var row = new HBoxContainer { ClipContents = true };
            row.AddThemeConstantOverride("separation", 8);
            card.AddChild(row);

            var info = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
            info.AddThemeConstantOverride("separation", 2);
            row.AddChild(info);

            var title = Title(Tr(course.Name), 13, Ui.Colour(inForce ? "text.accent" : "text.bright"));
            title.ClipText = true;
            title.AutowrapMode = TextServer.AutowrapMode.Off;
            title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            info.AddChild(title);

            var effects = NationalModifiers.Block(course.Modifiers);
            if (!string.IsNullOrEmpty(effects))
            {
                var eff = Title(effects, 11, Ui.Colour("text.body_soft"));
                eff.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                info.AddChild(eff);
            }

            var btn = CreateAdoptStrategyButton(n, course, inForce, refusal, compact: true);
            btn.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            row.AddChild(btn);
        }
    }

    /// <summary>
    /// The four currencies, each with the number a player actually wants beside it: what the next
    /// article costs, how many of his own are earning for the other two, and what a week of
    /// government adds. The same helpers the top bar uses, so the two cannot drift apart.
    /// </summary>
    private void CurrencyStrip(VBoxContainer rows, Nation n, bool withPolitical = true, int columns = 0)
    {
        var strip = new PanelContainer { Name = "DoctrineCurrencies", ClipContents = true };
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
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                Texture = Art("icon_" + icon, "res://game/gfx/interface/icons/icon_" + icon + ".png"),
                CustomMinimumSize = new Vector2(16, 16),
                Modulate = ArtColour("icon_" + icon, "text.accent")
            });
            var headLabel = Title(Tr(caption), 11, Ui.Colour("text.caption"));
            headLabel.Name = "DoctrineCurrencyHead_" + icon;
            headLabel.AutowrapMode = TextServer.AutowrapMode.Off;
            head.AddChild(headLabel);
            box.AddChild(head);
            var valueLabel = Title(Fmt.Number(value), 16, Ui.Colour("text.bright"));
            valueLabel.AutowrapMode = TextServer.AutowrapMode.Off;
            valueLabel.AddThemeFontOverride("font", PresentationFonts.Tabular());
            box.AddChild(valueLabel);
            var noteLabel = Title(note, 11, Ui.Colour("text.body_soft"));
            noteLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            noteLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
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
        var generalNext = NextArticlePrice(n, DoctrineSlots.General);
        Currency("agenda", "POLITICAL CAPITAL", n.PoliticalPoints,
            generalNext > 0f
                ? Loc.Format("{0}/week · next staff article {1}", Fmt.Number(weekly), Fmt.Number(generalNext))
                : Loc.Format("{0}/week", Fmt.Number(weekly)));
    }

    /// <summary>
    /// Full Strategic Courses showcase: responsive 2-column Sovereign Brass card grid in the
    /// wide Doctrine Hub ("course" tab), or 1-column fallback on compact viewports.
    /// </summary>
    private void StrategicDoctrinePage(Nation n, VBoxContainer rows = null, bool wideGrid = false)
    {
        rows ??= _policyOptions;
        PolicyHeading(rows, "STRATEGIC DOCTRINE");
        PolicyText(rows, Loc.Get(
            "One course per country: how the state means to win, not how its divisions fight. The first course is free. Changing it costs political credit, and a government does not change course in the middle of a war."));

        var compactVp = GetViewport().GetVisibleRect().Size.X < 1180f;
        Container host;
        if (wideGrid && !compactVp)
        {
            var grid = new GridContainer { Name = "StrategicDoctrineGrid", Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            grid.AddThemeConstantOverride("h_separation", 12);
            grid.AddThemeConstantOverride("v_separation", 10);
            rows.AddChild(grid);
            host = grid;
        }
        else
        {
            var stack = new VBoxContainer { Name = "StrategicDoctrineGrid", SizeFlagsHorizontal = SizeFlags.ExpandFill };
            stack.AddThemeConstantOverride("separation", 8);
            rows.AddChild(stack);
            host = stack;
        }

        foreach (var course in StrategicDoctrineCatalog.All)
        {
            var inForce = string.Equals(n.StrategicDoctrine.Id, course.Id, StringComparison.Ordinal);
            var card = new PanelContainer
            {
                Name = "Strategy_" + course.Id,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                ClipContents = true
            };
            card.AddThemeStyleboxOverride("panel", Ui.ChromeBox(
                "surface.inset", inForce ? 1f : 0.94f,
                inForce ? "text.accent" : "surface.line_faint",
                inForce ? 0.92f : 0.80f,
                cornerRadius: 4, borderWidth: 1, padX: 12, padY: 10,
                leftBorderWidth: inForce ? 4 : 1));
            host.AddChild(card);

            var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            body.AddThemeConstantOverride("separation", 6);
            card.AddChild(body);

            var topRow = new HBoxContainer();
            topRow.AddThemeConstantOverride("separation", 8);
            body.AddChild(topRow);

            var title = Title(Tr(course.Name), 15, Ui.Colour(inForce ? "text.accent" : "text.bright"));
            title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            title.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            topRow.AddChild(title);

            var badgeText = inForce
                ? Loc.Get("doctrine.ui.pill_in_force")
                : n.StrategicDoctrine.Id.Length == 0
                    ? Loc.Get("doctrine.ui.free_first_choice")
                    : Fmt.Number(course.PoliticalCost);
            var badgeTok = inForce ? "text.accent" : n.StrategicDoctrine.Id.Length == 0 ? "state.positive" : "text.caption";
            var badge = new PanelContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter };
            badge.AddThemeStyleboxOverride("panel", Ui.ChromeBox(badgeTok, 0.16f, badgeTok, 0.72f, cornerRadius: 3, borderWidth: 1, padX: 6, padY: 1));
            var badgeLbl = Title(badgeText, 11, Ui.Colour(badgeTok));
            badgeLbl.AddThemeFontOverride("font", PresentationFonts.Tabular());
            badgeLbl.AutowrapMode = TextServer.AutowrapMode.Off;
            badge.AddChild(badgeLbl);
            topRow.AddChild(badge);

            var desc = Title(Tr(course.Description), 12, Ui.Colour("text.body_soft"));
            desc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            body.AddChild(desc);

            var effects = NationalModifiers.Block(course.Modifiers);
            if (!string.IsNullOrEmpty(effects))
            {
                var effBox = new PanelContainer { ClipContents = true };
                effBox.AddThemeStyleboxOverride("panel", Ui.ChromeBox("surface.panel", 0.55f, "surface.line_faint", 0.50f, cornerRadius: 3, borderWidth: 1, padX: 8, padY: 5));
                var effLbl = Title(effects, 12, Ui.Colour("text.body"));
                effLbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                effBox.AddChild(effLbl);
                body.AddChild(effBox);
            }

            var learningLbl = Title(FormatCourseLearningLine(course), 12, Ui.Colour("text.caption"));
            learningLbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            learningLbl.AddThemeFontOverride("font", PresentationFonts.Tabular());
            body.AddChild(learningLbl);

            body.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore });

            var refusal = StrategicDoctrines.Refusal(_campaign, n, course.Id);
            var button = CreateAdoptStrategyButton(n, course, inForce, refusal, compact: false);
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            body.AddChild(button);
        }
    }

    private Button CreateAdoptStrategyButton(Nation n, StrategicDoctrine course, bool inForce, string refusal, bool compact)
    {
        var label = inForce ? Loc.Get("in force")
            : n.StrategicDoctrine.Id.Length == 0 ? Loc.Get("take this course")
            : Loc.Format("change course ({0})", Fmt.Number(course.PoliticalCost));
        var button = ActionButton(
            label,
            () =>
            {
                if (_campaign?.Player is not { } player) return;
                ApplyDoctrineCommand(new AdoptStrategicDoctrineCommand
                    { IssuerId = player.Id, DoctrineId = course.Id });
            }, compact ? 0 : 0, compact ? 28 : 32, !inForce && refusal.Length == 0 && !compact,
            "res://game/gfx/interface/icons/icon_agenda.png");
        button.Name = "AdoptStrategy_" + course.Id;
        button.Disabled = inForce || refusal.Length > 0;
        button.TooltipText = Ui.Tip(refusal.Length > 0 ? refusal : Tr(course.Description));
        return button;
    }

    private static string FormatCourseLearningLine(StrategicDoctrine course)
    {
        var learning = Loc.Format("Drill ×{0}, the line ×{1}, the air ×{2}, the sea ×{3}",
            Fmt.Number(course.DrillExperience), Fmt.Number(course.BattleExperience),
            Fmt.Number(course.AirExperience), Fmt.Number(course.NavalExperience));
        if (Math.Abs(course.TrainingSlots - 1f) > 0.001f)
            learning += " · " + Loc.Format("{0} formations on the drill square", Fmt.Number(course.TrainingSlots));
        if (course.TacticalBias.Length > 0 && TacticalDoctrineCatalog.Find(course.TacticalBias) is { } leaning)
            learning += " · " + Loc.Format("leans on {0}", Tr(leaning.Name));
        return learning;
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
                var empty = Title("No doctrine adopted.", 14, Ui.Colour("text.body_soft"));
                Tip(empty, "Shelf empty. The first article of any book costs nothing political; abandoning one later does.");
                rows.AddChild(empty);
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
