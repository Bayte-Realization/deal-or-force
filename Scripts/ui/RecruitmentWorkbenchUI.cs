using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private readonly Dictionary<string, VBoxContainer> _recruitmentPages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _recruitmentPageButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _recruitmentCardButtons = new(StringComparer.Ordinal);
    private LineEdit _recruitmentTemplateSearch;
    private Label _recruitmentNoTemplates, _recruitmentOrderTitle, _recruitmentOrderPrice, _recruitmentOrderStatus;

    private void BuildRecruitmentWorkbench()
    {
        _recruitmentLayer = FullLayer("Recruitment"); _hudLayer.AddChild(_recruitmentLayer);
        var shade = new ColorRect { Color = Ui.Colour("surface.overlay"), MouseFilter = MouseFilterEnum.Stop };
        _recruitmentLayer.AddChild(shade); shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var panel = TexturedPanel(_recruitmentLayer); panel.Name = "RecruitmentWorkbench";
        Ui.Place(panel, "modal.ledger");
        var rows = PanelRows(panel, 24, 18, 10);
        var production = ActionButton("Production & Lines", () => { _recruitmentLayer.Hide(); OpenScreen("Production"); }, 0, 32);
        var doctrines = ActionButton("Doctrines", () => { _recruitmentLayer.Hide(); OpenDoctrineScreen(); }, 0, 32);
        ScreenHeader(rows, "RECRUITMENT & MOBILISATION",
            "Choose a formation, review the bill, then place training on the map.", false,
            production, doctrines, ActionButton("×", () => _recruitmentLayer.Hide(), 36, 32));

        _recruitmentModeButtonsHost = new HBoxContainer();
        Tip(_recruitmentModeButtonsHost, "Posture applies to new army and naval orders, not to existing training batches or contractor contracts.");
        _recruitmentModeButtonsHost.AddThemeConstantOverride("separation", 8); rows.AddChild(_recruitmentModeButtonsHost);
        foreach (var (mode, caption, tip) in new[]
        {
            (MobilisationMode.Standard, "Standard training", "Full peacetime curriculum. Standard batch limit (up to 6 per order), no stability or regional unrest penalty."),
            (MobilisationMode.RapidCallUp, "Rapid call-up", "Accelerated wartime call-up: 2.5× faster deployment and dynamic batch ceiling based on barracks & population, at +35% treasury and +25% equipment draw."),
            (MobilisationMode.TotalMobilisation, "Emergency mobilisation", "All-out emergency mobilisation: deploys in days (15% training time) and unlocks massive single-order batches, but drains stockpiles heavily (+50%) and costs national stability & regional calm.")
        })
        {
            var selectedMode = mode;
            var button = ActionButton(caption, () => { _recruitmentMode = selectedMode; RefreshRecruitment(); }, 0, 34);
            button.Name = "RecruitmentMode" + mode; button.SetMeta("atlas_tab_button", true);
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill; Tip(button, tip);
            _recruitmentModeButtons[mode] = button; _recruitmentModeButtonsHost.AddChild(button);
        }

        var tabs = new HFlowContainer(); tabs.AddThemeConstantOverride("h_separation", 6); tabs.AddThemeConstantOverride("v_separation", 6);
        rows.AddChild(tabs);
        var pages = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        rows.AddChild(pages);
        foreach (var (id, caption) in new[] { ("army", "Land forces"), ("queue", "Training queue"),
                     ("depots", "Strategic depots"), ("navy", "Naval shipyards"), ("contractors", "Military contractors") })
        {
            var pageId = id;
            var tab = ActionButton(caption, () => ShowRecruitmentPage(pageId), 0, 32);
            tab.Name = "RecruitmentTab" + char.ToUpperInvariant(id[0]) + id[1..]; tab.SetMeta("atlas_tab_button", true);
            tabs.AddChild(tab); _recruitmentPageButtons[id] = tab;
            var page = new VBoxContainer { Name = "RecruitmentPage_" + id, Visible = false,
                SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
            pages.AddChild(page); _recruitmentPages[id] = page;
        }

        var columns = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 14); _recruitmentPages["army"].AddChild(columns);
        var catalogue = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1.05f };
        catalogue.AddThemeConstantOverride("separation", 8); columns.AddChild(catalogue);
        catalogue.AddChild(Title("FORMATION CATALOGUE", 12, Ui.Colour("text.caption")));
        _recruitmentTemplateSearch = Placeholder(new LineEdit { Name = "RecruitmentTemplateSearch", ClearButtonEnabled = true }, "Find a formation...");
        Tip(_recruitmentTemplateSearch, "Search formations by name or ID. Ctrl+F focuses search; Enter selects the first result.");
        _recruitmentTemplateSearch.TextChanged += _ => FilterRecruitmentTemplates();
        _recruitmentTemplateSearch.TextSubmitted += _ =>
        {
            var match = _recruitmentTemplates.FirstOrDefault(id => _recruitmentCardButtons.TryGetValue(id, out var b) && b.Visible);
            if (match is not null) { _recruitmentTemplate.Select(Array.IndexOf(_recruitmentTemplates, match)); RefreshRecruitment(); }
        };
        catalogue.AddChild(_recruitmentTemplateSearch);
        var catalogueRows = RecruitmentScroll(catalogue, "RecruitmentCatalogueScroll");
        // Retain the existing selector as the single selection index for map-return and mods,
        // without displaying a duplicate dropdown beside the same visible formation cards.
        _recruitmentTemplate = new OptionButton { Name = "RecruitmentTemplate", Visible = false };
        catalogueRows.AddChild(_recruitmentTemplate); _recruitmentTemplate.ItemSelected += _ => RefreshRecruitment();
        _recruitmentTemplateCards = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _recruitmentTemplateCards.AddThemeConstantOverride("h_separation", 8); _recruitmentTemplateCards.AddThemeConstantOverride("v_separation", 8);
        catalogueRows.AddChild(_recruitmentTemplateCards);
        _recruitmentNoTemplates = Title("No matching formations.", 14, Ui.Colour("text.muted")); catalogueRows.AddChild(_recruitmentNoTemplates);

        var order = new VBoxContainer { Name = "RecruitmentOrder", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        order.AddThemeConstantOverride("separation", 8); columns.AddChild(order);
        var document = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        document.AddThemeStyleboxOverride("panel", WarInsetStyle(14, 10)); order.AddChild(document);
        var documentRows = new VBoxContainer(); documentRows.AddThemeConstantOverride("separation", 8); document.AddChild(documentRows);
        _recruitmentOrderTitle = Title("", 20, Ui.Colour("text.bright")); _recruitmentOrderTitle.Name = "RecruitmentOrderTitle";
        _recruitmentOrderTitle.AutowrapMode = TextServer.AutowrapMode.WordSmart; documentRows.AddChild(_recruitmentOrderTitle);
        _recruitmentOrderPrice = Title("", 14, Ui.Colour("text.accent")); _recruitmentOrderPrice.Name = "RecruitmentOrderPrice";
        _recruitmentOrderPrice.AutowrapMode = TextServer.AutowrapMode.WordSmart; documentRows.AddChild(_recruitmentOrderPrice);
        var form = RecruitmentScroll(documentRows, "RecruitmentOrderScroll");
        form.AddChild(Title("TRAINING REGION", 12, Ui.Colour("text.caption")));
        _recruitmentRegion = new OptionButton { Name = "RecruitmentRegion", SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        form.AddChild(_recruitmentRegion); _recruitmentRegion.ItemSelected += _ => RefreshRecruitment();
        _recruitmentBatchHeading = Title("", 12, Ui.Colour("text.caption")); form.AddChild(_recruitmentBatchHeading);
        var quantity = new HBoxContainer(); quantity.AddThemeConstantOverride("separation", 8); form.AddChild(quantity);
        _recruitmentSlider = new HSlider { MinValue = 1, MaxValue = 6, Step = 1, Value = 1,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        quantity.AddChild(_recruitmentSlider);
        _recruitmentCount = new SpinBox { Name = "RecruitmentCount", MinValue = 1, MaxValue = 6, Step = 1, Value = 1, CustomMinimumSize = new Vector2(96, 36) };
        quantity.AddChild(_recruitmentCount);
        var quick = new HFlowContainer(); quick.AddThemeConstantOverride("h_separation", 6); quick.AddThemeConstantOverride("v_separation", 6); form.AddChild(quick);
        foreach (var increment in new[] { 1, 5, 10 })
        {
            var step = increment;
            quick.AddChild(ActionButton("+" + step, () => _recruitmentCount.Value = Math.Min(_recruitmentCount.MaxValue, _recruitmentCount.Value + step), 52, 32));
        }
        quick.AddChild(ActionButton("MAX", () => _recruitmentCount.Value = _recruitmentCount.MaxValue, 64, 32));
        _recruitmentSlider.ValueChanged += value =>
        {
            if (_updatingRecruitmentCount) return;
            _updatingRecruitmentCount = true; _recruitmentCount.Value = value; _updatingRecruitmentCount = false; RefreshRecruitment();
        };
        _recruitmentCount.ValueChanged += value =>
        {
            if (_updatingRecruitmentCount) return;
            _updatingRecruitmentCount = true; _recruitmentSlider.Value = value; _updatingRecruitmentCount = false; RefreshRecruitment();
        };
        SectionRule(form, "ORDER REVIEW");
        _recruitmentDetails = Title("", 14, Ui.Colour("text.body_soft")); _recruitmentDetails.Name = "RecruitmentOrderDetails";
        _recruitmentDetails.AutowrapMode = TextServer.AutowrapMode.WordSmart; form.AddChild(_recruitmentDetails);

        var footer = new VBoxContainer { Name = "RecruitmentOrderFooter" }; footer.AddThemeConstantOverride("separation", 6); order.AddChild(footer);
        _recruitmentOrderStatus = Title("", 14, Ui.Colour("text.body")); _recruitmentOrderStatus.Name = "RecruitmentOrderStatus";
        _recruitmentOrderStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart; footer.AddChild(_recruitmentOrderStatus);
        _recruitmentOnMap = ActionButton("Choose training region on map", () =>
            BeginRecruitmentPlacement(RecruitmentTemplateId(), (int)_recruitmentCount.Value, _recruitmentMode), 0, 38, true);
        _recruitmentOnMap.Name = "RecruitmentOnMap"; _recruitmentOnMap.ClipText = true;
        Tip(_recruitmentOnMap, "Keep this template, batch size and posture, then click an owned training region on the map."); footer.AddChild(_recruitmentOnMap);
        _recruitmentSubmit = ActionButton("Begin training", () =>
        {
            _campaign.Commands.Enqueue(new RaiseArmyCommand { IssuerId = _campaign.Player.Id,
                StateMapIndex = _recruitmentRegion.GetSelectedId(), TemplateId = RecruitmentTemplateId(),
                Divisions = (int)_recruitmentCount.Value, Mode = _recruitmentMode });
            _campaign.Commands.ApplyAll(_campaign); RefreshRecruitment(); Refresh();
        }, 0, 36);
        _recruitmentSubmit.Name = "RecruitmentSubmit"; footer.AddChild(_recruitmentSubmit);

        var queue = RecruitmentScroll(_recruitmentPages["queue"], "RecruitmentQueueScroll");
        queue.AddChild(Title("Occupation pauses training. Cancellation returns all recruits and up to 75% of the unspent procurement budget.", 12, Ui.Colour("text.caption")));
        _recruitmentQueue = new VBoxContainer(); _recruitmentQueue.AddThemeConstantOverride("separation", 8); queue.AddChild(_recruitmentQueue);
        _recruitmentStockpileHost = RecruitmentScroll(_recruitmentPages["depots"], "RecruitmentDepotScroll");
        SectionRule(_recruitmentStockpileHost, "STRATEGIC STOCKPILE DEPOTS & DAILY BURN");
        _recruitmentNavalHost = RecruitmentScroll(_recruitmentPages["navy"], "RecruitmentNavyScroll");
        AddContractorRows(RecruitmentScroll(_recruitmentPages["contractors"], "RecruitmentContractorScroll"));
        panel.Resized += FitRecruitmentWorkbench;
        _hudNavigationPanel.Resized += () => FitWorkspaceNavigationInset(_recruitmentLayer);
        _recruitmentTemplateCards.Resized += FitRecruitmentWorkbench;
    }

    private VBoxContainer RecruitmentScroll(Control parent, string name)
    {
        var scroll = new ScrollContainer { Name = name, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        parent.AddChild(scroll);
        var rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 8); scroll.AddChild(rows); return rows;
    }

    private void ShowRecruitmentPage(string page, bool refresh = true)
    {
        foreach (var (id, body) in _recruitmentPages) { body.Visible = id == page; StyleButton(_recruitmentPageButtons[id], id == page); }
        if (refresh) RefreshRecruitment();
    }

    private void FilterRecruitmentTemplates()
    {
        var query = _recruitmentTemplateSearch.Text.Trim();
        foreach (var (id, button) in _recruitmentCardButtons)
            button.Visible = query.Length == 0 || id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                Tr(UnitTypeCatalog.Get(id).Name).Contains(query, StringComparison.OrdinalIgnoreCase);
        _recruitmentNoTemplates.Visible = !_recruitmentCardButtons.Values.Any(button => button.Visible);
    }

    private void FitRecruitmentWorkbench()
    {
        if (_recruitmentLayer is not { Visible: true } || _recruitmentTemplateSearch is null) return;
        var pixels = (GetViewport().GetStretchTransform() * _recruitmentLayer.GetGlobalTransformWithCanvas()).Scale.Abs();
        var scale = MathF.Max(.01f, MathF.Min(pixels.X, pixels.Y));
        var preference = Math.Clamp(SettingsCatalog.Number("interface_scale"), .5f, 2.5f);
        Fit(_recruitmentLayer);
        var width = _recruitmentTemplateCards.Size.X;
        var cardWidth = width * scale >= Ui.Metric("recruitment.two_card_width_pixels", 440) ? (width - 8) * .5f : width;
        foreach (var card in _recruitmentCardButtons.Values)
            card.CustomMinimumSize = new Vector2(MathF.Max(100, MathF.Floor(cardWidth)), Ui.Metric("recruitment.card_height_pixels", 66) / scale);
        FitRecruitmentDepotReading();
        void Fit(Node node)
        {
            // Only retained depot values use their dedicated fitter, not the section heading.
            if (node is Label depot && (depot.Name.ToString().StartsWith("RecruitmentDepotName_", StringComparison.Ordinal) ||
                depot.Name.ToString().StartsWith("RecruitmentDepotValue_", StringComparison.Ordinal))) return;
            if (node is Control control && control is Label or Button or LineEdit or SpinBox)
            {
                if (!control.HasMeta("recruitment_base_font")) control.SetMeta("recruitment_base_font", control.GetThemeFontSize("font_size"));
                var authored = control.GetMeta("recruitment_base_font").AsInt32();
                var floor = Ui.Metric(authored >= 20 ? "recruitment.minimum_heading_pixels" :
                    authored <= 12 ? "recruitment.minimum_caption_pixels" : "recruitment.minimum_font_pixels",
                    authored >= 20 ? 20 : authored <= 12 ? 12 : 14);
                var size = DecisionReadability.FontSize(authored, scale, preference, Math.Clamp(floor, 8, 36));
                if (control.GetThemeFontSize("font_size") != size) control.AddThemeFontSizeOverride("font_size", size);
                if (control is Label label) label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                else
                {
                    if (!control.HasMeta("recruitment_base_height")) control.SetMeta("recruitment_base_height", control.CustomMinimumSize.Y);
                    var height = MathF.Max(control.GetMeta("recruitment_base_height").AsSingle(), Ui.Metric("recruitment.target_pixels", 32) * preference / scale);
                    if (MathF.Abs(control.CustomMinimumSize.Y - height) > .1f) control.CustomMinimumSize = new Vector2(control.CustomMinimumSize.X, height);
                }
            }
            foreach (var child in node.GetChildren()) Fit(child);
        }
    }

    private bool HandleRecruitmentWorkbenchShortcut(InputEventKey key)
    {
        if (_recruitmentLayer is not { Visible: true } || !_recruitmentTemplateSearch.IsVisibleInTree() ||
            _pauseMenuLayer is { Visible: true } || _settingsLayer is { Visible: true } || _handbookLayer is { Visible: true } ||
            _navigatorLayer is { Visible: true } || _developerConsoleLayer is { Visible: true }) return false;
        if (key.Keycode == Key.Escape && GetViewport().GuiGetFocusOwner() == _recruitmentTemplateSearch)
        { _recruitmentLayer.Hide(); return true; }
        if (!(key.CtrlPressed || key.MetaPressed) || key.AltPressed || Letter(key) != Key.F) return false;
        _recruitmentTemplateSearch.GrabFocus(); _recruitmentTemplateSearch.SelectAll(); return true;
    }
}
