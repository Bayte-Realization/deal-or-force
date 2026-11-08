using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private float _diplomacyComfortPixels = -1, _diplomacyComfortPreference = -1;

    private void StyleDiplomacyCountrySelection(DiplomacyRow row)
    {
        var selected = row.Nation?.Id == _diplomacySelectedId;
        // Selection is a frame, not a tint over the country's actual flag.
        row.Select.Modulate = Colors.White;
        row.Select.AddThemeStyleboxOverride("normal", AtlasSurfaceSkin.NavTile(selected, 0));
        row.Select.AddThemeStyleboxOverride("hover", AtlasSurfaceSkin.NavTile(selected, 1));
        row.Select.AddThemeStyleboxOverride("pressed", AtlasSurfaceSkin.NavTile(selected, 2));
    }

    private void FitDiplomacyComfort(bool full = true)
    {
        if (_diplomacyLayer is not { Visible: true } || _diplomacyCountryColumn is null) return;
        var pixels = GetViewport().GetStretchTransform() * _diplomacyLayer.GetGlobalTransformWithCanvas();
        var scale = MathF.Max(.01f, MathF.Min(MathF.Abs(pixels.Scale.X), MathF.Abs(pixels.Scale.Y)));
        var preference = Math.Clamp(SettingsCatalog.Number("interface_scale"), .5f, 2.5f);
        var changed = MathF.Abs(scale - _diplomacyComfortPixels) > .001f ||
                      MathF.Abs(preference - _diplomacyComfortPreference) > .001f;
        _diplomacyComfortPixels = scale; _diplomacyComfortPreference = preference;
        var available = (_diplomacyCountryColumn.GetParent() as Control)?.Size.X ?? 0;
        if (available > 0)
        {
            _diplomacyCountryColumn.CustomMinimumSize = new Vector2(Math.Clamp(available * .30f, 360, 490), 0);
            _diplomacyActionColumn.CustomMinimumSize = new Vector2(Math.Clamp(available * .27f, 300, 450), 0);
        }
        if (full || changed) FitBranch(_diplomacyLayer);
        foreach (var row in _diplomacyRows)
        {
            if (row.Nation is null || !row.Select.Visible) continue;
            FitControl(row.Name); FitControl(row.Identity); FitControl(row.Stance); FitControl(row.Opinion);
            row.Select.CustomMinimumSize = new Vector2(0, MathF.Max(row.Select.CustomMinimumSize.Y,
                Ui.Metric("diplomacy.minimum_target_pixels", 32) / scale));
            row.Opinion.CustomMinimumSize = new Vector2(48 / scale, row.Opinion.CustomMinimumSize.Y);
            var flagHeight = MathF.Max(18, 18 / scale);
            if (row.Flag.GetChild(0) is TextureRect flag && flag.Texture is not null &&
                MathF.Abs(flag.CustomMinimumSize.Y - MathF.Round(flagHeight)) > .5f)
            {
                var size = new Vector2(MathF.Round(flagHeight * flag.Texture.GetWidth() / flag.Texture.GetHeight()), MathF.Round(flagHeight));
                flag.CustomMinimumSize = size;
                row.Flag.CustomMinimumSize = size + new Vector2(2, 2);
            }
            FitDiplomacyName(row);
        }

        // The warmed country pool can contain hundreds of flags and labels. Fit its four
        // retained labels directly above, not with a fresh whole-tree discovery per refresh.
        void FitBranch(Node node)
        {
            if (node == _diplomacyListRows) return;
            if (node is Control control) FitControl(control);
            foreach (var child in node.GetChildren()) FitBranch(child);
        }
        void FitControl(Control control)
        {
            if (control is not (Label or Button or LineEdit)) return;
            if (!control.HasMeta("negotiation_base_font"))
                control.SetMeta("negotiation_base_font", control.GetThemeFontSize("font_size"));
            var authored = control.GetMeta("negotiation_base_font").AsInt32();
            var floor = Ui.Metric(authored >= 20 ? "diplomacy.minimum_heading_pixels" :
                authored <= 12 ? "diplomacy.minimum_caption_pixels" : "diplomacy.minimum_font_pixels",
                authored >= 20 ? 20 : authored <= 12 ? 12 : 14);
            var font = DecisionReadability.FontSize(authored, scale, preference, floor);
            if (control.GetThemeFontSize("font_size") != font) control.AddThemeFontSizeOverride("font_size", font);
            if (control is not (Button or LineEdit)) return;
            if (!control.HasMeta("negotiation_base_height"))
                control.SetMeta("negotiation_base_height", control.CustomMinimumSize.Y);
            var height = MathF.Max(control.GetMeta("negotiation_base_height").AsSingle(),
                Ui.Metric("diplomacy.minimum_target_pixels", 32) * preference / scale);
            if (MathF.Abs(control.CustomMinimumSize.Y - height) > .1f)
                control.CustomMinimumSize = new Vector2(control.CustomMinimumSize.X, height);
        }
    }

    private void OpenFirstDiplomacySearchResult()
    {
        var first = _diplomacyRows.FirstOrDefault(row => row.Nation is not null && row.Select.IsVisibleInTree());
        if (first is null) return;
        SelectDiplomacyNation(first.Nation);
        first.Select.GrabFocus();
    }

    private bool HandleDiplomacySearchShortcut(InputEventKey key)
    {
        if (_diplomacyLayer is not { Visible: true } ||
            _handbookLayer is { Visible: true } || _pauseMenuLayer is { Visible: true } ||
            _settingsLayer is { Visible: true } || _saveLoadLayer is { Visible: true } ||
            _developerConsoleLayer is { Visible: true } || _subjectsModalLayer is { Visible: true } ||
            _peaceLayer is { Visible: true } || _navigatorLayer is { Visible: true }) return false;
        if (key.Keycode == Key.Escape &&
            (GetViewport().GuiGetFocusOwner() == _diplomacySearch || GetViewport().GuiGetFocusOwner() == _diplomacyActionSearch))
        { CloseDiplomacyScreen(); return true; }
        if (!(key.CtrlPressed || key.MetaPressed) || key.AltPressed || Letter(key) != Key.F) return false;
        var search = key.ShiftPressed ? _diplomacyActionSearch : _diplomacySearch;
        if (search is null || !search.IsVisibleInTree()) return false;
        search.GrabFocus(); search.SelectAll();
        return true;
    }
}
