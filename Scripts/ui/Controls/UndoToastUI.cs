using System;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Controls;
using Godot;

namespace DealOrForce;

/// <summary>
/// Timeless Undo bar shown above the army bar after any Everywhere / Zero-budget action.
/// Has NO countdown timer — remains valid until the player triggers Ctrl+Z or issues their
/// next state-changing command (BatchActionUndo).
/// </summary>
public sealed partial class UndoToastControl : PanelContainer
{
    private readonly Label _label;
    private readonly Button _undoButton;
    private readonly Button _dismissButton;

    public event Action UndoRequested;
    public event Action DismissRequested;

    public UndoToastControl()
    {
        Name = "UndoToastControl";
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;

        var style = new StyleBoxFlat
        {
            BgColor = Ui.Colour("controls.undo_fill"),
            BorderColor = Ui.Colour("controls.undo_border"),
            ContentMarginLeft = 12,
            ContentMarginRight = 10,
            ContentMarginTop = 6,
            ContentMarginBottom = 6,
            ShadowColor = new Color(0, 0, 0, 0.35f),
            ShadowSize = 6
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(4);
        AddThemeStyleboxOverride("panel", style);

        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        row.AddThemeConstantOverride("separation", 10);
        AddChild(row);

        _label = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.Off,
            VerticalAlignment = VerticalAlignment.Center
        };
        _label.AddThemeFontSizeOverride("font_size", 13);
        _label.AddThemeColorOverride("font_color", Ui.Colour("text.bright"));
        row.AddChild(_label);

        var undoLabel = Platform.PlatformProfile.Current.IsTouchPrimary
            ? Loc.Get("Undo")
            : Loc.Get("Undo") + " (Ctrl+Z)";
        _undoButton = CreateButton(undoLabel, primary: true);
        _undoButton.Name = "UndoToastButton";
        _undoButton.Pressed += () => UndoRequested?.Invoke();
        row.AddChild(_undoButton);

        _dismissButton = CreateButton("×", primary: false);
        _dismissButton.Name = "UndoToastDismiss";
        _dismissButton.CustomMinimumSize = new Vector2(26, 26);
        _dismissButton.TooltipText = Ui.Tip(Loc.Get("Close"));
        _dismissButton.Pressed += () => DismissRequested?.Invoke();
        row.AddChild(_dismissButton);
    }

    public void RefreshFromCampaign(Campaign campaign)
    {
        var rec = campaign?.Player is null ? null : BatchActionUndo.Peek(campaign, campaign.Player.Id);
        if (rec is null)
        {
            Visible = false;
            return;
        }
        _label.Text = Loc.Format("Done in {0} region(s) ({1}) — Undo (Ctrl+Z)", rec.RegionCount, rec.SummaryText);
        _undoButton.Text = Platform.PlatformProfile.Current.IsTouchPrimary
            ? Loc.Get("Undo")
            : Loc.Get("Undo") + " (Ctrl+Z)";
        Visible = true;
        UpdateAnchorPosition();
    }

    public void UpdateAnchorPosition()
    {
        if (GetParent() is not Control parent) return;
        var vp = parent.GetViewportRect().Size;
        var min = GetCombinedMinimumSize();
        var w = MathF.Max(min.X, 340f);
        var h = MathF.Max(min.Y, 38f);
        Size = new Vector2(w, h);
        var bottomOffset = Ui.Metric("controls.undo_toast_bottom", 96);
        Position = new Vector2(MathF.Floor((vp.X - w) * 0.5f), MathF.Max(68f, vp.Y - bottomOffset - h));
    }

    private static Button CreateButton(string caption, bool primary)
    {
        var btn = new Button
        {
            Text = caption,
            CustomMinimumSize = new Vector2(primary ? 130 : 28, 26),
            FocusMode = FocusModeEnum.None
        };
        btn.AddThemeFontSizeOverride("font_size", 12);
        var box = new StyleBoxFlat
        {
            BgColor = primary ? Ui.Colour("button.primary_fill") : Ui.Colour("button.fill"),
            BorderColor = primary ? Ui.Colour("controls.focus_border") : Ui.Colour("button.border"),
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 3,
            ContentMarginBottom = 3
        };
        box.SetBorderWidthAll(1);
        box.SetCornerRadiusAll(3);
        btn.AddThemeStyleboxOverride("normal", box);
        var hover = (StyleBoxFlat)box.Duplicate();
        hover.BgColor = box.BgColor.Lightened(0.14f);
        btn.AddThemeStyleboxOverride("hover", hover);
        btn.AddThemeColorOverride("font_color", Ui.Colour("text.bright"));
        return btn;
    }
}
