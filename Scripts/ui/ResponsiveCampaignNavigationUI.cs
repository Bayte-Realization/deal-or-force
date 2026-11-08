using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private ScrollContainer _campaignNavigationScroll;
    private VBoxContainer _campaignNavigationRows;
    private readonly List<(Button Button, string Group)> _campaignNavigationEntries = new();
    private readonly List<Control> _campaignNavigationCaptions = new();
    private (float Height, float Preferred, string Language, string Filter) _navigationLayoutKey;
    private bool _navigationLayoutApplied;
    private LineEdit _campaignNavigationSearch = null!;
    private string _campaignNavigationFilter = "";

    private void BuildCampaignNavigation()
    {
        var navigation = TexturedPanel(_hudLayer, PanelSurface.CommandStrip);
        _hudNavigationPanel = navigation;
        navigation.Resized += LayoutCampaignNavigation;
        navigation.Resized += LayoutCampaignDossier;
        navigation.Resized += LayoutMapSelectionCards;
        navigation.Name = "CampaignNavigation";
        ApplyHudFrame(navigation);
        Ui.Place(navigation, "hud.navigation");
        var margin = Margin(navigation, (int)Ui.Metric("spacing.xs", 4), 6);
        var stack = new VBoxContainer();
        stack.AddThemeConstantOverride("separation", 4); margin.AddChild(stack);
        _campaignNavigationSearch = Placeholder(new LineEdit
        {
            Name = "NavigationSearch", SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new(0, 28), ClearButtonEnabled = true,
            TooltipText = Ui.Tip(Tr("Type to filter the navigation rail. Clearing it shows every destination again.")),
            AccessibilityName = Tr("Navigation search")
        }, "Search");
        _campaignNavigationSearch.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.body", 12));
        _campaignNavigationSearch.TextChanged += text =>
        { _campaignNavigationFilter = text.Trim(); LayoutCampaignNavigation(); };
        stack.AddChild(_campaignNavigationSearch);
        _campaignNavigationScroll = new ScrollContainer
        {
            Name = "CampaignNavigationScroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto, FollowFocus = true,
            SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        stack.AddChild(_campaignNavigationScroll);
        _campaignNavigationRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _campaignNavigationRows.AddThemeConstantOverride("separation", 4);
        _campaignNavigationScroll.AddChild(_campaignNavigationRows);
        _screenButtons.Clear(); _campaignNavigationEntries.Clear(); _campaignNavigationCaptions.Clear();
        var lastGroup = "";
        foreach (var item in CampaignUiCatalog.NavigationItems)
        {
            var group = string.IsNullOrWhiteSpace(item.Group) ? "OTHER SCREENS" : item.Group;
            if (group != lastGroup)
            {
                lastGroup = group;
                var groupFrame = new PanelContainer { Name = "NavGroup" + group.Replace(" ", "") };
                var rule = new StyleBoxFlat { BgColor = Ui.Colour("surface.inset", .7f),
                    BorderColor = Ui.Colour("text.accent", .5f),
                    BorderWidthTop = 1, ContentMarginTop = 6, ContentMarginBottom = 4 };
                groupFrame.AddThemeStyleboxOverride("panel", rule);
                var caption = Fixed(Title(group, 10, Ui.Colour("text.accent")));
                caption.HorizontalAlignment = HorizontalAlignment.Center;
                caption.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                caption.ClipText = false;
                caption.CustomMinimumSize = new(0, 20);
                caption.SetMeta(LocUpperMeta, true); caption.Text = caption.Text.ToUpperInvariant();
                groupFrame.AddChild(caption);
                _campaignNavigationCaptions.Add(groupFrame); _campaignNavigationRows.AddChild(groupFrame);
            }
            var captured = item.Screen;
            var button = ActionButton(item.Short, () => ToggleCampaignScreen(captured), 0, 50, false,
                SpriteCatalog.TextureOf($"icon_{item.Icon}", $"res://game/gfx/interface/icons/icon_{item.Icon}.png"));
            button.Name = "Nav" + item.Screen.Replace(" ", "");
            button.SetMeta("navigation_short", item.Short); button.SetMeta("icon_above", true);
            button.SetMeta("atlas_nav_tile", true); button.SetMeta(LocUpperMeta, true);
            button.Text = button.Text.ToUpperInvariant(); button.SetMeta(LocAppliedMeta, button.Text);
            button.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.navigation", 11));
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.RemoveMeta("icon_caption_minimum");
            StyleCampaignNavigationButton(button, false);
            Tip(button, Tr(item.Label) + "\n" + Tr(item.Help)); button.AccessibilityName = Tr(item.Label);
            _screenButtons[captured] = button; _campaignNavigationEntries.Add((button, group));
            _campaignNavigationRows.AddChild(button);
        }
        UpdateScreenButtons();
    }

    private void LayoutCampaignNavigation()
    {
        if (_campaignNavigationRows is null || _hudNavigationPanel is null) return;
        var preferred = Math.Clamp(InterfaceSkin.Metric("shell.navigation_height", 50), 42, 96);
        var room = Math.Max(0, _hudNavigationPanel.Size.Y - _campaignNavigationSearch.GetCombinedMinimumSize().Y - 22);
        var key = (room, preferred, Loc.Language, _campaignNavigationFilter);
        if (_navigationLayoutApplied && _navigationLayoutKey == key) return;
        _navigationLayoutKey = key; _navigationLayoutApplied = true;
        // All destinations remain in one rail. On short screens it scrolls instead of
        // silently changing to a category filter or shrinking buttons into tiny text.
        var buttonHeight = Math.Clamp((room - _campaignNavigationCaptions.Count * 30 -
            Math.Max(0, _campaignNavigationEntries.Count + _campaignNavigationCaptions.Count - 1) * 4) /
            Math.Max(1, _campaignNavigationEntries.Count), 42, preferred);
        foreach (var caption in _campaignNavigationCaptions) caption.Visible = _campaignNavigationFilter.Length == 0;
        foreach (var entry in _campaignNavigationEntries)
        {
            var screen = _screenButtons.First(pair => pair.Value == entry.Button).Key;
            entry.Button.Visible = NavigationDestinationVisible(screen) && NavigationDestinationEnabled(screen);
            entry.Button.CustomMinimumSize = new(0, buttonHeight);
            entry.Button.SetMeta("icon_above", true);
            entry.Button.AddThemeConstantOverride("icon_max_width", (int)Math.Clamp(buttonHeight * .44f, 12, 23));
            StyleCampaignNavigationButton(entry.Button, entry.Button.HasMeta("skin_button_primary") && entry.Button.GetMeta("skin_button_primary").AsBool());
        }
        UpdateScreenButtons();
    }

    private bool NavigationDestinationVisible(string screen)
    {
        if (string.IsNullOrWhiteSpace(_campaignNavigationFilter)) return true;
        if (!_screenButtons.TryGetValue(screen, out var button)) return false;
        var source = button.GetMeta("navigation_short", screen).AsString();
        return button.Text.Contains(_campaignNavigationFilter, StringComparison.OrdinalIgnoreCase)
            || source.Contains(_campaignNavigationFilter, StringComparison.OrdinalIgnoreCase)
            || Tr(source).Contains(_campaignNavigationFilter, StringComparison.OrdinalIgnoreCase)
            || screen.Contains(_campaignNavigationFilter, StringComparison.OrdinalIgnoreCase);
    }

    private bool NavigationDestinationEnabled(string screen) => MechanicFeatures.ScreenEnabled(_campaign, screen) &&
        (!string.Equals(screen, "Support", StringComparison.OrdinalIgnoreCase) || SupportTypeCatalog.All.Any(t => SupportCore.Enabled(_campaign, t)));
}
