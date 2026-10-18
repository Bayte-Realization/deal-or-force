using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The doctrine tree: six books of war, each drawn as what it is.
///
/// The page used to list every article of every book as a row. A row said what a book contains
/// and nothing about its shape - that the articles of a book are a tree, that the last ones wait
/// on the earlier ones, and that the ones at the end are the reason to have read the others.
/// Here the chosen book is laid out the way the book is: columns by what an article waits for,
/// lines drawn between them, and every price on the card the price is paid on - cost, days, the
/// crash-course price, what the article gives, and the article it waits for, by name.
///
/// It is read with the same hands as the technology graph (TreeViewport): Alt with the wheel
/// zooms, Ctrl with the wheel moves the view.
/// </summary>
public partial class DealOrForceGame
{
    /// <summary>A node of a page that must survive a refresh: a canvas is not a row.</summary>
    private const string KeepOnRefresh = "keep_on_policy_refresh";

    private const float DoctrineCardWidth = 336;
    private const float DoctrineCardHeight = 168;
    private const float DoctrineColumnGap = 84;
    private const float DoctrineRowGap = 24;

    private ScrollContainer _doctrineScroll;
    private TechnologyTreeCanvas _doctrineTree;
    private float _doctrineZoom = 1f;
    private Vector2 _doctrineExtent = new(360, 220);
    private string _doctrineBook = "";
    private string _doctrineStructure = "";
    private Control _doctrineViewport;
    private PanelContainer _doctrineWindow;
    private VBoxContainer _armyDoctrineRows;
    private TabContainer _armyCommandTabs;
    private int _armyDoctrineTab = -1;
    private int _armyDoctrineDay = -1;
    private readonly Dictionary<string, Control> _doctrineCards = new(StringComparer.Ordinal);
    private OptionButton _doctrineBookSelect;
    private readonly List<string> _doctrineBookIds = new();

    /// <summary>
    /// Both big trees are told about the wheel before the map is (DealOrForceGame._Input): a
    /// wheel over a graph is a wheel about the graph, and must not also march the camera.
    /// </summary>
    private bool HandleTreeViewportInput(InputEvent inputEvent)
    {
        // The middle button drags a tree, the way every map in the genre does. Checked first because
        // it only ever claims a middle-button event or a motion while one is held.
        if (_researchLayer is { Visible: true } && _researchScroll is not null &&
            TreeViewport.Drag("research", _researchScroll, _researchScroll, inputEvent))
        {
            GetViewport().SetInputAsHandled();
            return true;
        }
        if (_doctrineScroll is not null &&
            TreeViewport.Drag("doctrine", _doctrineScroll, _doctrineScroll, inputEvent))
        {
            GetViewport().SetInputAsHandled();
            return true;
        }
        if (_researchLayer is { Visible: true } && _researchGraph && _researchScroll is not null &&
            _researchViewport is not null && _researchTree is not null &&
            (TreeViewport.Wheel(_researchScroll, _researchScroll, _researchViewport, _researchTree,
                _researchExtent, ref _researchZoom, inputEvent) ||
             TreeViewport.Drag(_researchScroll, _researchScroll, inputEvent)))
        {
            GetViewport().SetInputAsHandled();
            UpdateResearchZoomLabel();
            return true;
        }
        // The book of war is on the wide screen and in the Army panel: what says "this wheel is
        // mine" is the plate itself being on screen, not the host it happens to be mounted in.
        if (_doctrineScroll is { Visible: true } && _doctrineScroll.IsVisibleInTree() && _doctrineViewport is not null &&
            _doctrineTree is not null &&
            (TreeViewport.Wheel(_doctrineScroll, _doctrineScroll, _doctrineViewport, _doctrineTree,
                _doctrineExtent, ref _doctrineZoom, inputEvent) ||
             TreeViewport.Drag(_doctrineScroll, _doctrineScroll, inputEvent)))
        {
            GetViewport().SetInputAsHandled();
            UpdateDoctrineZoomLabel();
            return true;
        }
        return false;
    }

    /// <summary>
    /// The tactical page: the four currencies are above it (DoctrineUI), the course below it, and
    /// this is the book itself - a row of books to choose between, the chosen book as a tree, and
    /// a line saying what the tree is worth right now.
    /// </summary>
    private void TacticalDoctrinePage(Nation n, VBoxContainer rows = null)
    {
        rows ??= _policyOptions;
        var memory = n.TacticalDoctrine;
        PolicyHeading(rows, "TACTICAL DOCTRINES");
        PolicyText(rows, Loc.Format(
            "Six books of war, each a tree of articles. Articles are paid for in the experience of their own service; a crash course costs more and lands at once. {0} and the army relearns what it thought it knew, until the new articles are drilled in.",
            Loc.Format("Abandoning a book costs {0} political points",
                Fmt.Number(_campaign.Defines.DoctrineRewritePoliticalCost))));

        if (_doctrineBook.Length == 0 || TacticalDoctrineCatalog.Find(_doctrineBook) is null)
            _doctrineBook = memory.Branch.Length > 0
                ? memory.Branch
                : TacticalDoctrineCatalog.All.FirstOrDefault()?.Id ?? "";
        var book = TacticalDoctrineCatalog.Find(_doctrineBook);

        // One full-width selector instead of six equally clipped "Doctrine..." tabs.
        // Keep the native popup and selected book alive across ordinary daily refreshes.
        if (_doctrineBookSelect is null)
        {
            _doctrineBookSelect = new OptionButton
            {
                Name = "DoctrineBookSelector", FitToLongestItem = false, ClipText = true,
                CustomMinimumSize = new Vector2(0, 36), SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            _doctrineBookSelect.SetMeta(KeepOnRefresh, true);
            _doctrineBookSelect.AccessibilityName = Tr("TACTICAL DOCTRINES");
            _doctrineBookSelect.ItemSelected += index =>
            {
                if (index >= 0 && index < _doctrineBookIds.Count) SelectDoctrineBook(_doctrineBookIds[(int)index]);
            };
        }
        if (_doctrineBookSelect.GetParent() is { } oldSelector) oldSelector.RemoveChild(_doctrineBookSelect);
        rows.AddChild(_doctrineBookSelect);
        var books = TacticalDoctrineCatalog.All.ToArray();
        if (!_doctrineBookIds.SequenceEqual(books.Select(branch => branch.Id)))
        {
            _doctrineBookSelect.Clear(); _doctrineBookIds.Clear();
            foreach (var branch in books)
            {
                _doctrineBookIds.Add(branch.Id);
                _doctrineBookSelect.AddItem("");
            }
        }
        var bookIndex = 0;
        foreach (var branch in TacticalDoctrineCatalog.All)
        {
            var id = branch.Id;
            var written = string.Equals(memory.Branch, id, StringComparison.Ordinal)
                ? branch.Nodes.Count(node => TacticalDoctrines.Known(n, node.Id)) : 0;
            _doctrineBookSelect.SetItemText(bookIndex, Loc.Format("{0} · {1}/{2}", Tr(branch.Name), written, branch.Nodes.Count));
            _doctrineBookSelect.GetPopup().SetItemTooltip(bookIndex, Ui.Tip(Tr(branch.Description) + "\n" +
                Loc.Format("Paid for in {0}.", Loc.Get(TacticalDoctrines.ServiceName(branch.Experience)))));
            if (_doctrineBook == id) _doctrineBookSelect.Select(bookIndex);
            bookIndex++;
        }

        // The tree itself lives on the book of war, a screen of its own (DoctrineScreenUI): a panel
        // this narrow can show a column of cards, not a tree. The tab keeps the summary, the four
        // currencies, the quick book switch and the door.
        var open = ActionButton(Loc.Get("Open the book of war"), OpenDoctrineScreen, 0, 36, true,
            "res://game/gfx/interface/icons/icon_laws.png");
        open.Name = "OpenDoctrineScreen";
        open.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        open.TooltipText = Ui.Tip(TreeViewport.Hint);
        rows.AddChild(open);
    }

    /// <summary>
    /// The tree plate: a window that scrolls, zooms and pans, held across redraws so the zoom, the
    /// scroll and the book being read survive a day change. One plate, two hosts - the Army tab in
    /// miniature and the book of war at full width (DoctrineScreenUI).
    /// </summary>
    private void DoctrineTreeWindow(Control rows)
    {
        if (_doctrineWindow is null)
        {
            _doctrineWindow = new PanelContainer
            {
                Name = "DoctrineTreeWindow",
                CustomMinimumSize = new Vector2(0, 470),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ExpandFill,
                // The cards are children of a Control, and a Control's children are drawn AND hit
                // tested outside its rectangle: without this the plate's cards quietly covered the
                // toolbar and the header, so every button above the tree ate the click meant for
                // it - measured, not guessed (the probe found a card's Body under the close button).
                ClipContents = true
            };
            _doctrineWindow.AddThemeStyleboxOverride("panel", WarInsetStyle(10, 8));
            AtlasSurfaceSkin.Attach(_doctrineWindow, AtlasSurfaceSkin.Level.Inset, "surface.inset");
            _doctrineWindow.SetMeta(KeepOnRefresh, true);
        }
        if (_doctrineWindow.GetParent() is { } plate) plate.RemoveChild(_doctrineWindow);
        rows.AddChild(_doctrineWindow);
        if (_doctrineScroll is null)
        {
            _doctrineScroll = new ScrollContainer
            {
                Name = "DoctrineTreeScroll",
                FollowFocus = true,
                SizeFlagsVertical = SizeFlags.ExpandFill
            };
            // The scaled node is a child of a plain Control, never of the ScrollContainer itself:
            // a container rewrites its children's transform on every layout pass.
            _doctrineViewport = new Control { Name = "DoctrineTreeViewport", MouseFilter = MouseFilterEnum.Pass };
            _doctrineScroll.AddChild(_doctrineViewport);
            _doctrineTree = new TechnologyTreeCanvas { Name = "DoctrineGraph", MouseFilter = MouseFilterEnum.Pass };
            _doctrineViewport.AddChild(_doctrineTree);
        }
        if (_doctrineScroll.GetParent() is { } holder) holder.RemoveChild(_doctrineScroll);
        _doctrineWindow.AddChild(_doctrineScroll);
    }

    /// <summary>Choosing a book is choosing which tree is on the table, so the view starts again.</summary>
    private void SelectDoctrineBook(string id)
    {
        _doctrineBook = id;
        _doctrineStructure = "";
        ResetDoctrineZoom();
        RedrawDoctrinePage();
    }

    /// <summary>
    /// Lays the book out and builds its cards. Called only when the book or its shape changes: a
    /// tree rebuilt every day would lose the zoom, the scroll and the card being read.
    /// </summary>
    private void RebuildDoctrineTree(Nation n, DoctrineBranch branch)
    {
        var structure = _doctrineBook + "|" + TacticalDoctrineCatalog.Revision + "|" + Loc.Language + "|" +
            string.Join(',', branch.Nodes.Select(node => node.Id));
        if (structure == _doctrineStructure && _doctrineCards.Count == branch.Nodes.Count) return;
        _doctrineStructure = structure;
        foreach (var (id, card) in _doctrineCards.ToArray())
        {
            if (branch.Find(id) is not null) continue;
            card.GetParent()?.RemoveChild(card);
            card.QueueFree();
            _doctrineCards.Remove(id);
        }

        var layout = DoctrineLayout(branch);
        var extent = new Vector2(360, 220);
        foreach (var node in branch.Nodes)
        {
            if (!_doctrineCards.TryGetValue(node.Id, out var card))
            {
                card = DoctrineNodeCard(n, branch, node);
                _doctrineTree.AddChild(card);
                _doctrineCards[node.Id] = card;
            }
            var (column, row) = layout[node.Id];
            var position = new Vector2(20 + column * (DoctrineCardWidth + DoctrineColumnGap),
                20 + row * (DoctrineCardHeight + DoctrineRowGap));
            var cardSize = new Vector2(DoctrineCardWidth, DoctrineCardHeight);
            card.Position = position;
            card.CustomMinimumSize = cardSize;
            card.Size = cardSize;
            card.SetDeferred(Control.PropertyName.Size, cardSize);
            extent.X = Math.Max(extent.X, position.X + DoctrineCardWidth + 24);
            extent.Y = Math.Max(extent.Y, position.Y + DoctrineCardHeight + 24);
        }

        // The lines run from the right edge of what an article waits for to the left edge of the
        // article itself, so a book reads in the direction it is learned.
        _doctrineTree.Edges.Clear();
        foreach (var node in branch.Nodes)
            foreach (var need in node.Requires)
                if (_doctrineCards.TryGetValue(need, out var from) &&
                    _doctrineCards.TryGetValue(node.Id, out var to))
                    _doctrineTree.Edges.Add((from.Position + new Vector2(DoctrineCardWidth, DoctrineCardHeight / 2f),
                        to.Position + new Vector2(0f, DoctrineCardHeight / 2f),
                        Ui.Colour(TacticalDoctrines.Known(n, node.Id) ? "state.positive" : "text.muted")));
        _doctrineExtent = extent;
        _doctrineTree.PivotOffset = Vector2.Zero;
        _doctrineTree.Scale = Vector2.One * _doctrineZoom;
        _doctrineViewport.CustomMinimumSize = extent * _doctrineZoom;
        _doctrineTree.QueueRedraw();
    }

    /// <summary>Column by how deep a prerequisite chain runs, row by order within the column.</summary>
    private static Dictionary<string, (int Column, int Row)> DoctrineLayout(DoctrineBranch branch)
    {
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var pass = 0; pass < branch.Nodes.Count; pass++)
        {
            var added = false;
            foreach (var node in branch.Nodes)
            {
                if (depth.ContainsKey(node.Id) || !node.Requires.All(depth.ContainsKey)) continue;
                depth[node.Id] = node.Requires.Count == 0 ? 0 : node.Requires.Max(id => depth[id]) + 1;
                added = true;
            }
            if (!added) break;
        }
        var rows = new Dictionary<int, int>();
        var layout = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        foreach (var node in branch.Nodes)
        {
            var column = depth.GetValueOrDefault(node.Id);
            var row = rows.GetValueOrDefault(column);
            rows[column] = row + 1;
            layout[node.Id] = (column, row);
        }
        return layout;
    }

    /// <summary>
    /// What the cards say today: which article is in force, which is being learned and how long
    /// is left, what stands in the way of the rest, and the two prices. Every line comes from the
    /// same refusal the command and the AI go through (TacticalDoctrines.Refusal), so a card
    /// cannot promise something pressing its own button will refuse.
    /// </summary>
    private void RefreshDoctrineTree(Nation n, DoctrineBranch branch)
    {
        var memory = n.TacticalDoctrine;
        var inForce = string.Equals(memory.Branch, branch.Id, StringComparison.Ordinal);
        var left = Math.Max(0, memory.LearningDoneDay - _campaign.ElapsedDays);
        foreach (var node in branch.Nodes)
        {
            if (!_doctrineCards.TryGetValue(node.Id, out var card)) continue;
            var body = card.GetNodeOrNull<Control>("Body");
            if (body is null) continue;
            var known = inForce && TacticalDoctrines.Known(n, node.Id);
            var learning = inForce && string.Equals(memory.LearningNode, node.Id, StringComparison.Ordinal);
            var missing = node.Requires.Where(need => !inForce || !TacticalDoctrines.Known(n, need))
                .Select(need => Tr(branch.Find(need)?.Name ?? need)).ToArray();

            var state = body.GetNodeOrNull<Label>("State_" + node.Id);
            if (state is not null)
            {
                state.Text = known ? Loc.Get("in force")
                    : learning ? Loc.Format("being learned · {0} days left", left)
                    : !inForce ? (memory.Branch.Length > 0
                        ? Loc.Get("another book is in force")
                        : Loc.Get("not started"))
                    : missing.Length > 0 ? Loc.Format("waits on {0}", string.Join(", ", missing))
                    : Loc.Get("ready to study");
                state.AddThemeColorOverride("font_color", Ui.Colour(known ? "state.positive"
                    : learning ? "text.accent" : missing.Length > 0 ? "state.warning" : "text.caption"));
            }
            var title = body.GetNodeOrNull<Label>("Title_" + node.Id);
            title?.AddThemeColorOverride("font_color",
                Ui.Colour(known ? "state.positive" : learning ? "text.accent" : "text.bright"));

            // A persistent canvas needs its dynamic text recomposed after a language change.
            var effects = body.GetNodeOrNull<Control>("Effects_" + node.Id);
            if (effects is not null)
            {
                var labels = effects.GetChildren().OfType<Label>().ToArray();
                var index = 0;
                foreach (var (key, value) in node.Modifiers)
                {
                    if (index >= labels.Length) break;
                    labels[index++].Text = DoctrineEffectText(key, value);
                }
            }

            var price = body.GetNodeOrNull<Label>("Price_" + node.Id);
            if (price is not null)
                price.Text = Loc.Format("{0} {1} · {2} days · or {3} at once",
                    Fmt.Number(node.Cost), Loc.Get(TacticalDoctrines.ServiceName(branch.Experience)),
                    node.Days, Fmt.Number(node.RushCost));

            var row = body.GetNodeOrNull<Control>("Buttons_" + node.Id);
            var study = row?.GetNodeOrNull<Button>("StudyArticle_" + node.Id);
            if (study is not null)
                DoctrineButton(study, n, branch, node, false, known, learning,
                    Loc.Format("Study · {0}", Fmt.Number(node.Cost)));
            var rush = row?.GetNodeOrNull<Button>("RushArticle_" + node.Id);
            if (rush is not null)
                DoctrineButton(rush, n, branch, node, true, known, learning,
                    Loc.Format("At once · {0}", Fmt.Number(node.RushCost)));
            var cardSize = new Vector2(DoctrineCardWidth, DoctrineCardHeight);
            card.CustomMinimumSize = cardSize;
            card.Size = cardSize;
        }
    }

    /// <summary>
    /// One of the two ways to start an article, told what the simulation would answer. An article
    /// already in force says so instead of offering itself again.
    /// </summary>
    private void DoctrineButton(Button button, Nation n, DoctrineBranch branch, DoctrineNode node,
        bool rush, bool known, bool learning, string label)
    {
        button.Text = known ? Loc.Get("in force") : learning ? Loc.Get("learning") : label;
        var refusal = TacticalDoctrines.Refusal(_campaign, n, branch.Id, node.Id, rush);
        button.Disabled = known || learning || refusal.Length > 0;
        button.TooltipText = Ui.Tip(refusal.Length > 0 ? refusal
            : rush
                ? Loc.Get("War is when the next article is worth more than the money of waiting.")
                : Loc.Format("The army reads this article for {0} days; it lands by itself once they pass.",
                    node.Days));
        StyleButton(button, !button.Disabled && rush);
    }

    /// <summary>
    /// One article as a card: what it is called, what it gives, what it waits for, what it costs,
    /// and the two doors. Everything a player needs to decide stands on the card, because a tree
    /// read from tooltips is a tree read one node at a time.
    /// </summary>
    private Control DoctrineNodeCard(Nation n, DoctrineBranch branch, DoctrineNode node)
    {
        var card = new PanelContainer { Name = "DoctrineNode_" + node.Id };
        card.AddThemeStyleboxOverride("panel", WarInsetStyle(12, 10));
        AtlasSurfaceSkin.Attach(card, AtlasSurfaceSkin.Level.Inset, "surface.inset");
        var body = new VBoxContainer { Name = "Body" };
        body.AddThemeConstantOverride("separation", 4);
        card.AddChild(body);

        var state = Fixed(Title("", 12, Ui.Colour("text.caption")));
        state.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        state.Name = "State_" + node.Id;
        body.AddChild(state);

        var title = new Label
        {
            Name = "Title_" + node.Id,
            Text = Tr(node.Name),
            AutowrapMode = TextServer.AutowrapMode.Off,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            CustomMinimumSize = new Vector2(140, 0),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        title.AddThemeFontSizeOverride("font_size", 16);
        title.AddThemeColorOverride("font_color", Ui.Colour("text.bright"));
        title.SetMeta(LocSourceMeta, node.Name);
        title.SetMeta(LocAppliedMeta, title.Text);
        title.ClipText = true;
        body.AddChild(title);

        // What it gives, written out: the numbers are the whole reason to read a book.
        var effects = new VBoxContainer { Name = "Effects_" + node.Id };
        effects.AddThemeConstantOverride("separation", 1);
        body.AddChild(effects);
        if (node.Modifiers.Count == 0)
        {
            var none = Fixed(Title(Loc.Get("No effects are written for this article."), 12,
                Ui.Colour("text.muted")));
            none.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            effects.AddChild(none);
        }
        foreach (var (key, value) in node.Modifiers)
        {
            var effectLabel = Fixed(Title(DoctrineEffectText(key, value), 12,
                Ui.Colour(NationalModifiers.IsGood(key, value) ? "state.positive" : "state.warning")));
            effectLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            effects.AddChild(effectLabel);
        }

        var price = Fixed(Title("", 12, Ui.Colour("text.body")));
        price.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        price.Name = "Price_" + node.Id;
        body.AddChild(price);

        var buttons = new HBoxContainer { Name = "Buttons_" + node.Id };
        buttons.AddThemeConstantOverride("separation", 6);
        body.AddChild(buttons);
        var study = ActionButton("", () => StudyArticle(n, branch, node, false), 0, 28, false,
            "res://game/gfx/interface/icons/icon_research.png");
        study.Name = "StudyArticle_" + node.Id;
        study.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        study.ClipText = true;
        // An expanded icon takes the whole button and the label is what a player needs to read:
        // "Study · 12.00" beside a small mark, not a mark filling the place the words were.
        study.ExpandIcon = false;
        study.AddThemeConstantOverride("icon_max_width", 18);
        buttons.AddChild(study);
        var rush = ActionButton("", () => StudyArticle(n, branch, node, true), 0, 28, false,
            "res://game/gfx/interface/icons/icon_war.png");
        rush.Name = "RushArticle_" + node.Id;
        rush.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        rush.ClipText = true;
        rush.ExpandIcon = false;
        rush.AddThemeConstantOverride("icon_max_width", 18);
        buttons.AddChild(rush);

        card.TooltipText = Ui.Tip(Tr(node.Name) + "\n" + Tr(node.Description));
        return card;
    }

    private static string DoctrineEffectText(string key, float value) =>
        Loc.Format("{0} {1} {2}", Tr(NationalModifiers.Of(key)?.Name ?? key),
            value >= 0f ? "+" : "−", Fmt.Percent(Math.Abs(value)));

    /// <summary>One order, through the one door: the command checks the refusal this page showed.</summary>
    private void StudyArticle(Nation n, DoctrineBranch branch, DoctrineNode node, bool rush)
    {
        _campaign.Commands.Enqueue(new StudyDoctrineCommand
            { IssuerId = n.Id, BranchId = branch.Id, NodeId = node.Id, Rush = rush });
        PlayStrategicCue("ui_select");
        RedrawDoctrinePage();
    }

    /// <summary>
    /// Redraws wherever the book is being read - the Army panel's Doctrines tab. The canvas and
    /// its row of books are held across the redraw (ClearPolicyRows keeps what is marked), so the
    /// tree keeps its zoom, its scroll and the book that was open.
    /// </summary>
    private void RedrawDoctrinePage()
    {
        // The book may be open on the wide screen, in the Army panel, or in neither: whichever is
        // showing is the one that is redrawn.
        if (_doctrineLayer is { Visible: true })
        {
            _doctrineScreenDay = -1;
            RefreshDoctrineScreen();
            return;
        }
        if (_armyDoctrineRows is not null && _armyDoctrineRows.IsInsideTree())
        {
            ClearPolicyRows(_armyDoctrineRows);
            ShowDoctrineWorkspace(_campaign.Player, _armyDoctrineRows);
        }
        else
        {
            ShowPolicyOptions();
        }
        RefreshPolicyPreview();
    }

    private void OpenArmyDoctrinePage()
    {
        if (_armyCommandTabs is null || _armyDoctrineTab < 0 || _campaign?.Player is null) return;
        _sidePanel.Visible = false;
        _openScreen = "Military";
        _armyCommandTabs.CurrentTab = _armyDoctrineTab;
        _armyDoctrineDay = -1;
        SetArmyPanelVisible(true);
        RefreshArmyPanel();
        UpdateScreenButtons();
    }
}
