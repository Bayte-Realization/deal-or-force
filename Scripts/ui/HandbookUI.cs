using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The handbook (F1, or "Handbook" beside the menu button, or the "?" on a screen): the game
/// explained inside the game.
///
/// A list of articles by topic on the left, searchable; the article on the right, with a
/// button that opens the screen it is about and links to the articles worth reading next.
/// The words live in localisation and the structure in common/handbook (HandbookCatalog), so
/// a mod that adds a mechanic adds the page that explains it.
///
/// And the first steps: a short list on the situation rail of the things a new player should
/// do once - let time run, read the budget, pick a research project, build something, look at
/// the neighbours, give a division an order, open this handbook - each ticked when it has been
/// done, each a click away from the screen where it is done. Shown until all are done or it is
/// hidden; the option "First steps" brings it back.
/// </summary>
public partial class DealOrForceGame
{
    private Control _handbookLayer;
    private VBoxContainer _handbookList;
    private VBoxContainer _handbookPage;
    private LineEdit _handbookSearch;
    private ScrollContainer _handbookPageScroll;
    private string _handbookArticle = "";

    private void BuildHandbook()
    {
        _handbookLayer = FullLayer("Handbook");
        _handbookLayer.Visible = false;
        _hudLayer.AddChild(_handbookLayer);

        var shade = new ColorRect { Color = Ui.Colour("surface.overlay"), MouseFilter = MouseFilterEnum.Stop };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _handbookLayer.AddChild(shade);

        var panel = TexturedPanel(_handbookLayer);
        Ui.Place(panel, "modal.handbook");
        var rows = PanelRows(panel, 22, 16, 10);

        ScreenHeader(rows, "HANDBOOK",
            "What every screen is for, what its figures mean and what to do about them. F1 opens this from anywhere.",
            true, ActionButton("×", CloseHandbook, 38, 32));

        var body = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 16);
        rows.AddChild(body);

        var left = new VBoxContainer { CustomMinimumSize = new Vector2(330, 0), SizeFlagsVertical = SizeFlags.ExpandFill };
        left.AddThemeConstantOverride("separation", 6);
        body.AddChild(left);
        _handbookSearch = Placeholder(new LineEdit { CustomMinimumSize = new Vector2(0, 30), ClearButtonEnabled = true }, "Search the handbook...");
        _handbookSearch.Name = "HandbookSearch";
        _handbookSearch.TextChanged += _ => RebuildHandbookList();
        left.AddChild(_handbookSearch);
        var listFrame = new PanelContainer { Name = "HandbookListFrame", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        listFrame.SetMeta("atlas_section_ground", "surface.inset");
        listFrame.AddThemeStyleboxOverride("panel", AtlasSurfaceSkin.Section("surface.inset"));
        AtlasSurfaceSkin.Attach(listFrame, AtlasSurfaceSkin.Level.Inset, "surface.inset");
        left.AddChild(listFrame);
        var listScroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        listFrame.AddChild(listScroll);
        _handbookList = new VBoxContainer { Name = "HandbookList", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _handbookList.AddThemeConstantOverride("separation", 3);
        listScroll.AddChild(_handbookList);

        var pageFrame = new PanelContainer { Name = "HandbookPageFrame", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        pageFrame.SetMeta("atlas_section_ground", "surface.inset");
        pageFrame.AddThemeStyleboxOverride("panel", AtlasSurfaceSkin.Section("surface.inset"));
        AtlasSurfaceSkin.Attach(pageFrame, AtlasSurfaceSkin.Level.Inset, "surface.inset");
        body.AddChild(pageFrame);
        _handbookPageScroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        pageFrame.AddChild(_handbookPageScroll);
        _handbookPage = new VBoxContainer { Name = "HandbookPage", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _handbookPage.AddThemeConstantOverride("separation", 10);
        _handbookPageScroll.AddChild(_handbookPage);

        // Loc is process-wide; the handbook belongs to this scene. A named callback
        // can be removed on exit instead of retaining a disposed campaign and its UI.
        Simulation.Loc.LanguageChanged -= RefreshHandbookLanguage;
        Simulation.Loc.LanguageChanged += RefreshHandbookLanguage;
    }

    private void RefreshHandbookLanguage()
    {
        if (!IsInsideTree() || !IsInstanceValid(_handbookLayer) || !_handbookLayer.Visible) return;
        RebuildHandbookList(); ShowHandbookArticle(_handbookArticle);
    }

    /// <summary>Open the handbook at an article, at the one for the screen in front of the player, or at the start.</summary>
    internal void OpenHandbook(string articleId = "")
    {
        if (_handbookLayer is null) return;
        if (string.IsNullOrEmpty(articleId)) articleId = HandbookCatalog.ForScreen(_openScreen);
        if (string.IsNullOrEmpty(articleId) || HandbookCatalog.Find(articleId) is null) articleId = "first_steps";
        _handbookLayer.Visible = true;
        _handbookLayer.MoveToFront();
        ShowHandbookArticle(articleId);
        RebuildHandbookList();
        MarkFirstStep("handbook");
    }

    private void CloseHandbook()
    {
        if (_handbookLayer is not null) _handbookLayer.Visible = false;
    }

    private void ToggleHandbook()
    {
        if (_handbookLayer is { Visible: true }) CloseHandbook();
        else OpenHandbook();
    }

    /// <summary>A small "?" that opens the handbook at the article about this screen.</summary>
    private Button HandbookButton(string articleId)
    {
        var button = ActionButton("?", () => OpenHandbook(articleId), 32, 32);
        button.Name = "HandbookButton";
        Tip(button, "Open the handbook at the page about this screen (F1).");
        return button;
    }

    private void RebuildHandbookList()
    {
        if (_handbookList is null) return;
        ClearRuntimeChildren(_handbookList);
        var query = _handbookSearch?.Text ?? "";
        var found = HandbookCatalog.Search(query).ToList();
        if (found.Count == 0)
        {
            _handbookList.AddChild(Title("Nothing in the handbook mentions that.", 13, Ui.Colour("text.caption")));
            return;
        }
        var shownTopic = "";
        foreach (var article in found)
        {
            if (article.Topic != shownTopic)
            {
                shownTopic = article.Topic;
                var topic = HandbookCatalog.OrderedTopics.FirstOrDefault(t => t.Id == article.Topic);
                var caption = new Label
                {
                    Text = (topic?.Name ?? article.Topic).ToUpperInvariant(),
                    AutowrapMode = TextServer.AutowrapMode.WordSmart
                };
                caption.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.handbook.caption", 11));
                caption.AddThemeColorOverride("font_color", Ui.Colour("text.caption"));
                _handbookList.AddChild(caption);
            }
            var id = article.Id;
            var button = new Button
            {
                Text = article.Title,
                Alignment = HorizontalAlignment.Left,
                CustomMinimumSize = new Vector2(0, 30),
                ClipText = true
            };
            StyleButton(button, id == _handbookArticle);
            button.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.handbook.button", 13));
            button.Pressed += () => { PlayStrategicCue("ui_select"); ShowHandbookArticle(id); RebuildHandbookList(); };
            button.Name = "Handbook_" + id;
            _handbookList.AddChild(button);
        }
    }

    private void ShowHandbookArticle(string id)
    {
        if (_handbookPage is null) return;
        var article = HandbookCatalog.Find(id);
        if (article is null) return;
        _handbookArticle = id;
        ClearRuntimeChildren(_handbookPage);

        var heading = new Label { Text = article.Title, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        heading.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.handbook.heading", 24));
        heading.AddThemeColorOverride("font_color", Ui.Colour("text.bright"));
        _handbookPage.AddChild(heading);

        foreach (var block in article.Body.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var text = block.Trim();
            if (text.Length == 0) continue;
            if (text.StartsWith("## ", StringComparison.Ordinal))
            {
                var sub = new Label { Text = text[3..].Trim().ToUpperInvariant(), AutowrapMode = TextServer.AutowrapMode.WordSmart };
                sub.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.handbook.sub", 13));
                sub.AddThemeColorOverride("font_color", Ui.Colour("text.accent"));
                _handbookPage.AddChild(sub);
                continue;
            }
            var lines = text.Split('\n');
            if (lines.All(line => line.TrimStart().StartsWith("- ", StringComparison.Ordinal)))
            {
                var list = new VBoxContainer();
                list.AddThemeConstantOverride("separation", 4);
                foreach (var line in lines)
                {
                    var point = new Label
                    {
                        Text = "•  " + line.TrimStart()[2..].Trim(),
                        AutowrapMode = TextServer.AutowrapMode.WordSmart,
                        SizeFlagsHorizontal = SizeFlags.ExpandFill
                    };
                    point.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.handbook.point", 15));
                    point.AddThemeColorOverride("font_color", Ui.Colour("text.body"));
                    list.AddChild(point);
                }
                _handbookPage.AddChild(list);
                continue;
            }
            var paragraph = new Label
            {
                Text = string.Join(" ", lines.Select(line => line.Trim())),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            paragraph.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.handbook.paragraph", 15));
            paragraph.AddThemeColorOverride("font_color", Ui.Colour("text.body"));
            _handbookPage.AddChild(paragraph);
        }

        var actions = new HFlowContainer();
        actions.AddThemeConstantOverride("h_separation", 6);
        actions.AddThemeConstantOverride("v_separation", 6);
        if (article.Screen.Length > 0 && CampaignUiCatalog.Destinations.Contains(article.Screen) &&
            MechanicFeatures.ScreenEnabled(_campaign, article.Screen))
        {
            var screen = article.Screen;
            var open = ActionButton("Open this screen", () => { CloseHandbook(); OpenScreen(screen); }, 0, 34, primary: true);
            actions.AddChild(open);
        }
        foreach (var relatedId in article.Related)
        {
            var related = HandbookCatalog.Find(relatedId);
            if (related is null) continue;
            var captured = relatedId;
            var link = new Button { Text = "→ " + related.Title, CustomMinimumSize = new Vector2(0, 34) };
            StyleButton(link, false);
            link.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.handbook.link", 13));
            link.Pressed += () => { PlayStrategicCue("ui_select"); ShowHandbookArticle(captured); RebuildHandbookList(); };
            actions.AddChild(link);
        }
        if (actions.GetChildCount() > 0)
        {
            _handbookPage.AddChild(new HSeparator());
            _handbookPage.AddChild(actions);
        }
        if (_handbookPageScroll is not null) _handbookPageScroll.ScrollVertical = 0;
    }

    // ================================================================== the first steps

    /// <summary>The first steps, in order: id, what to do, why, and where it is done.</summary>
    private static readonly (string Id, string Text, string Why, string Screen)[] FirstSteps =
    {
        ("time", "Let time run: Space, or a speed button", "The campaign opens paused. Nothing happens until the clock runs, and nothing is lost by looking around first.", ""),
        ("budget", "Read the budget", "Income, spending and the balance a week: the first figure that will hurt if nobody looks at it.", "State Affairs"),
        ("research", "Choose a research project", "Research points are spent every week. With no project chosen they are wasted.", "Research"),
        ("construction", "Start building something", "Construction capacity that builds nothing is capacity thrown away. Open a region of yours and build.", "Economy"),
        ("diplomacy", "Look at your neighbours", "Who likes you, who does not and why: the diplomacy screen lists every country with its opinion of you.", "Diplomacy"),
        ("army", "Select a division and give it an order", "Left click a counter on the map, then right click where it should go. The army panel explains every order.", "Military"),
        ("handbook", "Open the handbook (F1)", "Every screen has a page here. The \"?\" on a screen opens the page about it.", "")
    };

    private HashSet<string> _firstStepsDone;

    private HashSet<string> FirstStepsDone() => _firstStepsDone ??= new HashSet<string>(
        PlayerSettings.Text("first_steps_done", "").Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    /// <summary>Tick a first step, once and for good.</summary>
    private void MarkFirstStep(string id)
    {
        var done = FirstStepsDone();
        if (!done.Add(id)) return;
        PlayerSettings.Set("first_steps_done", string.Join(",", done.OrderBy(step => step, StringComparer.Ordinal)));
        _strategicHudFingerprint = int.MinValue;
        if (!_drawingFirstSteps) RefreshStrategicHud();
    }

    /// <summary>Set while the rail is being drawn, when a step noticed on the way must not redraw it again.</summary>
    private bool _drawingFirstSteps;

    private bool FirstStepsShowing =>
        SettingsCatalog.Flag("show_first_steps") && FirstSteps.Any(step => !FirstStepsDone().Contains(step.Id));

    /// <summary>The things the game can see were done without being told: time run, a project chosen, something built.</summary>
    private void NoticeFirstSteps()
    {
        var player = _campaign?.Player;
        if (player is null) return;
        if (_campaign.ElapsedHours > 0) MarkFirstStep("time");
        if (!string.IsNullOrEmpty(player.CurrentResearch)) MarkFirstStep("research");
        if (_campaign.Construction.Any(project => project.NationId == player.Id)) MarkFirstStep("construction");
        if (_selectedArmies.Count > 0) MarkFirstStep("army");
    }

    /// <summary>The first-steps group on the situation rail.</summary>
    private void AddFirstStepsToOutliner()
    {
        _drawingFirstSteps = true;
        try { NoticeFirstSteps(); }
        finally { _drawingFirstSteps = false; }
        if (!FirstStepsShowing) return;
        AddOutlinerHeader("FIRST STEPS");
        var done = FirstStepsDone();
        foreach (var (id, text, why, screen) in FirstSteps)
        {
            var finished = done.Contains(id);
            var captured = (id, screen);
            AddOutlinerButton((finished ? "✓ " : "○ ") + Tr(text), () =>
            {
                if (captured.screen.Length > 0) OpenScreen(captured.screen);
                else if (captured.id == "handbook") OpenHandbook("first_steps");
                else if (captured.id == "time") TogglePause();
            }, Tr(why));
            if (_outlinerGroupRows.GetChild(_outlinerGroupRows.GetChildCount() - 1) is Button row)
            {
                row.Name = "FirstStep_" + id;
                row.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                row.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.handbook.row", 12));
                if (finished) row.AddThemeColorOverride("font_color", Ui.Colour("text.caption"));
            }
        }
        AddOutlinerButton(Tr("Hide the first steps"), () =>
        {
            SettingsCatalog.Store("show_first_steps", false);
            _strategicHudFingerprint = int.MinValue;
            RefreshStrategicHud();
        }, Tr("They can be brought back in the settings, under First steps."));
    }
}
