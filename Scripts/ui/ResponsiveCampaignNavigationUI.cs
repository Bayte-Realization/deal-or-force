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
    private OptionButton _campaignNavigationCategory;
    private readonly List<string> _campaignNavigationGroups = new();
    private readonly List<(Button Button, string Group)> _campaignNavigationEntries = new();
    private readonly Dictionary<string, string> _campaignNavigationScreenGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Label> _campaignNavigationCaptions = new();
    private string _campaignNavigationGroup = "";
    private (float Height, float Preferred, string Group, string Language) _navigationLayoutKey;
    private bool _navigationLayoutApplied;
    private bool _navigationCategoryMode;

    private void BuildCampaignNavigation()
    {
        var navigation = TexturedPanel(_hudLayer, PanelSurface.CommandStrip);
        _hudNavigationPanel = navigation;
        navigation.Resized += LayoutCampaignNavigation;
        navigation.Name = "CampaignNavigation";
        Ui.Place(navigation, "hud.navigation");
        var margin = Margin(navigation, 4, 6);
        var stack = new VBoxContainer(); stack.AddThemeConstantOverride("separation", 4); margin.AddChild(stack);

        _campaignNavigationCategory = new OptionButton
        {
            Name = "NavigationCategory", CustomMinimumSize = new(0, 32), ClipText = true,
            FitToLongestItem = false,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, Visible = false
        };
        _campaignNavigationCategory.AddThemeFontSizeOverride("font_size", 11);
        _campaignNavigationCategory.TooltipText = Ui.Tip(Tr("Choose a group of screens. Every destination remains available on small displays."));
        _campaignNavigationCategory.AccessibilityName = Tr("Navigation category");
        stack.AddChild(_campaignNavigationCategory);
        _campaignNavigationCategory.ItemSelected += index =>
        {
            if (index >= 0 && index < _campaignNavigationGroups.Count)
            { _campaignNavigationGroup = _campaignNavigationGroups[(int)index]; LayoutCampaignNavigation(); }
        };

        _campaignNavigationScroll = new ScrollContainer
        {
            Name = "CampaignNavigationScroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.ShowNever, FollowFocus = true,
            SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        stack.AddChild(_campaignNavigationScroll);
        _campaignNavigationRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _campaignNavigationRows.AddThemeConstantOverride("separation", 4);
        _campaignNavigationScroll.AddChild(_campaignNavigationRows);
        _screenButtons.Clear(); _campaignNavigationGroups.Clear(); _campaignNavigationEntries.Clear(); _campaignNavigationCaptions.Clear();
        _campaignNavigationScreenGroups.Clear();

        var lastGroup = "";
        foreach (var item in CampaignUiCatalog.NavigationItems)
        {
            var group = string.IsNullOrWhiteSpace(item.Group) ? "OTHER SCREENS" : item.Group;
            if (!_campaignNavigationGroups.Contains(group))
            {
                _campaignNavigationGroups.Add(group);
                _campaignNavigationCategory.AddItem(Tr(group));
            }
            if (group != lastGroup)
            {
                var caption = Fixed(Title(group, 9, Ui.Colour("text.caption")));
                caption.Name = "NavGroup" + group.Replace(" ", "");
                caption.HorizontalAlignment = HorizontalAlignment.Center;
                caption.SetMeta(LocUpperMeta, true); caption.Text = caption.Text.ToUpperInvariant();
                _campaignNavigationCaptions.Add(caption); _campaignNavigationRows.AddChild(caption); lastGroup = group;
            }
            var captured = item.Screen;
            var button = ActionButton(item.Short, () => ToggleCampaignScreen(captured), 0, 50, false,
                SpriteCatalog.TextureOf($"icon_{item.Icon}", $"res://game/gfx/interface/icons/icon_{item.Icon}.png"));
            button.Name = "Nav" + item.Screen.Replace(" ", "");
            button.SetMeta("icon_above", true); button.ClipText = true;
            button.AddThemeFontSizeOverride("font_size", 10);
            button.AddThemeConstantOverride("icon_max_width", 23);
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.RemoveMeta("icon_caption_minimum"); button.CustomMinimumSize = new(0, 50);
            Tip(button, Tr(item.Label) + "\n" + Tr(item.Help)); button.AccessibilityName = Tr(item.Label);
            _screenButtons[captured] = button; _campaignNavigationEntries.Add((button, group)); _campaignNavigationRows.AddChild(button);
            _campaignNavigationScreenGroups[captured] = group;
        }
        _campaignNavigationGroup = _campaignNavigationGroups.FirstOrDefault() ?? "";
        var media = ActionButton("Sound / Art", OpenMediaLibrary, 0, 42, false,
            SpriteCatalog.TextureOf("icon_ledger", "res://game/gfx/interface/icons/icon_ledger.png"));
        media.Name = "OpenMediaLibraryNavigation"; media.SetMeta("icon_above", true); StyleButton(media, false);
        media.RemoveMeta("icon_caption_minimum"); media.CustomMinimumSize = new(0, 42);
        media.ClipText = true; media.AddThemeFontSizeOverride("font_size", 10); media.AddThemeConstantOverride("icon_max_width", 18);
        media.AccessibilityName = Tr("Sound and artwork");
        Tip(media, "Listen to the soundtrack, preview sound effects and inspect artwork without leaving the campaign.");
        stack.AddChild(media);
        UpdateScreenButtons();
    }

    private void LayoutCampaignNavigation()
    {
        if (_campaignNavigationRows is null || _hudNavigationPanel is null) return;
        var preferred = InterfaceSkin.Metric("shell.navigation_height", 50);
        var rowRoom = Math.Max(0, _hudNavigationPanel.Size.Y - 64);
        var key = (rowRoom, preferred, _campaignNavigationGroup, Loc.Language);
        if (_navigationLayoutApplied && _navigationLayoutKey == key) return;
        var selectedCount = _campaignNavigationEntries.Count(entry => entry.Group == _campaignNavigationGroup);
        var plan = ResponsiveHudLayout.Navigation(rowRoom, _campaignNavigationEntries.Count,
            _campaignNavigationCaptions.Count, selectedCount, preferred);
        if (plan.CategoryMode && !_navigationCategoryMode)
        {
            var selected = _campaignNavigationEntries.FirstOrDefault(entry =>
                entry.Button.HasMeta("skin_button_primary") && entry.Button.GetMeta("skin_button_primary").AsBool());
            if (selected.Button is not null) _campaignNavigationGroup = selected.Group;
            plan = ResponsiveHudLayout.Navigation(rowRoom, _campaignNavigationEntries.Count,
                _campaignNavigationCaptions.Count, _campaignNavigationEntries.Count(entry => entry.Group == _campaignNavigationGroup), preferred);
        }
        _navigationCategoryMode = plan.CategoryMode;
        _navigationLayoutKey = (rowRoom, preferred, _campaignNavigationGroup, Loc.Language); _navigationLayoutApplied = true;
        _campaignNavigationCategory.Visible = plan.CategoryMode;
        var selectedCategory = _campaignNavigationGroups.IndexOf(_campaignNavigationGroup);
        if (selectedCategory >= 0) _campaignNavigationCategory.Select(selectedCategory);
        foreach (var caption in _campaignNavigationCaptions) caption.Visible = !plan.CategoryMode;
        foreach (var entry in _campaignNavigationEntries)
        {
            var screen = _screenButtons.First(pair => pair.Value == entry.Button).Key;
            entry.Button.Visible = NavigationDestinationVisible(screen) && NavigationDestinationEnabled(screen);
            entry.Button.CustomMinimumSize = new(0, plan.ButtonHeight);
            entry.Button.SetMeta("icon_above", plan.ButtonHeight >= 42);
            entry.Button.AddThemeConstantOverride("icon_max_width", (int)Math.Clamp(plan.ButtonHeight * .44f, 12, 23));
            StyleButton(entry.Button, entry.Button.HasMeta("skin_button_primary") && entry.Button.GetMeta("skin_button_primary").AsBool());
        }
        // Localisation can change while a campaign is open; native menu entries have no
        // label node for RefreshLocalisedText to visit, so refresh their captions explicitly.
        for (var index = 0; index < _campaignNavigationGroups.Count; index++)
            _campaignNavigationCategory.SetItemText(index, Tr(_campaignNavigationGroups[index]));
        UpdateScreenButtons();
    }

    private bool NavigationDestinationVisible(string screen) => !_navigationCategoryMode ||
        !_campaignNavigationScreenGroups.TryGetValue(screen, out var group) || group == _campaignNavigationGroup;

    private bool NavigationDestinationEnabled(string screen) => MechanicFeatures.ScreenEnabled(_campaign, screen) &&
        (!string.Equals(screen, "Support", StringComparison.OrdinalIgnoreCase) || SupportTypeCatalog.All.Any(t => SupportCore.Enabled(_campaign, t)));
}
