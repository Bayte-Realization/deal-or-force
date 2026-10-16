using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The book of war on a screen of its own.
///
/// The Army panel could show a column of cards and one article at a time, which is not what a tree
/// is for: the point of a doctrine tree is seeing the whole book at once - what is in force, what
/// it opens, and what the far end of the branch would cost. So the tree gets the wide screen the
/// technology graph has: books down the left, the open book in the middle with its lines drawn,
/// and what the army is actually reading on the right.
///
/// Every button reads the same refusal the command and the AI go through, the canvas is the same
/// plate the Army tab shows in miniature (DoctrineTreeWindow), and the hands are the same two
/// (TreeViewport), with buttons beside the toolbar for a layout where Alt with the wheel belongs
/// to the window manager.
/// </summary>
public partial class DealOrForceGame
{
    private Control _doctrineLayer;
    private VBoxContainer _doctrineBookList;
    private VBoxContainer _doctrineCentre;
    private VBoxContainer _doctrineSide;
    private VBoxContainer _doctrineScreenCurrencies;
    private Label _doctrineScreenSummary;
    private Label _doctrineZoomLabel;
    private Button _doctrineTacticalView, _doctrineCourseView;
    private string _doctrineView = "tactical";
    private int _doctrineScreenDay = -1;

    private void BuildDoctrineScreen()
    {
        _doctrineLayer = FullLayer("Doctrines");
        _doctrineLayer.Visible = false;
        _hudLayer.AddChild(_doctrineLayer);
        var shade = new ColorRect { Color = Ui.Colour("surface.overlay"), MouseFilter = MouseFilterEnum.Stop };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _doctrineLayer.AddChild(shade);
        var panel = TexturedPanel(_doctrineLayer);
        Ui.Place(panel, "modal.doctrines");
        var rows = PanelRows(panel, 18, 14, 8);

        var header = new HBoxContainer();
        rows.AddChild(header);
        var title = Title(Tr("BOOK OF WAR"), 25, Ui.Colour("text.accent"));
        title.Name = "DoctrineScreenTitle";
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(title);
        _doctrineTacticalView = ActionButton("Tactical books", () => SetDoctrineView("tactical"), 176, 32, true,
            "res://game/gfx/interface/icons/icon_military.png");
        _doctrineTacticalView.Name = "DoctrineTacticalView";
        header.AddChild(_doctrineTacticalView);
        _doctrineCourseView = ActionButton("Strategic course", () => SetDoctrineView("course"), 176, 32, false,
            "res://game/gfx/interface/icons/icon_agenda.png");
        _doctrineCourseView.Name = "DoctrineCourseView";
        header.AddChild(_doctrineCourseView);
        var close = ActionButton("×", CloseDoctrineScreen, 38, 32);
        close.Name = "CloseDoctrineScreen";
        close.TooltipText = Ui.Tip(Tr("Close"));
        header.AddChild(close);

        _doctrineScreenSummary = Title("", 14, Ui.Colour("text.body"));
        _doctrineScreenSummary.Name = "DoctrineScreenSummary";
        _doctrineScreenSummary.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_doctrineScreenSummary);

        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 14);
        rows.AddChild(body);

        // Left: the six books, each saying how much of it is written down, and the four currencies
        // the books are paid in.
        var left = new VBoxContainer { CustomMinimumSize = new Vector2(266, 0) };
        left.AddThemeConstantOverride("separation", 6);
        body.AddChild(left);
        left.AddChild(Title(Tr("BOOKS"), 13, Ui.Colour("text.caption")));
        _doctrineBookList = new VBoxContainer { Name = "DoctrineBookList" };
        _doctrineBookList.AddThemeConstantOverride("separation", 4);
        left.AddChild(_doctrineBookList);
        _doctrineScreenCurrencies = new VBoxContainer { Name = "DoctrineScreenCurrencies" };
        left.AddChild(_doctrineScreenCurrencies);

        // Middle: the open book, as wide as the screen allows.
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
        body.AddChild(side);
        _doctrineSide = PolicyColumn(side, "DoctrineSide", true);
    }

    /// <summary>Opening the book: the world stays behind it, and the map does not take the wheel.</summary>
    private void OpenDoctrineScreen()
    {
        if (_doctrineLayer is null) BuildDoctrineScreen();
        _openScreen = "Doctrines";
        _sidePanel.Visible = false;
        HideMapSelection(true);
        SetArmyPanelVisible(false);
        foreach (var other in new[] { _ledgerLayer, _warLedgerLayer, _stateAffairsLayer, _diplomacyLayer,
                     _tradeScreenLayer, _productionLayer, _researchLayer, _policyLayer })
            if (other is not null) other.Visible = false;
        _doctrineLayer.Visible = true;
        _doctrineLayer.MoveToFront();
        _doctrineScreenDay = -1;
        RefreshDoctrineScreen();
        UpdateScreenButtons();
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

    /// <summary>
    /// What the wide screen says today: the summary line every frame, and the heavy parts - the
    /// book list, the currencies, the tree, the side column - once a campaign day, because the
    /// canvas must keep its zoom and its scroll across those redraws.
    /// </summary>
    private void RefreshDoctrineScreen()
    {
        if (_doctrineLayer is not { Visible: true } || _campaign?.Player is null) return;
        var n = _campaign.Player;
        var weekly = _campaign.Defines.PoliticalPointsPerWeek +
            _campaign.Defines.PoliticalPointsStabilityWeight * n.Stability;
        _doctrineScreenSummary.Text = Loc.Format(
            "{0} · army experience {1}, air {2}, sea {3} · political credit {4} ({5} a week) · {6}",
            n.DisplayName, Fmt.Number(n.ArmyExperience), Fmt.Number(n.AirExperience),
            Fmt.Number(n.NavalExperience), Fmt.Number(n.PoliticalPoints), Fmt.Number(weekly),
            TreeViewport.Hint);
        StyleButton(_doctrineTacticalView, _doctrineView == "tactical");
        StyleButton(_doctrineCourseView, _doctrineView == "course");
        UpdateDoctrineZoomLabel();
        if (_doctrineScreenDay == _campaign.ElapsedDays) return;
        _doctrineScreenDay = _campaign.ElapsedDays;
        if (_doctrineBook.Length == 0 || TacticalDoctrineCatalog.Find(_doctrineBook) is null)
            _doctrineBook = n.TacticalDoctrine.Branch.Length > 0
                ? n.TacticalDoctrine.Branch
                : TacticalDoctrineCatalog.All.FirstOrDefault()?.Id ?? "";

        ClearPolicyRows(_doctrineScreenCurrencies);
        CurrencyStrip(_doctrineScreenCurrencies, n);
        RebuildDoctrineBookList(n);

        ClearPolicyRows(_doctrineCentre);
        if (_doctrineView == "course")
        {
            StrategicDoctrinePage(n, _doctrineCentre);
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
        ShowDoctrinePreview(n, _doctrineSide);
    }

    /// <summary>The six books as a column: what each is, and how much of it is written down.</summary>
    private void RebuildDoctrineBookList(Nation n)
    {
        if (_doctrineBookList is null) return;
        ClearPolicyRows(_doctrineBookList);
        var memory = n.TacticalDoctrine;
        foreach (var branch in TacticalDoctrineCatalog.All)
        {
            var id = branch.Id;
            var inForce = string.Equals(memory.Branch, id, StringComparison.Ordinal);
            var written = inForce ? branch.Nodes.Count(node => TacticalDoctrines.Known(n, node.Id)) : 0;
            var button = ActionButton("", () => SelectDoctrineBook(id), 0, 46, inForce);
            button.Name = "ScreenBook_" + id;
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.ClipText = true;
            button.AutowrapMode = TextServer.AutowrapMode.Off;
            button.Text = Loc.Format("{0}\n{1} of {2} · paid for in {3}", Tr(branch.Name), written,
                branch.Nodes.Count, Loc.Get(TacticalDoctrines.ServiceName(branch.Experience)));
            button.AddThemeFontSizeOverride("font_size", 12);
            button.TooltipText = Ui.Tip(Tr(branch.Description));
            _doctrineBookList.AddChild(button);
        }
    }

    /// <summary>The toolbar: the two hands as buttons, for a layout where Alt is the window manager's.</summary>
    private void DoctrineTreeToolbar(Control rows)
    {
        var bar = new HBoxContainer { Name = "DoctrineTreeToolbar" };
        bar.AddThemeConstantOverride("separation", 6);
        rows.AddChild(bar);
        var further = ActionButton("−", () => NudgeDoctrineZoom(false), 46, 30);
        further.Name = "DoctrineZoomOut";
        further.TooltipText = Ui.Tip(Tr("Further away"));
        bar.AddChild(further);
        _doctrineZoomLabel = Title("100%", 13, Ui.Colour("text.body"));
        _doctrineZoomLabel.Name = "DoctrineZoomLabel";
        _doctrineZoomLabel.CustomMinimumSize = new Vector2(66, 0);
        _doctrineZoomLabel.HorizontalAlignment = HorizontalAlignment.Center;
        bar.AddChild(_doctrineZoomLabel);
        var closer = ActionButton("+", () => NudgeDoctrineZoom(true), 46, 30);
        closer.Name = "DoctrineZoomIn";
        closer.TooltipText = Ui.Tip(Tr("Closer"));
        bar.AddChild(closer);
        var reset = ActionButton("100%", ResetDoctrineZoom, 88, 30);
        reset.Name = "DoctrineZoomReset";
        bar.AddChild(reset);
        var hint = Title(TreeViewport.Hint, 12, Ui.Colour("text.caption"));
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        hint.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        bar.AddChild(hint);
    }

    private void NudgeDoctrineZoom(bool closer)
    {
        if (_doctrineScroll is null || _doctrineViewport is null || _doctrineTree is null)
        {
            GD.Print($"[zoom-nudge] plate missing: scroll={_doctrineScroll is not null} viewport={_doctrineViewport is not null} tree={_doctrineTree is not null}");
            return;
        }
        var was = _doctrineZoom;
        TreeViewport.Nudge(_doctrineScroll, _doctrineViewport, _doctrineTree, _doctrineExtent,
            ref _doctrineZoom, closer);
        GD.Print($"[zoom-nudge] closer={closer} {was} -> {_doctrineZoom} scale={_doctrineTree.Scale.X}");
        UpdateDoctrineZoomLabel();
    }

    private void ResetDoctrineZoom()
    {
        _doctrineZoom = 1f;
        if (_doctrineTree is not null)
        {
            _doctrineTree.Scale = Vector2.One;
            _doctrineTree.CustomMinimumSize = Vector2.Zero;
        }
        if (_doctrineViewport is not null) _doctrineViewport.CustomMinimumSize = _doctrineExtent;
        UpdateDoctrineZoomLabel();
    }

    private void UpdateDoctrineZoomLabel()
    {
        if (_doctrineZoomLabel is not null)
            _doctrineZoomLabel.Text = Loc.Format("{0}%", Math.Round(_doctrineZoom * 100));
    }
}
