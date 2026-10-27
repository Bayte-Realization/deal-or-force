using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    /// <summary>
    /// A rail caption the width of the rail. The rail is ninety pixels, and "STATE AFFAIRS" in the
    /// tracked capitals is wider than that: cut with an ellipsis it read "STATE AFFAI…". The font
    /// steps down a point at a time until the whole word fits, never below eight, and steps back up
    /// when the rail is wider - re-measured whenever the button changes size, so a localised
    /// caption, a wider rail or a new font is handled the same way.
    /// </summary>
    private static void FitNavigationCaption(Button button, int preferred)
    {
        void Fit()
        {
            if (!GodotObject.IsInstanceValid(button) || button.Size.X <= 0f || string.IsNullOrEmpty(button.Text)) return;
            var font = button.GetThemeFont("font");
            if (font is null) return;
            var style = button.GetThemeStylebox("normal");
            var room = button.Size.X - (style is null ? 8f : style.GetMargin(Side.Left) + style.GetMargin(Side.Right)) - 2f;
            var size = preferred;
            while (size > 8 && font.GetStringSize(button.Text, HorizontalAlignment.Left, -1, size).X > room) size--;
            if (button.GetThemeFontSize("font_size") != size) button.AddThemeFontSizeOverride("font_size", size);
        }
        button.Resized += Fit;
        button.Ready += Fit;
    }
}
