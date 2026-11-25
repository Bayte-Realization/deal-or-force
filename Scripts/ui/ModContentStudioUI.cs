using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DealOrForce.Platform;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Guided Sovereign Brass Mod Content Studio: lets modders create, inspect, reload, and localize
/// 14 structured mod categories with live validation, ScriptedEffects/Trigger reference, and 1-click
/// switching across Country Editor, Flag Designer, Map Editor, and Raw Workbench.
/// </summary>
public partial class DealOrForceGame
{
    private ModStudioDocument _studioDoc;
    private VBoxContainer _studioFormRows;
    private VBoxContainer _studioValidationRows;
    private CodeEdit _studioJsonPreview;
    private CodeEdit _studioLocPreview;
    private CodeEdit _studioRefCode;
    private LineEdit _studioCategoryFilter;
    private LineEdit _studioRefFilter;
    private OptionButton _studioSavedSelect;
    private Button _studioLoadSavedBtn;
    private Label _studioPathLabel;
    private Label _studioStatusLabel;
    private CheckBox _studioWriteLocToggle;
    private Button _studioSaveBtn;
    private Button _studioSaveReloadBtn;
    private readonly Dictionary<ModStudioCategory, Button> _studioCategoryButtons = new();
    private readonly Dictionary<ModStudioSection, Label> _studioSectionHeaders = new();
    private IReadOnlyList<string> _studioSavedFiles = Array.Empty<string>();

    private static string StudioSectionKey(ModStudioSection section) => section switch
    {
        ModStudioSection.EventsAndDecisions => "studio.sec.events_decisions",
        ModStudioSection.EconomyAndTech => "studio.sec.economy_tech",
        ModStudioSection.MilitaryAndProjects => "studio.sec.military_projects",
        ModStudioSection.PoliticsAndBalance => "studio.sec.politics_balance",
        _ => "studio.categories_hdr"
    };

    private void OpenModContentStudio(ModStudioCategory initialCategory = ModStudioCategory.Event)
    {
        var touch = PlatformProfile.Current.IsTouchPrimary;
        var btnH = touch ? 44f : 34f;
        var rows = AuthorWindow(Loc.Get("studio.window_title"),
            reopen: () => OpenModContentStudio(_studioDoc?.Category ?? initialCategory));
        rows.AddChild(Title(Loc.Get("studio.window_subtitle"), 13, Ui.Colour("text.muted")));

        _studioDoc = ModStudioDocument.CreateFromTemplate(initialCategory);
        _studioCategoryButtons.Clear();
        _studioSectionHeaders.Clear();

        // ---- Top Tool Switcher Strip
        var toolStrip = new HFlowContainer { Name = "ModStudioToolStrip" };
        toolStrip.AddThemeConstantOverride("h_separation", 8);
        toolStrip.AddThemeConstantOverride("v_separation", 6);
        rows.AddChild(toolStrip);

        void AddToolSwitch(string nodeName, string labelKey, Action openTarget, bool disabled = false)
        {
            var btn = ActionButton(Loc.Get(labelKey), () => ConfirmAuthorExit(openTarget), 0, touch ? 42f : 32f);
            btn.Name = nodeName;
            btn.Disabled = disabled;
            toolStrip.AddChild(btn);
        }

        string CurrentTag() => (_selectedNation ?? _campaign?.Player)?.Tag ?? "";
        void ReopenSelf() => OpenModContentStudio(_studioDoc?.Category ?? initialCategory);
        AddToolSwitch("StudioToolCountry", "studio.tool.country_editor", () => OpenCountryEditor(CurrentTag(), ReopenSelf), _campaign is null);
        AddToolSwitch("StudioToolFlag", "studio.tool.flag_designer", () => OpenFlagEditor(CurrentTag(), ReopenSelf));
        AddToolSwitch("StudioToolMap", "studio.tool.map_editor", OpenScenarioMapEditor, _campaign?.ProvinceMap is null);
        AddToolSwitch("StudioToolLayout", "studio.tool.layout_editor", OpenVisualLayoutEditor);
        AddToolSwitch("StudioToolRaw", "studio.open_raw_editor", OpenContentEditor);

        var columns = new HBoxContainer { Name = "ModStudioColumns", SizeFlagsVertical = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 12);
        rows.AddChild(columns);

        // ---- Column 1: Guided Category Rail + Live Filter + Section Headers
        var catPanel = new PanelContainer
        {
            Name = "ModStudioCategoryColumn",
            CustomMinimumSize = new Vector2(228, 0),
            ClipContents = true
        };
        catPanel.AddThemeStyleboxOverride("panel",
            Ui.ChromeBox("surface.inset", 0.94f, "surface.line_faint", 0.80f, cornerRadius: 4, borderWidth: 1, padX: 10, padY: 10));
        columns.AddChild(catPanel);

        var catWrap = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        catWrap.AddThemeConstantOverride("separation", 6);
        catPanel.AddChild(catWrap);

        catWrap.AddChild(Title(Loc.Get("studio.categories_hdr"), 12, Ui.Colour("text.accent")));
        _studioCategoryFilter = new LineEdit
        {
            Name = "ModStudioCategoryFilter",
            PlaceholderText = Loc.Get("studio.filter_placeholder"),
            ClearButtonEnabled = true,
            CustomMinimumSize = new Vector2(0, touch ? 40f : 30f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        Ui.FocusInputIfDesktop(_studioCategoryFilter);
        _studioCategoryFilter.TextChanged += _ => ApplyStudioCategoryFilter();
        catWrap.AddChild(_studioCategoryFilter);

        var catScroll = new ScrollContainer
        {
            Name = "ModStudioCategoryScroll",
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            ClipContents = true
        };
        catWrap.AddChild(catScroll);

        var catPad = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        catPad.AddThemeConstantOverride("margin_right", 14);
        catScroll.AddChild(catPad);

        var catCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        catCol.AddThemeConstantOverride("separation", 5);
        catPad.AddChild(catCol);

        foreach (ModStudioSection section in Enum.GetValues(typeof(ModStudioSection)))
        {
            var secHdr = Title(Loc.Get(StudioSectionKey(section)), 11, Ui.Colour("text.accent"));
            secHdr.Name = "StudioSection_" + section;
            catCol.AddChild(secHdr);
            _studioSectionHeaders[section] = secHdr;

            foreach (var template in ModStudioDocument.AllTemplates.Where(t => t.Section == section))
            {
                var captured = template.Category;
                var btn = ActionButton(Loc.Get(template.TitleKey), () => SelectStudioCategory(captured), 0, btnH);
                btn.Name = "StudioCategory_" + captured;
                btn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                btn.TooltipText = Ui.Tip(Loc.Get(template.DescriptionKey));
                catCol.AddChild(btn);
                _studioCategoryButtons[captured] = btn;
            }
        }

        // ---- Column 2: Saved Selector + Form Editor + Preview / Scripted Reference Tabs
        var centerCol = new VBoxContainer
        {
            Name = "ModStudioCenterColumn",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1.65f,
            ClipContents = true
        };
        centerCol.AddThemeConstantOverride("separation", 6);
        columns.AddChild(centerCol);

        var savedBar = new HFlowContainer { Name = "ModStudioSavedBar" };
        savedBar.AddThemeConstantOverride("h_separation", 8);
        savedBar.AddThemeConstantOverride("v_separation", 6);
        centerCol.AddChild(savedBar);

        var savedLbl = Title(Loc.Get("studio.saved_entries_lbl"), 12, Ui.Colour("text.muted"));
        savedBar.AddChild(savedLbl);

        _studioSavedSelect = new OptionButton
        {
            Name = "ModStudioSavedSelect",
            CustomMinimumSize = new Vector2(210, touch ? 40f : 30f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        savedBar.AddChild(_studioSavedSelect);

        _studioLoadSavedBtn = ActionButton(Loc.Get("studio.load_saved_btn"), LoadSelectedStudioEntry, 0, touch ? 40f : 30f);
        _studioLoadSavedBtn.Name = "ModStudioLoadSaved";
        savedBar.AddChild(_studioLoadSavedBtn);

        var dupBtn = ActionButton(Loc.Get("studio.duplicate_btn"), DuplicateCurrentStudioEntry, 0, touch ? 40f : 30f);
        dupBtn.Name = "ModStudioDuplicate";
        savedBar.AddChild(dupBtn);

        _studioPathLabel = Title("", 12, Ui.Colour("text.accent"));
        _studioPathLabel.Name = "ModStudioTargetPath";
        _studioPathLabel.AddThemeFontOverride("font", PresentationFonts.Tabular());
        _studioPathLabel.AutowrapMode = TextServer.AutowrapMode.Off;
        _studioPathLabel.ClipText = true;
        _studioPathLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        centerCol.AddChild(_studioPathLabel);

        var tabs = new TabContainer { Name = "ModStudioTabs", SizeFlagsVertical = SizeFlags.ExpandFill, ClipContents = true };
        centerCol.AddChild(tabs);

        var formScroll = new ScrollContainer
        {
            Name = "GuidedForm",
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            ClipContents = true
        };
        tabs.AddChild(formScroll);
        tabs.SetTabTitle(0, Loc.Get("studio.tab_form"));

        var formPad = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        formPad.AddThemeConstantOverride("margin_right", 14);
        formScroll.AddChild(formPad);

        _studioFormRows = new VBoxContainer { Name = "ModStudioFormRows", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _studioFormRows.AddThemeConstantOverride("separation", 8);
        formPad.AddChild(_studioFormRows);

        _studioJsonPreview = new CodeEdit
        {
            Name = "LiveJsonPreview",
            Editable = false,
            GuttersDrawLineNumbers = true,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        _studioJsonPreview.AddThemeFontOverride("font", PresentationFonts.Tabular());
        tabs.AddChild(_studioJsonPreview);
        tabs.SetTabTitle(1, Loc.Get("studio.tab_json"));

        _studioLocPreview = new CodeEdit
        {
            Name = "LiveLocPreview",
            Editable = false,
            GuttersDrawLineNumbers = true,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        _studioLocPreview.AddThemeFontOverride("font", PresentationFonts.Tabular());
        tabs.AddChild(_studioLocPreview);
        tabs.SetTabTitle(2, Loc.Get("studio.tab_loc"));

        // 4th Tab: Scripted Reference (live ScriptedEffects.Vocabulary & ScriptedTrigger keys)
        var refTab = new VBoxContainer { Name = "ScriptedReference", SizeFlagsVertical = SizeFlags.ExpandFill };
        refTab.AddThemeConstantOverride("separation", 6);
        tabs.AddChild(refTab);
        tabs.SetTabTitle(3, Loc.Get("studio.tab_reference"));

        _studioRefFilter = new LineEdit
        {
            Name = "ModStudioReferenceFilter",
            PlaceholderText = Loc.Get("studio.ref_filter_placeholder"),
            ClearButtonEnabled = true,
            CustomMinimumSize = new Vector2(0, touch ? 40f : 30f),
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        Ui.FocusInputIfDesktop(_studioRefFilter);
        _studioRefFilter.TextChanged += _ => RefreshStudioScriptedReference();
        refTab.AddChild(_studioRefFilter);

        _studioRefCode = new CodeEdit
        {
            Name = "ModStudioReferenceCode",
            Editable = false,
            GuttersDrawLineNumbers = true,
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        _studioRefCode.AddThemeFontOverride("font", PresentationFonts.Tabular());
        refTab.AddChild(_studioRefCode);

        // ---- Column 3: Live Validation, Save & Hot-Reload Inspector
        var rightPanel = new PanelContainer
        {
            Name = "ModStudioInspectorColumn",
            CustomMinimumSize = new Vector2(265, 0),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 0.85f,
            ClipContents = true
        };
        rightPanel.AddThemeStyleboxOverride("panel",
            Ui.ChromeBox("surface.inset", 0.94f, "text.accent", 0.65f, cornerRadius: 4, borderWidth: 1, padX: 10, padY: 10, leftBorderWidth: 3));
        columns.AddChild(rightPanel);

        var rightCol = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, ClipContents = true };
        rightCol.AddThemeConstantOverride("separation", 8);
        rightPanel.AddChild(rightCol);

        rightCol.AddChild(Title(Loc.Get("studio.validation_hdr"), 13, Ui.Colour("text.accent")));

        var valScroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            ClipContents = true
        };
        rightCol.AddChild(valScroll);

        var valPad = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        valPad.AddThemeConstantOverride("margin_right", 14);
        valScroll.AddChild(valPad);

        _studioValidationRows = new VBoxContainer { Name = "ModStudioValidationRows", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _studioValidationRows.AddThemeConstantOverride("separation", 6);
        valPad.AddChild(_studioValidationRows);

        _studioWriteLocToggle = new CheckBox
        {
            Name = "ModStudioWriteLocStubs",
            Text = Loc.Get("studio.generate_loc_stubs"),
            ButtonPressed = true
        };
        rightCol.AddChild(_studioWriteLocToggle);

        var validateBtn = ActionButton(Loc.Get("Validate"), RefreshStudioValidationAndPreview, 0, touch ? 42f : 34f);
        validateBtn.Name = "ModStudioValidate";
        validateBtn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        rightCol.AddChild(validateBtn);

        _studioSaveBtn = ActionButton(Loc.Get("studio.save_to_mod"), SaveStudioDocument, 0, touch ? 44f : 36f);
        _studioSaveBtn.Name = "ModStudioSave";
        _studioSaveBtn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        rightCol.AddChild(_studioSaveBtn);

        _studioSaveReloadBtn = ActionButton(Loc.Get("studio.save_and_reload"), SaveAndReloadStudioCatalogs, 0, touch ? 46f : 38f, true);
        _studioSaveReloadBtn.Name = "ModStudioSaveAndReload";
        _studioSaveReloadBtn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        rightCol.AddChild(_studioSaveReloadBtn);

        var openFolderBtn = ActionButton(Loc.Get("Open mod folder"), () => { ContentWorkbench.EnsureMod(AuthorRoot); OS.ShellOpen(AuthorRoot); }, 0, touch ? 42f : 32f);
        openFolderBtn.Name = "ModStudioOpenFolder";
        openFolderBtn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        rightCol.AddChild(openFolderBtn);

        _studioStatusLabel = Title("", 12, Ui.Colour("text.caption"));
        _studioStatusLabel.Name = "ModStudioStatus";
        _studioStatusLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_studioStatusLabel);

        RefreshStudioScriptedReference();
        SelectStudioCategory(initialCategory);
    }

    private void ApplyStudioCategoryFilter()
    {
        var query = (_studioCategoryFilter?.Text ?? "").Trim();
        var visibleBySection = new Dictionary<ModStudioSection, int>();
        foreach (var tpl in ModStudioDocument.AllTemplates)
        {
            var title = Loc.Get(tpl.TitleKey);
            var matches = string.IsNullOrEmpty(query) ||
                          title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                          tpl.Category.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
                          tpl.Folder.Contains(query, StringComparison.OrdinalIgnoreCase);
            if (_studioCategoryButtons.TryGetValue(tpl.Category, out var btn))
                btn.Visible = matches;
            if (matches)
                visibleBySection[tpl.Section] = visibleBySection.GetValueOrDefault(tpl.Section) + 1;
        }
        foreach (var (section, hdr) in _studioSectionHeaders)
            hdr.Visible = visibleBySection.GetValueOrDefault(section) > 0;
    }

    private void SelectStudioCategory(ModStudioCategory category)
    {
        _studioDoc = ModStudioDocument.CreateFromTemplate(category);
        _authorDirty = false;
        foreach (var (cat, btn) in _studioCategoryButtons)
            StyleButton(btn, cat == category);
        RefreshSavedEntriesDropdown();
        RebuildStudioForm();
        RefreshStudioValidationAndPreview();
    }

    private void RefreshSavedEntriesDropdown()
    {
        if (_studioSavedSelect is null || _studioDoc is null) return;
        _studioSavedSelect.Clear();
        _studioSavedFiles = ModStudioDocument.ListSavedEntries(AuthorRoot, _studioDoc.Category);
        if (_studioSavedFiles.Count == 0)
        {
            _studioSavedSelect.AddItem(Loc.Get("studio.no_saved_entries"));
            _studioSavedSelect.Disabled = true;
            if (_studioLoadSavedBtn is not null) _studioLoadSavedBtn.Disabled = true;
            return;
        }
        _studioSavedSelect.Disabled = false;
        if (_studioLoadSavedBtn is not null) _studioLoadSavedBtn.Disabled = false;
        foreach (var file in _studioSavedFiles)
            _studioSavedSelect.AddItem(file);
        _studioSavedSelect.Select(0);
    }

    private void LoadSelectedStudioEntry()
    {
        if (_studioDoc is null || _studioSavedSelect is null || _studioSavedFiles.Count == 0) return;
        var idx = _studioSavedSelect.Selected;
        if (idx < 0 || idx >= _studioSavedFiles.Count) return;
        var file = _studioSavedFiles[idx];
        if (!ModStudioDocument.TryLoadFromMod(AuthorRoot, _studioDoc.Category, file, out var loaded, out var error))
        {
            _studioStatusLabel.Text = error;
            return;
        }
        _studioDoc = loaded;
        _authorDirty = false;
        RebuildStudioForm();
        RefreshStudioValidationAndPreview();
        _studioStatusLabel.Text = Loc.Format("studio.loaded_fmt", file);
    }

    private void DuplicateCurrentStudioEntry()
    {
        if (_studioDoc is null) return;
        var baseId = _studioDoc.GetField("id").Trim();
        var nextId = baseId.EndsWith("_copy", StringComparison.Ordinal) ? baseId + "_2" : baseId + "_copy";
        _studioDoc = _studioDoc.DuplicateAs(nextId);
        _authorDirty = true;
        RebuildStudioForm();
        RefreshStudioValidationAndPreview();
        _studioStatusLabel.Text = Loc.Format("studio.duplicated_fmt", _studioDoc.GetField("id"));
    }

    private void RebuildStudioForm()
    {
        if (_studioFormRows is null || _studioDoc is null) return;
        foreach (var child in _studioFormRows.GetChildren()) child.QueueFree();

        var tpl = _studioDoc.Template;
        var head = Title(Loc.Get(tpl.TitleKey), 16, Ui.Colour("text.bright"));
        _studioFormRows.AddChild(head);
        var desc = Title(Loc.Get(tpl.DescriptionKey), 12, Ui.Colour("text.body_soft"));
        desc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _studioFormRows.AddChild(desc);

        var presets = ModStudioDocument.GetPresets(_studioDoc.Category);
        if (presets.Count > 0)
        {
            var presetCard = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
            presetCard.AddThemeStyleboxOverride("panel",
                Ui.ChromeBox("surface.panel", 0.55f, "text.accent", 0.45f, cornerRadius: 3, borderWidth: 1, padX: 8, padY: 6));
            var presetRow = new HFlowContainer { Name = "ModStudioPresetBar" };
            presetRow.AddThemeConstantOverride("h_separation", 8);
            presetRow.AddThemeConstantOverride("v_separation", 4);
            presetCard.AddChild(presetRow);

            presetRow.AddChild(Title(Loc.Get("studio.presets_lbl"), 12, Ui.Colour("text.accent")));
            var presetOpt = new OptionButton
            {
                Name = "ModStudioPresetSelect",
                CustomMinimumSize = new Vector2(210, PlatformProfile.Current.IsTouchPrimary ? 38f : 30f),
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            presetOpt.AddItem(Loc.Get("studio.preset.none"));
            foreach (var p in presets) presetOpt.AddItem(Loc.Get(p.TitleKey));
            presetOpt.ItemSelected += idx =>
            {
                var pIdx = (int)idx - 1;
                if (pIdx < 0 || pIdx >= presets.Count) return;
                if (_studioDoc.ApplyPreset(presets[pIdx].Id))
                {
                    _authorDirty = true;
                    RebuildStudioForm();
                    RefreshStudioValidationAndPreview();
                }
            };
            presetRow.AddChild(presetOpt);
            _studioFormRows.AddChild(presetCard);
        }

        foreach (var field in tpl.Fields)
        {
            var capturedKey = field.Key;
            var card = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
            card.AddThemeStyleboxOverride("panel",
                Ui.ChromeBox("surface.panel", 0.55f, "surface.line_faint", 0.55f, cornerRadius: 3, borderWidth: 1, padX: 8, padY: 6));
            _studioFormRows.AddChild(card);

            var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            box.AddThemeConstantOverride("separation", 4);
            card.AddChild(box);

            var lblText = Loc.Get(field.LabelKey) + (field.Required ? " *" : "");
            var lbl = Title(lblText, 12, Ui.Colour("text.bright"));
            box.AddChild(lbl);

            switch (field.Kind)
            {
                case ModStudioFieldKind.Bool:
                {
                    var cb = new CheckBox
                    {
                        Name = "StudioField_" + field.Key,
                        Text = Loc.Get(field.LabelKey),
                        ButtonPressed = bool.TryParse(_studioDoc.GetField(field.Key), out var b) && b
                    };
                    cb.Toggled += on =>
                    {
                        _studioDoc.SetField(capturedKey, on ? "true" : "false");
                        _authorDirty = true;
                        RefreshStudioValidationAndPreview();
                    };
                    box.AddChild(cb);
                    break;
                }
                case ModStudioFieldKind.Choice when field.Choices is { Length: > 0 }:
                {
                    var opt = new OptionButton
                    {
                        Name = "StudioField_" + field.Key,
                        CustomMinimumSize = new Vector2(0, PlatformProfile.Current.IsTouchPrimary ? 40f : 32f),
                        SizeFlagsHorizontal = SizeFlags.ExpandFill
                    };
                    var cur = _studioDoc.GetField(field.Key);
                    var selectedIdx = 0;
                    for (var i = 0; i < field.Choices.Length; i++)
                    {
                        opt.AddItem(field.Choices[i]);
                        if (string.Equals(field.Choices[i], cur, StringComparison.OrdinalIgnoreCase)) selectedIdx = i;
                    }
                    opt.Select(selectedIdx);
                    opt.ItemSelected += idx =>
                    {
                        if (idx >= 0 && idx < field.Choices.Length)
                        {
                            _studioDoc.SetField(capturedKey, field.Choices[(int)idx]);
                            _authorDirty = true;
                            RefreshStudioValidationAndPreview();
                        }
                    };
                    box.AddChild(opt);
                    break;
                }
                case ModStudioFieldKind.Number or ModStudioFieldKind.Float:
                {
                    var spin = new SpinBox
                    {
                        Name = "StudioField_" + field.Key,
                        MinValue = field.Min,
                        MaxValue = field.Max,
                        Step = field.Kind == ModStudioFieldKind.Number ? 1.0 : 0.005,
                        AllowGreater = true,
                        AllowLesser = true,
                        CustomMinimumSize = new Vector2(0, PlatformProfile.Current.IsTouchPrimary ? 40f : 32f),
                        SizeFlagsHorizontal = SizeFlags.ExpandFill,
                        Value = double.TryParse(_studioDoc.GetField(field.Key), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0.0
                    };
                    spin.GetLineEdit()?.AddThemeFontOverride("font", PresentationFonts.Tabular());
                    spin.ValueChanged += val =>
                    {
                        _studioDoc.SetField(capturedKey, field.Kind == ModStudioFieldKind.Number
                            ? ((int)Math.Round(val)).ToString(CultureInfo.InvariantCulture)
                            : val.ToString("0.####", CultureInfo.InvariantCulture));
                        _authorDirty = true;
                        RefreshStudioValidationAndPreview();
                    };
                    box.AddChild(spin);
                    break;
                }
                default:
                {
                    var edit = new LineEdit
                    {
                        Name = "StudioField_" + field.Key,
                        Text = _studioDoc.GetField(field.Key),
                        CustomMinimumSize = new Vector2(0, PlatformProfile.Current.IsTouchPrimary ? 40f : 32f),
                        SizeFlagsHorizontal = SizeFlags.ExpandFill
                    };
                    Ui.FocusInputIfDesktop(edit);
                    edit.TextChanged += txt =>
                    {
                        _studioDoc.SetField(capturedKey, txt);
                        _authorDirty = true;
                        RefreshStudioValidationAndPreview();
                    };
                    box.AddChild(edit);
                    break;
                }
            }

            if (!string.IsNullOrEmpty(field.HelpKey))
            {
                var help = Title(Loc.Get(field.HelpKey), 11, Ui.Colour("text.caption"));
                help.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                box.AddChild(help);
            }
        }
    }

    private void RefreshStudioScriptedReference()
    {
        if (_studioRefCode is null) return;
        var filter = (_studioRefFilter?.Text ?? "").Trim();
        static bool Match(string text, string f) =>
            string.IsNullOrEmpty(f) || text.Contains(f, StringComparison.OrdinalIgnoreCase);

        var sb = new StringBuilder(2048);
        sb.AppendLine("=== SCRIPTED EFFECTS (ScriptedEffects.Vocabulary) ===");
        sb.AppendLine("Suffixes: (none)=add, _percent=*(1+v), _mult=*v, _set=assign");
        foreach (var (key, help) in ScriptedEffects.Vocabulary)
        {
            if (Match(key, filter) || Match(help, filter))
                sb.AppendLine($"  {key,-28}  {help}");
        }

        sb.AppendLine();
        sb.AppendLine("=== SCRIPTED TRIGGER DIRECTIVES & KEYS ===");
        var directives = new[]
        {
            "min_<key> / max_<key>   Numeric inclusive lower / upper threshold",
            "at_war (bool)           True when country is currently in any war",
            "is_movement (bool)      True for stateless movement / non-state actors",
            "tags / not_tags         Array of country ISO tags to allow / block",
            "continent               Continent name match",
            "capital_in_region       Capital belongs to named ScenarioState",
            "has_government          Government id from common/governments",
            "has_ideology            Ideology id from common/ideologies",
            "has_law                 Active law id from common/laws",
            "has_feature             Enabled feature flag id",
            "has_grand_project       Active or completed Grand Project id",
            "has_completed_grand_project  Completed Grand Project id",
            "has_modifier / has_tech / year"
        };
        foreach (var d in directives)
            if (Match(d, filter)) sb.AppendLine("  " + d);

        var readable = ScriptedValues.AllReadableKeys().OrderBy(k => k, StringComparer.Ordinal).Where(k => Match(k, filter)).ToArray();
        if (readable.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Readable trigger scalar keys:");
            sb.AppendLine("  " + string.Join(", ", readable));
        }

        var libEffects = ScriptLibrary.EffectNames.OrderBy(k => k, StringComparer.Ordinal).Where(k => Match(k, filter)).ToArray();
        var libTriggers = ScriptLibrary.TriggerNames.OrderBy(k => k, StringComparer.Ordinal).Where(k => Match(k, filter)).ToArray();
        if (libEffects.Length > 0 || libTriggers.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("=== SCRIPT LIBRARY MACROS (common/scripted_*) ===");
            if (libEffects.Length > 0) sb.AppendLine("  Effects:  " + string.Join(", ", libEffects));
            if (libTriggers.Length > 0) sb.AppendLine("  Triggers: " + string.Join(", ", libTriggers));
        }

        _studioRefCode.Text = sb.ToString();
    }

    private void RefreshStudioValidationAndPreview()
    {
        if (_studioDoc is null || _studioValidationRows is null) return;

        var report = _studioDoc.Validate();
        _studioPathLabel.Text = Loc.Format("studio.target_path_fmt", report.SuggestedPath);
        _studioJsonPreview.Text = _studioDoc.RenderJson();

        _studioDoc.RenderLocalizationStubs(out var enStub, out var ruStub);
        _studioLocPreview.Text = "// EN\n" + enStub + "\n// RU\n" + ruStub;

        foreach (var child in _studioValidationRows.GetChildren()) child.QueueFree();

        var badge = new PanelContainer { ClipContents = true };
        var badgeTok = report.Valid ? "state.positive" : "state.negative";
        badge.AddThemeStyleboxOverride("panel",
            Ui.ChromeBox(badgeTok, 0.18f, badgeTok, 0.85f, cornerRadius: 3, borderWidth: 1, padX: 8, padY: 6, leftBorderWidth: 3));
        var badgeLbl = Title(Loc.Get(report.Valid ? "studio.status_valid" : "studio.status_invalid"), 13, Ui.Colour(badgeTok));
        badgeLbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        badge.AddChild(badgeLbl);
        _studioValidationRows.AddChild(badge);

        if (!string.IsNullOrWhiteSpace(report.EngineValidationMessage))
        {
            var engLbl = Title(Loc.Get(report.EngineValidationMessage), 11, Ui.Colour("text.body_soft"));
            engLbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _studioValidationRows.AddChild(engLbl);
        }

        foreach (var issue in report.Issues)
        {
            var row = Title($"• [{issue.FieldKey}] {issue.Message}", 11,
                Ui.Colour(issue.IsError ? "state.negative" : "state.warning"));
            row.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _studioValidationRows.AddChild(row);
        }

        var canSave = report.Valid;
        if (_studioSaveBtn is not null) _studioSaveBtn.Disabled = !canSave;
        if (_studioSaveReloadBtn is not null) _studioSaveReloadBtn.Disabled = !canSave;
    }

    private void SaveStudioDocument()
    {
        if (_studioDoc is null) return;
        try
        {
            var written = _studioDoc.SaveToMod(AuthorRoot, _studioWriteLocToggle?.ButtonPressed ?? true);
            _authorDirty = false;
            _studioStatusLabel.Text = Loc.Format("studio.saved_fmt", written);
            PlayStrategicCue("ui_select");
            RefreshSavedEntriesDropdown();
            RefreshStudioValidationAndPreview();
        }
        catch (Exception ex)
        {
            _studioStatusLabel.Text = ex.Message;
        }
    }

    private void SaveAndReloadStudioCatalogs()
    {
        if (_studioDoc is null) return;
        try
        {
            var written = _studioDoc.SaveToMod(AuthorRoot, _studioWriteLocToggle?.ButtonPressed ?? true);
            _authorDirty = false;
            var roots = _modLoader?.Packages.Select(p => p.PhysicalRoot);
            var count = ModStudioDocument.HotReloadCatalogs(AuthorRoot, roots, _campaign, out var errors);
            if (_campaign is not null) RefreshLocalisedText();
            RefreshSavedEntriesDropdown();
            RefreshStudioScriptedReference();
            RefreshStudioValidationAndPreview();
            _studioStatusLabel.Text = errors.Count == 0
                ? Loc.Format("studio.reloaded_fmt", written, count)
                : Loc.Format("studio.reloaded_errors_fmt", written, errors.Count, errors[0]);
            PlayStrategicCue("ui_select");
        }
        catch (Exception ex)
        {
            _studioStatusLabel.Text = ex.Message;
        }
    }
}
