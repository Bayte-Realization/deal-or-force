using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The book of war on a screen of its own: unified mode-aware hub where Tactical Books show the
/// service shelf filter tabs + left book catalogue + center doctrine tree, while Strategic Course
/// and Generals hide the irrelevant service/catalogue chrome to claim the full wide center canvas.
/// </summary>
public partial class DealOrForceGame
{
    private Control _doctrineLayer;
    private CanvasLayer _doctrineCanvas;
    private VBoxContainer _doctrineBookList;
    private VBoxContainer _doctrineCentre;
    private VBoxContainer _doctrineSide;
    private VBoxContainer _doctrineScreenCurrencies;
    private HBoxContainer _doctrineToolbar;
    private Label _doctrineScreenSummary;
    private Label _doctrineZoomLabel;
    private Button _doctrineTacticalView, _doctrineCourseView, _doctrineGeneralsView;
    private string _doctrineView = "tactical";
    private int _doctrineScreenDay = -1;
    private DoctrinePresentationState? _doctrineScreenState;

    private void BuildDoctrineScreen()
    {
        _doctrineCanvas = new CanvasLayer { Name = "DoctrineCanvas", Layer = 12 };
        AddChild(_doctrineCanvas);
        _doctrineLayer = FullLayer("Doctrines");
        _doctrineLayer.Visible = false;
        _doctrineCanvas.AddChild(_doctrineLayer);
        var shade = new ColorRect { Color = Ui.Colour("surface.overlay"), MouseFilter = MouseFilterEnum.Stop };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        shade.OffsetLeft = InterfaceSkin.Metric("shell.navigation_size", 82);
        shade.SetMeta("rail_shade", true);
        _doctrineLayer.AddChild(shade);
        var panel = TexturedPanel(_doctrineLayer);
        _doctrineScreenPanel = panel; panel.Name = "DoctrineWorkbench";
        Ui.Place(panel, "modal.doctrines");
        var rows = PanelRows(panel, 18, 14, 8);

        _doctrineTacticalView = ActionButton("Tactical books", () => SetDoctrineView("tactical"), 176, 34, true,
            "res://game/gfx/interface/icons/icon_military.png");
        _doctrineTacticalView.Name = "DoctrineTacticalView";
        _doctrineTacticalView.SetMeta("atlas_tab_button", true);
        StyleButton(_doctrineTacticalView, true);
        _doctrineCourseView = ActionButton("Strategic course", () => SetDoctrineView("course"), 176, 34, false,
            "res://game/gfx/interface/icons/icon_agenda.png");
        _doctrineCourseView.Name = "DoctrineCourseView";
        _doctrineCourseView.SetMeta("atlas_tab_button", true);
        StyleButton(_doctrineCourseView, false);
        _doctrineGeneralsView = ActionButton("Generals", () => SetDoctrineView("generals"), 150, 34, false,
            "res://game/gfx/interface/icons/icon_military.png");
        _doctrineGeneralsView.Name = "DoctrineGeneralsView";
        _doctrineGeneralsView.SetMeta("atlas_tab_button", true);
        StyleButton(_doctrineGeneralsView, false);
        var toMilitary = ActionButton("Army", () => { CloseDoctrineScreen(); OpenScreen("Military"); }, 0, 34, false,
            "res://game/gfx/interface/icons/icon_military.png");
        var close = ActionButton("×", CloseDoctrineScreen, 38, 32);
        close.Name = "CloseDoctrineScreen";
        close.TooltipText = Ui.Tip(Tr("Close"));
        ScreenHeader(rows, "BOOK OF WAR",
            "What each service is taught to be, what it costs to change its mind, and what the country pays it in.",
            false, toMilitary, close);
        var tabs = TabStrip(rows, "DoctrineViewTabs");
        foreach (var button in new[] { _doctrineTacticalView, _doctrineCourseView, _doctrineGeneralsView })
            tabs.AddChild(button);

        _doctrineScreenSummary = Title("", (int)Ui.Metric("ui.font.body", 12), Ui.Colour("text.body"));
        _doctrineScreenSummary.Name = "DoctrineScreenSummary";
        _doctrineScreenSummary.AutowrapMode = TextServer.AutowrapMode.Off;
        _doctrineScreenSummary.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _doctrineScreenSummary.ClipText = true;
        _doctrineScreenSummary.AddThemeFontOverride("font", PresentationFonts.Tabular());
        rows.AddChild(_doctrineScreenSummary);
        BuildDoctrineServices(rows);

        var bodyScroll = new ScrollContainer
        {
            Name = "DoctrineBodyScroll",
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        rows.AddChild(bodyScroll);
        var body = new HBoxContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        body.AddThemeConstantOverride("separation", 14);
        bodyScroll.AddChild(body);

        // Left: service-filtered books + currencies (visible on Tactical Books tab only).
        var left = new VBoxContainer { Name = "DoctrineCatalogueColumn", CustomMinimumSize = new Vector2(266, 0) };
        _doctrineCatalogueColumn = left;
        left.AddThemeConstantOverride("separation", 6);
        body.AddChild(left);
        _doctrineCatalogueHeading = Title("", 13, Ui.Colour("text.accent"));
        _doctrineCatalogueHeading.Name = "DoctrineCatalogueHeading";
        left.AddChild(_doctrineCatalogueHeading);
        var bookScroll = new ScrollContainer
        {
            Name = "DoctrineBookScroll",
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            FollowFocus = true
        };
        left.AddChild(bookScroll);
        _doctrineBookList = new VBoxContainer
        {
            Name = "DoctrineBookList", SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _doctrineBookList.AddThemeConstantOverride("separation", 4);
        bookScroll.AddChild(_doctrineBookList);
        _doctrineScreenCurrencies = new VBoxContainer { Name = "DoctrineScreenCurrencies" };
        left.AddChild(_doctrineScreenCurrencies);

        // Middle: full-width center canvas for tree, 2-column Strategic Courses, or Generals.
        _doctrineCentre = new VBoxContainer
        {
            Name = "DoctrineCentre",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1.5f
        };
        _doctrineCentre.AddThemeConstantOverride("separation", 6);
        body.AddChild(_doctrineCentre);

        // Right: what the army is reading now, and what buys it.
        var side = new VBoxContainer { Name = "DoctrineSideColumn", CustomMinimumSize = new Vector2(368, 0) };
        _doctrinePreviewColumn = side;
        body.AddChild(side);
        _doctrineSide = PolicyColumn(side, "DoctrineSide", true);
    }

    /// <summary>Opening the book: the world stays behind it, and the map does not take the wheel.</summary>
    private void OpenDoctrineScreen()
    {
        EnsureScreen("doctrine", BuildDoctrineScreen);
        _openScreen = "Doctrines";
        _sidePanel.Visible = false;
        HideMapSelection(true);
        SetArmyPanelVisible(false);
        RaiseWorkspaceLayer(_doctrineLayer);
        _doctrineScreenDay = -1;
        RefreshDoctrineScreen();
        UpdateScreenButtons();
    }

    private void OpenDoctrineScreenOnView(string view)
    {
        _doctrineView = view;
        OpenDoctrineScreen();
    }

    private void OpenDoctrineScreenOnService(string slot)
    {
        _doctrineView = "tactical";
        OpenDoctrineScreen();
        SelectDoctrineService(slot);
    }

    private void CloseDoctrineScreen()
    {
        if (_doctrineLayer is not null) _doctrineLayer.Visible = false;
        if (_openScreen == "Doctrines") _openScreen = "";
        UpdateScreenButtons();
    }

    private void SetDoctrineView(string view)
    {
        _doctrineView = view;
        _doctrineScreenDay = -1;
        RefreshDoctrineScreen();
    }

    private void RefreshDoctrineScreen()
    {
        if (_doctrineLayer is not { Visible: true } || _campaign?.Player is null) return;
        var n = _campaign.Player;
        var weekly = _campaign.Defines.PoliticalPointsPerWeek +
            _campaign.Defines.PoliticalPointsStabilityWeight * n.Stability;
        var countryName = n.DisplayName;
        if (_doctrineBook.Length == 0 || TacticalDoctrineCatalog.Find(_doctrineBook) is null)
            _doctrineBook = n.TacticalDoctrine.Branch.Length > 0
                ? n.TacticalDoctrine.Branch
                : TacticalDoctrineCatalog.All.FirstOrDefault()?.Id ?? "";
        var state = DoctrinePresentation(n);
        var geometryChanged = UpdateDoctrineComfortGeometry();
        if (_doctrineScreenDay == _campaign.ElapsedDays && _doctrineScreenState == state && !geometryChanged &&
            _doctrineSummaryWeekly == weekly && _doctrineSummaryCountryName == countryName) return;
        _doctrineScreenDay = _campaign.ElapsedDays;
        _doctrineScreenState = state;
        _doctrineSummaryWeekly = weekly; _doctrineSummaryCountryName = countryName;
        var summary = Loc.Format(
            "{0} · army {1} · air {2} · navy {3} · political credit {4} ({5}/week)",
            countryName, Fmt.Number(n.ArmyExperience), Fmt.Number(n.AirExperience),
            Fmt.Number(n.NavalExperience), Fmt.Number(n.PoliticalPoints), Fmt.Number(weekly));
        if (summary != _doctrineScreenSummary.Text)
        {
            _doctrineScreenSummary.Text = summary;
            _doctrineScreenSummary.TooltipText = Ui.Tip(summary);
        }
        StyleButton(_doctrineTacticalView, _doctrineView == "tactical");
        StyleButton(_doctrineCourseView, _doctrineView == "course");
        StyleButton(_doctrineGeneralsView, _doctrineView == "generals");

        var isTactical = _doctrineView == "tactical";
        if (_doctrineServiceTabs is not null) _doctrineServiceTabs.Visible = isTactical;
        if (_doctrineCatalogueColumn is not null) _doctrineCatalogueColumn.Visible = isTactical;

        UpdateDoctrineZoomLabel();
        ClearPolicyRows(_doctrineScreenCurrencies);
        CurrencyStrip(_doctrineScreenCurrencies, n, withPolitical: false, columns: 2);
        RebuildDoctrineBookList(n);
        RefreshDoctrineServices();

        ClearPolicyRows(_doctrineCentre);
        if (_doctrineView == "generals")
        {
            var generalsScroll = new ScrollContainer
            {
                Name = "DoctrineGeneralsScroll",
                SizeFlagsVertical = SizeFlags.ExpandFill,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                ClipContents = true,
                FollowFocus = true
            };
            _doctrineCentre.AddChild(generalsScroll);
            var pad = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
            pad.AddThemeConstantOverride("margin_right", 14);
            generalsScroll.AddChild(pad);
            var generalsRows = new VBoxContainer { Name = "DoctrineGeneralsRows", SizeFlagsHorizontal = SizeFlags.ExpandFill };
            generalsRows.AddThemeConstantOverride("separation", 8);
            pad.AddChild(generalsRows);
            GeneralsPage(n, generalsRows);
        }
        else if (_doctrineView == "course")
        {
            var courseScroll = new ScrollContainer
            {
                Name = "DoctrineCourseScroll",
                SizeFlagsVertical = SizeFlags.ExpandFill,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                ClipContents = true,
                FollowFocus = true
            };
            _doctrineCentre.AddChild(courseScroll);
            var pad = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
            pad.AddThemeConstantOverride("margin_right", 14);
            courseScroll.AddChild(pad);
            var courseRows = new VBoxContainer { Name = "DoctrineCourseRows", SizeFlagsHorizontal = SizeFlags.ExpandFill };
            courseRows.AddThemeConstantOverride("separation", 8);
            pad.AddChild(courseRows);
            CurrencyStrip(courseRows, n, withPolitical: true, columns: 4);
            StrategicDoctrinePage(n, courseRows, wideGrid: true);
        }
        else
        {
            DoctrineTreeToolbar(_doctrineCentre);
            DoctrineTreeWindow(_doctrineCentre);
            if (TacticalDoctrineCatalog.Find(_doctrineBook) is { } book)
            {
                RebuildDoctrineTree(n, book);
                RefreshDoctrineTree(n, book);
            }
        }

        ClearPolicyRows(_doctrineSide);
        if (_doctrineView == "generals") GeneralsSide(_doctrineSide);
        else ShowDoctrinePreview(n, _doctrineSide);
        FitDoctrineTypography(_doctrineLayer);
    }

    /// <summary>The books as a column, shelf by shelf: clear [IN FORCE]/[COMPANION]/[STUDYING] status and X/Y progress.</summary>
    private void RebuildDoctrineBookList(Nation n)
    {
        if (_doctrineBookList is null) return;
        var books = TacticalDoctrineCatalog.All.ToArray();
        var live = books.Select(book => book.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _doctrineBookButtons.Keys.Where(id => !live.Contains(id)).ToArray())
        {
            var old = _doctrineBookButtons[id].GetParent(); old.GetParent()?.RemoveChild(old);
            old.QueueFree(); _doctrineBookButtons.Remove(id);
        }
        TacticalDoctrines.EnsureShelves(n);
        foreach (var branch in books)
        {
            var id = branch.Id;
            var shelf = n.TacticalDoctrine.Track(branch.Slot);
            var isPrimary = string.Equals(shelf.Branch, id, StringComparison.Ordinal);
            var isCompanion = shelf.Companion is not null && string.Equals(shelf.Companion.Branch, id, StringComparison.Ordinal);
            var track = TacticalDoctrines.TrackFor(n, branch);
            var inForce = isPrimary || isCompanion;
            var isStudying = track is not null && string.Equals(track.LearningBranch, id, StringComparison.Ordinal) && track.LearningNode.Length > 0;
            var written = inForce && track is not null ? track.Known.Count : 0;
            if (!_doctrineBookButtons.TryGetValue(id, out var button))
            {
                button = ActionButton("", () => SelectDoctrineBook(id), 0, 46, inForce);
                var retainedRow = new HBoxContainer { Name = "BookRow_" + id };
                retainedRow.AddThemeConstantOverride("separation", 6);
                var retainedEdge = new Panel { Name = "StatusEdge", CustomMinimumSize = new Vector2(4, 0), MouseFilter = MouseFilterEnum.Ignore };
                retainedRow.AddChild(retainedEdge); retainedRow.AddChild(button);
                _doctrineBookList.AddChild(retainedRow); _doctrineBookButtons[id] = button;
            }
            button.Name = "ScreenBook_" + id;
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.ClipText = true;
            button.AutowrapMode = TextServer.AutowrapMode.WordSmart;

            var statusTag = isPrimary ? " " + Loc.Get("doctrine.ui.tag_in_force")
                : isCompanion ? " " + Loc.Get("doctrine.ui.tag_companion")
                : isStudying ? " " + Loc.Get("doctrine.ui.tag_studying")
                : "";
            button.Text = Tr(branch.Name) + statusTag + "\n" +
                Loc.Format("{0} · {1}/{2} articles", Tr(DoctrineSlots.Name(branch.Slot)), written, branch.Nodes.Count);
            if (!button.HasMeta("doctrine_base_font")) button.AddThemeFontSizeOverride("font_size", 14);
            button.TooltipText = Ui.Tip(Tr(branch.Name) + "\n" + Tr(branch.Description) + "\n" +
                Loc.Format("Paid for in {0}.", Loc.Get(TacticalDoctrines.ServiceName(branch.Experience))));
            StyleButton(button, branch.Id == _doctrineBook);

            var row = button.GetParent() as HBoxContainer;
            var edge = row.GetNode<Panel>("StatusEdge");
            var edgeTok = isPrimary ? "text.accent" : (isCompanion || isStudying) ? "state.info" : "surface.line_faint";
            edge.AddThemeStyleboxOverride("panel", Ui.ChromeBox(edgeTok, 1f, edgeTok, 0f, cornerRadius: 1, borderWidth: 0));
            _doctrineBookList.MoveChild(row, Array.IndexOf(books, branch));
        }
    }

    private void DoctrineTreeToolbar(Control rows)
    {
        if (_doctrineToolbar is null)
        {
            _doctrineToolbar = new HBoxContainer { Name = "DoctrineTreeToolbar" };
            _doctrineToolbar.AddThemeConstantOverride("separation", 6);
            _doctrineToolbar.SetMeta(KeepOnRefresh, true);
            var further = ActionButton("−", () => NudgeDoctrineZoom(false), 46, 30);
            further.Name = "DoctrineZoomOut";
            further.TooltipText = Ui.Tip(Tr("Further away"));
            _doctrineToolbar.AddChild(further);
            _doctrineZoomLabel = Title("100%", 13, Ui.Colour("text.body"));
            _doctrineZoomLabel.Name = "DoctrineZoomLabel";
            _doctrineZoomLabel.CustomMinimumSize = new Vector2(66, 0);
            _doctrineZoomLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _doctrineZoomLabel.AddThemeFontOverride("font", PresentationFonts.Tabular());
            _doctrineToolbar.AddChild(_doctrineZoomLabel);
            var closer = ActionButton("+", () => NudgeDoctrineZoom(true), 46, 30);
            closer.Name = "DoctrineZoomIn";
            closer.TooltipText = Ui.Tip(Tr("Closer"));
            _doctrineToolbar.AddChild(closer);
            var reset = ActionButton("100%", ResetDoctrineZoom, 88, 30);
            reset.Name = "DoctrineZoomReset";
            _doctrineToolbar.AddChild(reset);
            var hint = Title(TreeViewport.Hint, 12, Ui.Colour("text.caption"));
            hint.Name = "DoctrineZoomHint";
            hint.AutowrapMode = TextServer.AutowrapMode.Off;
            hint.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            hint.ClipText = true;
            hint.TooltipText = Ui.Tip(TreeViewport.Hint);
            hint.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _doctrineToolbar.AddChild(hint);
        }
        if (_doctrineToolbar.GetParent() is { } holder) holder.RemoveChild(_doctrineToolbar);
        rows.AddChild(_doctrineToolbar);
        if (_doctrineToolbar.GetNodeOrNull<Label>("DoctrineZoomHint") is { } hintLabel)
        {
            hintLabel.Text = TreeViewport.Hint;
            hintLabel.TooltipText = Ui.Tip(TreeViewport.Hint);
        }
        UpdateDoctrineZoomLabel();
    }

    private void NudgeDoctrineZoom(bool closer)
    {
        if (_doctrineScroll is null || _doctrineViewport is null || _doctrineTree is null) return;
        TreeViewport.Nudge(_doctrineScroll, _doctrineViewport, _doctrineTree, _doctrineExtent,
            ref _doctrineZoom, closer);
        UpdateDoctrineZoomLabel();
    }

    private void ResetDoctrineZoom()
    {
        TreeViewport.Reset(_doctrineViewport, _doctrineTree, _doctrineExtent, ref _doctrineZoom);
        UpdateDoctrineZoomLabel();
    }

    private void UpdateDoctrineZoomLabel()
    {
        if (_doctrineZoomLabel is not null) _doctrineZoomLabel.Text = TreeViewport.PercentLabel(_doctrineZoom);
    }
}
