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
    private (float Height, float Preferred, string Group, string Language, string Filter) _navigationLayoutKey;
    private bool _navigationLayoutApplied;
    private bool _navigationCategoryMode;
    private LineEdit _campaignNavigationSearch = null!;
    private string _campaignNavigationFilter = "";

    private void BuildCampaignNavigation()
    {
        var navigation = TexturedPanel(_hudLayer, PanelSurface.CommandStrip);
        _hudNavigationPanel = navigation;
        navigation.Resized += LayoutCampaignNavigation;
        navigation.Name = "CampaignNavigation";
        Ui.Place(navigation, "hud.navigation");
        var margin = Margin(navigation, (int)Ui.Metric("spacing.xs", 4), 6);
        var stack = new VBoxContainer();
        stack.AddThemeConstantOverride("separation", (int)Ui.Metric("shell.navigation_row_gap", 4));
        margin.AddChild(stack);

        _campaignNavigationCategory = new OptionButton
        {
            Name = "NavigationCategory", CustomMinimumSize = new(0, Ui.Metric("shell.navigation_category_height", 32)), ClipText = true,
            FitToLongestItem = false,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, Visible = false
        };
        _campaignNavigationCategory.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.navigation", 11));
        _campaignNavigationCategory.TooltipText = Ui.Tip(Tr("Choose a group of screens. Every destination remains available on small displays."));
        _campaignNavigationCategory.AccessibilityName = Tr("Navigation category");
        stack.AddChild(_campaignNavigationCategory);
        _campaignNavigationCategory.ItemSelected += index =>
        {
            if (index >= 0 && index < _campaignNavigationGroups.Count)
            { _campaignNavigationGroup = _campaignNavigationGroups[(int)index]; LayoutCampaignNavigation(); }
        };

        _campaignNavigationSearch = Placeholder(new LineEdit
        {
            Name = "NavigationSearch", SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new(0, 28), ClearButtonEnabled = true,
            TooltipText = Ui.Tip(Tr("Type to filter the navigation rail. Clearing it shows every destination again.")),
            AccessibilityName = Tr("Navigation search")
        }, "Search");
        _campaignNavigationSearch.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.body", 12));
        _campaignNavigationSearch.TextChanged += text =>
        {
            _campaignNavigationFilter = text.Trim();
            LayoutCampaignNavigation();
        };
        stack.AddChild(_campaignNavigationSearch);

        _campaignNavigationScroll = new ScrollContainer
        {
            Name = "CampaignNavigationScroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.ShowNever, FollowFocus = true,
            SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        stack.AddChild(_campaignNavigationScroll);
        _campaignNavigationRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _campaignNavigationRows.AddThemeConstantOverride("separation", (int)Ui.Metric("shell.navigation_row_gap", 4));
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
            button.SetMeta("navigation_short", item.Short);
            button.SetMeta("icon_above", true); button.ClipText = true;
            // The rail is ninety pixels and the caption is the whole destination: a cut
            // with no ellipsis reads as a word nobody wrote. Same cure as the outliner.
            button.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            button.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.navigation", 10));
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
        media.RemoveMeta("icon_caption_minimum"); media.CustomMinimumSize = new(0, Ui.Metric("shell.navigation_media_height", 42));
        media.ClipText = true; media.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.navigation", 10)); media.AddThemeConstantOverride("icon_max_width", 18);
        media.AccessibilityName = Tr("Sound and artwork");
        Tip(media, "Listen to the soundtrack, preview sound effects and inspect artwork without leaving the campaign.");
        var mediaGroup = _campaignNavigationGroups.LastOrDefault() ?? "OTHER SCREENS";
        media.SetMeta("navigation_short", "Sound and artwork");
        _screenButtons["MediaLibrary"] = media;
        _campaignNavigationEntries.Add((media, mediaGroup));
        _campaignNavigationScreenGroups["MediaLibrary"] = mediaGroup;
        _campaignNavigationRows.AddChild(media);
        UpdateScreenButtons();
    }

    private void LayoutCampaignNavigation()
    {
        if (_campaignNavigationRows is null || _hudNavigationPanel is null) return;
        var preferred = InterfaceSkin.Metric("shell.navigation_height", 50);
        var rowRoom = Math.Max(0, _hudNavigationPanel.Size.Y - _campaignNavigationSearch.GetCombinedMinimumSize().Y - 18);
        var key = (rowRoom, preferred, _campaignNavigationGroup, Loc.Language, _campaignNavigationFilter);
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
        var filtering = !string.IsNullOrWhiteSpace(_campaignNavigationFilter);
        // A search overrides categories: every matching destination stays on the rail,
        // grouped under its own heading, so a filtered list is still readable.
        _navigationCategoryMode = filtering ? false : plan.CategoryMode;
        _navigationLayoutKey = (rowRoom, preferred, _campaignNavigationGroup, Loc.Language, _campaignNavigationFilter); _navigationLayoutApplied = true;
        _campaignNavigationCategory.Visible = _navigationCategoryMode;
        _campaignNavigationSearch.Visible = true;
        var selectedCategory = _campaignNavigationGroups.IndexOf(_campaignNavigationGroup);
        if (selectedCategory >= 0) _campaignNavigationCategory.Select(selectedCategory);
        foreach (var caption in _campaignNavigationCaptions) caption.Visible = !_navigationCategoryMode && !filtering;
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

    private bool NavigationDestinationVisible(string screen)
    {
        if (!string.IsNullOrWhiteSpace(_campaignNavigationFilter))
        {
            if (!_screenButtons.TryGetValue(screen, out var button)) return false;
            var source = button.GetMeta("navigation_short", screen).AsString();
            return button.Text.Contains(_campaignNavigationFilter, StringComparison.OrdinalIgnoreCase)
                || source.Contains(_campaignNavigationFilter, StringComparison.OrdinalIgnoreCase)
                || Tr(source).Contains(_campaignNavigationFilter, StringComparison.OrdinalIgnoreCase)
                || screen.Contains(_campaignNavigationFilter, StringComparison.OrdinalIgnoreCase);
        }
        return !_navigationCategoryMode ||
            !_campaignNavigationScreenGroups.TryGetValue(screen, out var group) || group == _campaignNavigationGroup;
    }

    private bool NavigationDestinationEnabled(string screen) => MechanicFeatures.ScreenEnabled(_campaign, screen) &&
        (!string.Equals(screen, "Support", StringComparison.OrdinalIgnoreCase) || SupportTypeCatalog.All.Any(t => SupportCore.Enabled(_campaign, t)));
}
