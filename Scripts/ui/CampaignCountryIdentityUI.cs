using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    /// <summary>A campaign dialog is not the authoring workspace: it neither pauses the
    /// shared clock nor writes a mod. Every route out returns to the existing campaign.</summary>
    private (Window Window, VBoxContainer Rows, Action<Action> Leave) CampaignIdentityWindow(
        string nodeName, string title, Vector2I preferredSize, Func<bool> dirty)
    {
        var viewport = GetViewport().GetVisibleRect().Size;
        var window = new Window
        {
            Name = nodeName, Title = Tr(title), Exclusive = true,
            Size = new Vector2I(Math.Max(320, Math.Min(preferredSize.X, (int)viewport.X - 40)),
                Math.Max(300, Math.Min(preferredSize.Y, (int)viewport.Y - 64))),
            Theme = ScenarioEditorTheme.Create()
        };
        AddChild(window);
        void Leave(Action action)
        {
            if (!dirty()) { action(); return; }
            if (window.GetChildren().OfType<ConfirmationDialog>().Any(dialog => dialog.Visible)) return;
            var confirmation = new ConfirmationDialog
            {
                Name = "CampaignIdentityDiscard", Title = Tr("Unsaved changes"),
                DialogText = Tr("Discard unsaved changes?"), Exclusive = true
            };
            window.AddChild(confirmation);
            confirmation.Confirmed += () => { confirmation.QueueFree(); action(); };
            confirmation.Canceled += () => confirmation.QueueFree();
            confirmation.PopupCentered();
        }
        window.CloseRequested += () => Leave(() => window.QueueFree());
        window.WindowInput += input =>
        {
            if (input is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) return;
            window.SetInputAsHandled();
            Leave(() => window.QueueFree());
        };
        var surface = new PanelContainer();
        window.AddChild(surface);
        surface.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        surface.AddThemeStyleboxOverride("panel", ScenarioEditorTheme.Surface("shell.background", "shell.border", 0));
        var rows = PanelRows(surface, 16, 14, 8);
        var back = ActionButton("Back to campaign", () => Leave(() => window.QueueFree()), 220, 36);
        back.Name = "CampaignIdentityBack";
        rows.AddChild(back);
        return (window, rows, Leave);
    }

    private void OpenCampaignCountryIdentity()
    {
        if (_isLoading || _campaign?.Player is not { } nation ||
            !CampaignCosmetics.CanEdit(_campaign, nation, nation) ||
            GetChildren().OfType<Window>().Any(window => window.Visible &&
                window.Name.ToString() is "CampaignCountryIdentity" or "CampaignFlagEditor")) return;
        var dirty = false;
        var (window, shell, leave) = CampaignIdentityWindow("CampaignCountryIdentity", "Country appearance",
            new Vector2I(780, 660), () => dirty);
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        shell.AddChild(scroll);
        var rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(rows);
        rows.AddChild(Title("Changes belong to this campaign and its saves, not to a scenario or mod.", 14, Ui.Colour("text.muted")));
        rows.AddChild(Title(nation.DisplayName, 20, Ui.Colour("text.bright")));
        rows.AddChild(Title("Country name", 14, Ui.Colour("text.accent"), true));
        var name = new LineEdit
        {
            Name = "CampaignIdentityName", Text = nation.CustomName ?? "", MaxLength = 96,
            PlaceholderText = Tr("Leave blank for the automatic country name"),
            SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 36)
        };
        rows.AddChild(name);
        name.TextChanged += _ => dirty = true;
        rows.AddChild(Title("Map colour", 14, Ui.Colour("text.accent"), true));
        var colourRow = new HBoxContainer();
        rows.AddChild(colourRow);
        var colour = FlagDesign.Normalise(nation.Color) ?? "#888888";
        var resetColour = false;
        var picker = new ColorPickerButton
        {
            Name = "CampaignIdentityColour", Color = Color.FromHtml(colour), EditAlpha = false,
            CustomMinimumSize = new Vector2(100, 40)
        };
        foreach (var preset in FlagDesign.Palette) picker.GetPicker().AddPreset(Color.FromHtml(preset));
        var hex = new LineEdit
        {
            Name = "CampaignIdentityHex", Text = colour, MaxLength = 7, PlaceholderText = "#rrggbb",
            SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(180, 36)
        };
        colourRow.AddChild(picker); colourRow.AddChild(hex);
        var status = Title("", 14, Ui.Colour("text.caption"));
        status.Name = "CampaignIdentityStatus";
        var updatingColour = false;
        void SetColour(string value)
        {
            if (updatingColour) return;
            updatingColour = true;
            colour = value; hex.Text = value; picker.Color = Color.FromHtml(value);
            resetColour = false; dirty = true; status.Text = "";
            updatingColour = false;
        }
        picker.ColorChanged += value => SetColour("#" + value.ToHtml(false).ToLowerInvariant());
        hex.TextChanged += _ => { resetColour = false; dirty = true; };
        void ReadHex()
        {
            if (FlagDesign.Normalise(hex.Text) is { } value)
            { if (value != colour) SetColour(value); }
            else status.Text = Tr("Enter a map colour as #rrggbb.");
        }
        hex.TextSubmitted += _ => ReadHex();
        hex.FocusExited += ReadHex;
        var palette = new HFlowContainer { Name = "CampaignIdentityPalette" };
        rows.AddChild(palette);
        foreach (var preset in FlagDesign.Palette)
        {
            var swatch = ActionButton("", () => SetColour(preset), 34, 34);
            swatch.TooltipText = Ui.Tip(preset);
            var sample = new ColorRect
            {
                Color = Color.FromHtml(preset), MouseFilter = MouseFilterEnum.Ignore
            };
            swatch.AddChild(sample);
            sample.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            sample.OffsetLeft = sample.OffsetTop = 7; sample.OffsetRight = sample.OffsetBottom = -7;
            palette.AddChild(swatch);
        }
        rows.AddChild(Title("Preview and palette do not change the country until you apply.", 14, Ui.Colour("text.muted")));
        var actions = new HFlowContainer(); rows.AddChild(actions);
        var apply = ActionButton("Apply name & map colour", () =>
        {
            if (FlagDesign.Normalise(hex.Text) is not { } value)
            { status.Text = Tr("Enter a map colour as #rrggbb."); return; }
            if (SubmitCampaignCosmetics(nation, name.Text, nation.CustomFlag, null,
                resetColour ? "" : value == FlagDesign.Normalise(nation.Color) ? null : value, status)) dirty = false;
        }, 250, 40, true);
        apply.Name = "CampaignIdentityApply"; actions.AddChild(apply);
        var reset = ActionButton("Reset name & map colour", () =>
        {
            name.Text = "";
            SetColour(FlagDesign.Normalise(nation.MapColorBeforeOverride) ?? FlagDesign.Normalise(nation.Color) ?? "#888888");
            resetColour = true;
        }, 250, 40);
        reset.Name = "CampaignIdentityReset"; actions.AddChild(reset);
        var flag = ActionButton("Edit campaign flag…", () => leave(() =>
        {
            window.Hide(); window.QueueFree(); OpenCampaignFlagEditor(nation.Tag);
        }), 250, 40);
        flag.Name = "CampaignIdentityEditFlag"; actions.AddChild(flag);
        rows.AddChild(status);
        window.PopupCentered();
    }

    /// <summary>Use the same command/interceptor as every campaign action. A transported
    /// request is not a local success: retain the draft until the host has applied it.</summary>
    private bool SubmitCampaignCosmetics(Nation nation, string name, string flag, string design, string mapColour, Label status)
    {
        if (_isLoading || _campaign?.Player is not { } player || nation != player ||
            !CampaignCosmetics.CanEdit(_campaign, player, nation))
        { status.Text = Tr("You can only edit your own country's appearance here."); return false; }
        var command = new SetNationalCosmeticsCommand
        {
            IssuerId = player.Id, TargetNationId = nation.Id, CustomName = name,
            CustomFlag = flag, CustomFlagDesignJson = design, MapColor = mapColour
        };
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        if (command.Refusal.Length > 0) { status.Text = Tr(command.Refusal); return false; }
        // The queue also has a transport interceptor in the native proof. Inspect the actual
        // outcome as well as the network role, never fake a successful local application.
        var applied = nation.CustomName == (name ?? "").Trim() && nation.CustomFlag == (flag ?? "").Trim() &&
            (design is null || design.Length == 0 && nation.CustomFlagDesignJson.Length == 0 ||
                design.Length > 0 && CampaignCosmetics.TryNormaliseFlagDesign(design, nation.Id, out var expected, out _) &&
                nation.CustomFlagDesignJson == expected) &&
            (mapColour is null || mapColour.Length == 0 && nation.MapColorBeforeOverride.Length == 0 ||
                mapColour.Length > 0 && nation.Color == FlagDesign.Normalise(mapColour));
        status.Text = Tr(applied ? "Country appearance updated in this campaign." : "Appearance request sent to the host; waiting for confirmation.");
        if (applied)
        {
            Refresh();
            // The open country card is retained on pause; refreshing the HUD alone
            // leaves its old name/banner visible behind the editor and after closing it.
            if (MapSelectionShowing && (_mapSelectionNation ?? _selectedNation) is { } selected)
                ShowCountryCard(selected);
        }
        return applied;
    }

    private void OpenCampaignFlagEditor(string tag)
    {
        if (_isLoading || _campaign?.Player is not { } nation || nation.Tag != tag ||
            !CampaignCosmetics.CanEdit(_campaign, nation, nation)) return;
        var dirty = false;
        var (window, rows, _) = CampaignIdentityWindow("CampaignFlagEditor", "Campaign flag editor",
            new Vector2I(1280, 900), () => dirty);
        BuildFlagEditor(rows, tag, changed => dirty = changed, nation);
        window.PopupCentered();
    }
}
