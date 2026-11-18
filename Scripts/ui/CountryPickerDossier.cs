using System;
using System.Collections.Generic;
using Godot;

namespace DealOrForce;

/// <summary>
/// Architectural Rev 3 dossier card for the country selection screen: natural-aspect
/// recessed flag (no shield, no gloss), Source Serif 4 heading with rank, compact leader
/// well, and a 2-column grid of sunken 2-line stat chips.
/// </summary>
internal sealed class CountryPickerDossier
{
    private const float FlagMaxWidth = 82f;
    private const float FlagMaxHeight = 52f;
    private const int MaxChips = 6;

    private readonly List<StatChip> _chips = new(MaxChips);

    internal VBoxContainer Root { get; }
    internal PanelContainer FlagPocket { get; }
    internal Label NameLabel { get; }
    internal Label TierLabel { get; }
    internal VBoxContainer LeaderBox { get; }
    internal PanelContainer LeaderWell { get; }
    internal Label IdentityLabel { get; }
    internal Label LocationLabel { get; }

    internal CountryPickerDossier()
    {
        Root = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        Root.AddThemeConstantOverride("separation", 8);

        var header = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        header.AddThemeConstantOverride("separation", 10);
        Root.AddChild(header);

        FlagPocket = MenuChromeTheme.CreateFlagPocket("KOR", null, FlagMaxWidth, FlagMaxHeight);
        FlagPocket.Name = "PickerFlagPocket";
        header.AddChild(FlagPocket);

        var titles = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
        };
        titles.AddThemeConstantOverride("separation", 2);
        header.AddChild(titles);

        NameLabel = new Label
        {
            Name = "PickerCountryName",
            Text = "South Korea",
            AutowrapMode = TextServer.AutowrapMode.Off,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        MenuChromeTheme.StyleExistingLabel(NameLabel, 22, MenuChromeTheme.Paper, serif: true);
        titles.AddChild(NameLabel);

        TierLabel = new Label
        {
            Name = "PickerCountryTier",
            Text = "KOR · № 1 · major power",
            AutowrapMode = TextServer.AutowrapMode.Off,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        MenuChromeTheme.StyleExistingLabel(TierLabel, 12, MenuChromeTheme.BrassHi, tabular: true);
        titles.AddChild(TierLabel);

        Root.AddChild(MenuChromeTheme.CreateEngravedRule("PickerDossierRule", 1f));

        LeaderWell = new PanelContainer
        {
            Name = "PickerLeaderWell",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        LeaderWell.AddThemeStyleboxOverride("panel", MenuChromeTheme.WellBox(9, 7));
        Root.AddChild(LeaderWell);

        LeaderBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        LeaderBox.AddThemeConstantOverride("separation", 4);
        LeaderWell.AddChild(LeaderBox);

        var grid = new GridContainer
        {
            Name = "PickerStatGrid",
            Columns = 2,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        grid.AddThemeConstantOverride("h_separation", 6);
        grid.AddThemeConstantOverride("v_separation", 6);
        Root.AddChild(grid);

        for (var i = 0; i < MaxChips; i++)
        {
            var chip = BuildChip();
            _chips.Add(chip);
            grid.AddChild(chip.Frame);
        }

        IdentityLabel = new Label
        {
            Name = "PickerIdentityNote",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Visible = false,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        MenuChromeTheme.StyleExistingLabel(IdentityLabel, 12, MenuChromeTheme.PaperDim);
        Root.AddChild(IdentityLabel);

        LocationLabel = new Label
        {
            Name = "PickerMapLocation",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        MenuChromeTheme.StyleExistingLabel(LocationLabel, 12, MenuChromeTheme.PaperFaint);
        Root.AddChild(LocationLabel);
    }

    internal void SetNationHeader(string displayName, string tierLine, string tag, Texture2D flagTexture)
    {
        NameLabel.Text = displayName;
        NameLabel.TooltipText = Ui.Tip(displayName);
        TierLabel.Text = tierLine;
        TierLabel.TooltipText = Ui.Tip(tierLine);
        MenuChromeTheme.UpdateFlagPocket(FlagPocket, tag, flagTexture, FlagMaxWidth, FlagMaxHeight);
    }

    internal void SetStats(IReadOnlyList<(string Caption, string Value)> items, string identityBrief = "")
    {
        for (var i = 0; i < _chips.Count; i++)
        {
            var chip = _chips[i];
            if (i < items.Count)
            {
                var (caption, value) = items[i];
                chip.Frame.Visible = true;
                chip.Caption.Text = caption.ToUpperInvariant();
                chip.Value.Text = value;
                var tip = $"{caption}: {value}";
                chip.Frame.TooltipText = Ui.Tip(tip);
                chip.Caption.TooltipText = chip.Frame.TooltipText;
                chip.Value.TooltipText = chip.Frame.TooltipText;
            }
            else
            {
                chip.Frame.Visible = false;
            }
        }

        if (!string.IsNullOrWhiteSpace(identityBrief))
        {
            IdentityLabel.Text = identityBrief;
            IdentityLabel.Visible = true;
        }
        else
        {
            IdentityLabel.Text = "";
            IdentityLabel.Visible = false;
        }
    }

    /// <summary>
    /// Harmonizes borrowed HUD child blocks (such as the compact executive header) into the
    /// picker's warm parchment-and-brass palette without duplicating their construction logic.
    /// </summary>
    internal static void WarmSubtree(Node root)
    {
        if (root is null) return;
        foreach (var child in root.GetChildren())
        {
            if (child is Label label)
            {
                var current = label.HasThemeColorOverride("font_color")
                    ? label.GetThemeColor("font_color")
                    : MenuChromeTheme.Paper;
                var target = current.B > current.R + 0.08f
                    ? (label.GetThemeFontSize("font_size") >= 14 ? MenuChromeTheme.Paper : MenuChromeTheme.BrassHi)
                    : current.R < 0.55f ? MenuChromeTheme.PaperFaint
                    : MenuChromeTheme.PaperDim;
                label.AddThemeColorOverride("font_color", target);
            }
            else if (child is PanelContainer panel)
            {
                if (panel.GetThemeStylebox("panel") is StyleBoxFlat flat && flat.BorderColor.B > flat.BorderColor.R + 0.08f)
                {
                    var warm = (StyleBoxFlat)flat.Duplicate();
                    warm.BgColor = MenuChromeTheme.WellTop;
                    warm.BorderColor = MenuChromeTheme.Brass;
                    panel.AddThemeStyleboxOverride("panel", warm);
                }
            }
            WarmSubtree(child);
        }
    }

    private static StatChip BuildChip()
    {
        var frame = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        frame.AddThemeStyleboxOverride("panel", MenuChromeTheme.WellBox(9, 6));

        var stack = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        stack.AddThemeConstantOverride("separation", 1);
        frame.AddChild(stack);

        var caption = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.Off,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        MenuChromeTheme.StyleExistingLabel(caption, 10, MenuChromeTheme.PaperFaint);
        stack.AddChild(caption);

        var value = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.Off,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        MenuChromeTheme.StyleExistingLabel(value, 13, MenuChromeTheme.Paper, tabular: true);
        stack.AddChild(value);

        return new StatChip(frame, caption, value);
    }

    private readonly record struct StatChip(PanelContainer Frame, Label Caption, Label Value);
}
