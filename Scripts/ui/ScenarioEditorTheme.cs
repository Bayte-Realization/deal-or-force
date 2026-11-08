using System;
using Godot;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>Editor forms use the campaign's named skin roles, not Godot's grey defaults.
/// A fresh theme per editor prevents one mod/scene from recolouring another live editor.</summary>
internal static class ScenarioEditorTheme
{
    internal static StyleBoxFlat Surface(string fill, string border, int padding = 8)
    {
        var box = new StyleBoxFlat { BgColor = Ui.Colour(fill), BorderColor = Ui.Colour(border) };
        box.SetBorderWidthAll(1);
        box.SetCornerRadiusAll(2);
        box.SetContentMarginAll(padding);
        return box;
    }

    internal static Theme Create()
    {
        var theme = new Theme {
            DefaultFont = PresentationFonts.For("ui.default"),
            DefaultFontSize = (int)Math.Clamp(Ui.Metric("map_editor.form_font_size", 14), 12, 22)
        };
        theme.SetColor("font_color", "Label", Ui.Colour("text.body_soft"));
        foreach (var type in new[] { "LineEdit", "TextEdit", "CodeEdit" })
        {
            theme.SetStylebox("normal", type, Surface("surface.inset", "shell.border"));
            theme.SetStylebox("read_only", type, Surface("button.fill_disabled", "button.border_disabled"));
            var focus = Surface("surface.inset", "text.accent", 8);
            focus.DrawCenter = false;
            theme.SetStylebox("focus", type, focus);
            theme.SetColor("font_color", type, Ui.Colour("text.body"));
            theme.SetColor("font_placeholder_color", type, Ui.Colour("text.hint"));
            theme.SetColor("caret_color", type, Ui.Colour("text.accent"));
            theme.SetColor("selection_color", type, Ui.Colour("button.fill_hover"));
        }
        theme.SetFont("font", "CodeEdit", PresentationFonts.For("ui.code"));
        foreach (var type in new[] { "Button", "OptionButton", "ColorPickerButton" })
        {
            theme.SetStylebox("normal", type, InstrumentMaterials.Face(Surface("button.fill", "button.border")));
            theme.SetStylebox("hover", type, InstrumentMaterials.Face(Surface("button.fill_hover", "button.border_hover")));
            theme.SetStylebox("pressed", type, InstrumentMaterials.Face(Surface("button.fill_pressed", "button.border_pressed"), inset: true));
            theme.SetStylebox("hover_pressed", type, InstrumentMaterials.Face(Surface("button.fill_pressed", "button.border_pressed"), inset: true));
            theme.SetStylebox("disabled", type, InstrumentMaterials.Face(Surface("button.fill_disabled", "button.border_disabled"), disabled: true));
            var focus = Surface("button.fill", "text.accent"); focus.DrawCenter = false;
            theme.SetStylebox("focus", type, focus);
            theme.SetColor("font_color", type, Ui.Colour("button.text"));
            theme.SetColor("font_hover_color", type, Ui.Colour("button.text_hover"));
            theme.SetColor("font_pressed_color", type, Ui.Colour("button.text_pressed"));
            theme.SetColor("font_disabled_color", type, Ui.Colour("button.text_disabled"));
        }
        theme.SetStylebox("panel", "TabContainer", Surface("shell.row", "shell.border"));
        theme.SetStylebox("tab_unselected", "TabContainer", Surface("button.fill", "shell.border"));
        theme.SetStylebox("tab_selected", "TabContainer", Surface("button.primary_fill", "button.primary_border"));
        theme.SetStylebox("tab_hovered", "TabContainer", Surface("button.fill_hover", "button.border_hover"));
        theme.SetColor("font_selected_color", "TabContainer", Ui.Colour("text.bright"));
        theme.SetColor("font_unselected_color", "TabContainer", Ui.Colour("text.muted"));
        theme.SetStylebox("panel", "PopupMenu", Surface("shell.background", "shell.border"));
        theme.SetStylebox("hover", "PopupMenu", Surface("button.fill_hover", "button.border_hover"));
        theme.SetColor("font_color", "PopupMenu", Ui.Colour("text.body"));
        theme.SetColor("font_hover_color", "PopupMenu", Ui.Colour("button.text_hover"));
        theme.SetStylebox("panel", "ItemList", Surface("surface.inset", "shell.border"));
        theme.SetStylebox("selected", "ItemList", Surface("button.primary_fill", "button.primary_border", 3));
        theme.SetStylebox("selected_focus", "ItemList", Surface("button.primary_fill", "button.border_hover", 3));
        theme.SetColor("font_color", "ItemList", Ui.Colour("text.body"));
        // Embedded window chrome lies above the client rectangle. A plain panel style
        // leaves the title floating over the game behind it; reserve a real title band.
        var chrome = Surface("shell.background", "shell.background", 0);
        chrome.SetExpandMarginAll(2);
        chrome.ExpandMarginTop = 28;
        chrome.BorderWidthTop = 28;
        theme.SetStylebox("embedded_border", "Window", chrome);
        theme.SetColor("title_color", "Window", Ui.Colour("text.accent"));
        return theme;
    }
}
