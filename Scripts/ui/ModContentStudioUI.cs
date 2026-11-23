using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Guided Sovereign Brass Mod Content Studio: lets modders create valid, game-ready Events,
/// Decisions, Modifiers, Strategic Doctrines, Diseases, Disasters, and Localization entries
/// using structured forms, live JSON preview, instant schema validation, and automatic EN+RU
/// localization stub generation — without ever touching base game files.
/// </summary>
public partial class DealOrForceGame
{
    private ModStudioDocument _studioDoc;
    private VBoxContainer _studioFormRows;
    private VBoxContainer _studioValidationRows;
    private CodeEdit _studioJsonPreview;
    private CodeEdit _studioLocPreview;
    private Label _studioPathLabel;
    private Label _studioStatusLabel;
    private CheckBox _studioWriteLocToggle;
    private Button _studioSaveBtn;
    private readonly Dictionary<ModStudioCategory, Button> _studioCategoryButtons = new();

    private void OpenModContentStudio(ModStudioCategory initialCategory = ModStudioCategory.Event)
    {
        var rows = AuthorWindow(Loc.Get("studio.window_title"),
            reopen: () => OpenModContentStudio(_studioDoc?.Category ?? initialCategory));
        rows.AddChild(Title(Loc.Get("studio.window_subtitle"), 13, Ui.Colour("text.muted")));

        _studioDoc = ModStudioDocument.CreateFromTemplate(initialCategory);
        _studioCategoryButtons.Clear();

        var columns = new HBoxContainer { Name = "ModStudioColumns", SizeFlagsVertical = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 12);
        rows.AddChild(columns);

        // ---- Column 1: Guided Category Rail
        var catPanel = new PanelContainer
        {
            Name = "ModStudioCategoryColumn",
            CustomMinimumSize = new Vector2(210, 0),
            ClipContents = true
        };
        catPanel.AddThemeStyleboxOverride("panel",
            Ui.ChromeBox("surface.inset", 0.94f, "surface.line_faint", 0.80f, cornerRadius: 4, borderWidth: 1, padX: 10, padY: 10));
        columns.AddChild(catPanel);

        var catScroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            ClipContents = true
        };
        catPanel.AddChild(catScroll);

        var catPad = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true };
        catPad.AddThemeConstantOverride("margin_right", 14);
        catScroll.AddChild(catPad);

        var catCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        catCol.AddThemeConstantOverride("separation", 6);
        catPad.AddChild(catCol);

        catCol.AddChild(Title(Loc.Get("studio.categories_hdr"), 12, Ui.Colour("text.accent")));
        foreach (var template in ModStudioDocument.AllTemplates)
        {
            var captured = template.Category;
            var btn = ActionButton(Loc.Get(template.TitleKey), () => SelectStudioCategory(captured), 0, 36);
            btn.Name = "StudioCategory_" + captured;
            btn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            btn.TooltipText = Ui.Tip(Loc.Get(template.DescriptionKey));
            catCol.AddChild(btn);
            _studioCategoryButtons[captured] = btn;
        }

        catCol.AddChild(new HSeparator());
        var openRawBtn = ActionButton(Loc.Get("studio.open_raw_editor"), OpenContentEditor, 0, 34);
        openRawBtn.Name = "StudioOpenRawEditor";
        openRawBtn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        catCol.AddChild(openRawBtn);

        var openMapBtn = ActionButton(Loc.Get("Edit current world on map"), OpenScenarioMapEditor, 0, 34);
        openMapBtn.Name = "StudioOpenMapEditor";
        openMapBtn.Disabled = _campaign?.ProvinceMap is null;
        openMapBtn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        catCol.AddChild(openMapBtn);

        // ---- Column 2: Form Editor + Live JSON / Loc Preview Tabs
        var centerCol = new VBoxContainer
        {
            Name = "ModStudioCenterColumn",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1.6f,
            ClipContents = true
        };
        centerCol.AddThemeConstantOverride("separation", 8);
        columns.AddChild(centerCol);

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
        tabs.AddChild(_studioJsonPreview);
        tabs.SetTabTitle(1, Loc.Get("studio.tab_json"));

        _studioLocPreview = new CodeEdit
        {
            Name = "LiveLocPreview",
            Editable = false,
            GuttersDrawLineNumbers = true,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        tabs.AddChild(_studioLocPreview);
        tabs.SetTabTitle(2, Loc.Get("studio.tab_loc"));

        // ---- Column 3: Live Validation & Save Inspector
        var rightPanel = new PanelContainer
        {
            Name = "ModStudioInspectorColumn",
            CustomMinimumSize = new Vector2(260, 0),
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

        var validateBtn = ActionButton(Loc.Get("Validate"), RefreshStudioValidationAndPreview, 0, 34);
        validateBtn.Name = "ModStudioValidate";
        validateBtn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        rightCol.AddChild(validateBtn);

        _studioSaveBtn = ActionButton(Loc.Get("studio.save_to_mod"), SaveStudioDocument, 0, 38, true);
        _studioSaveBtn.Name = "ModStudioSave";
        _studioSaveBtn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        rightCol.AddChild(_studioSaveBtn);

        var openFolderBtn = ActionButton(Loc.Get("Open mod folder"), () => { ContentWorkbench.EnsureMod(AuthorRoot); OS.ShellOpen(AuthorRoot); }, 0, 32);
        openFolderBtn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        rightCol.AddChild(openFolderBtn);

        _studioStatusLabel = Title("", 12, Ui.Colour("text.caption"));
        _studioStatusLabel.Name = "ModStudioStatus";
        _studioStatusLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_studioStatusLabel);

        SelectStudioCategory(initialCategory);
    }

    private void SelectStudioCategory(ModStudioCategory category)
    {
        _studioDoc = ModStudioDocument.CreateFromTemplate(category);
        _authorDirty = false;
        foreach (var (cat, btn) in _studioCategoryButtons)
            StyleButton(btn, cat == category);
        RebuildStudioForm();
        RefreshStudioValidationAndPreview();
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
                        CustomMinimumSize = new Vector2(0, 32),
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
                        CustomMinimumSize = new Vector2(0, 32),
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
                        CustomMinimumSize = new Vector2(0, 32),
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

        if (_studioSaveBtn is not null) _studioSaveBtn.Disabled = !report.Valid;
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
            RefreshStudioValidationAndPreview();
        }
        catch (Exception ex)
        {
            _studioStatusLabel.Text = ex.Message;
        }
    }
}
