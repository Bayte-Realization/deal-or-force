using System.Threading.Tasks;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyMediaShortcutFocus(DealOrForceGame game, string language, int width)
    {
        // The full diplomacy workspace owns its veil. Reach the ordinary map HUD
        // before testing a mouse click on the music shortcut behind that workspace.
        game.Call("CloseDiplomacyScreen");
        await Wait(.2);
        var note = game.FindChild("OpenMediaLibrary", true, false) as Button;
        Check(note is not null, "music focus proof finds the real HUD shortcut");
        if (note is null) return;
        await ClickAllianceControl(note, "open music by mouse");
        Check(game.FindChild("MediaLibrary", true, false) is Control { Visible: true }, "mouse shortcut opens music");
        var close = game.FindChild("CloseMediaLibrary", true, false) as Button;
        await ClickAllianceControl(close, "close music by mouse");
        Check(game.FindChild("MediaLibrary", true, false) is Control { Visible: false } &&
            GetViewport().GuiGetFocusOwner() == note && note.GetThemeStylebox("focus") is StyleBoxEmpty && !note.ButtonPressed,
            "mouse-closing music leaves a neutral shortcut and preserves keyboard return position: " + language + width);
        GetViewport().PushInput(new InputEventMouseMotion { Position = GetViewport().GetVisibleRect().Size * new Vector2(.66f, .7f) }, true);
        await Wait(.15); await Capture("music-pointer-closed-" + language + "-" + width);

        // Use the actual ui_accept path, not EmitSignal. The retained logical focus
        // should let a keyboard user reopen the same shortcut without a mouse.
        await AllianceReviewKey(Key.Enter);
        Check(game.FindChild("MediaLibrary", true, false) is Control { Visible: true }, "retained music shortcut opens with Enter");
        await AllianceReviewKey(Key.Escape);
        Check(game.FindChild("MediaLibrary", true, false) is Control { Visible: false } &&
            GetViewport().GuiGetFocusOwner() == note && note.GetThemeStylebox("focus") is not StyleBoxEmpty && !note.ButtonPressed,
            "Escape returns visible keyboard focus to the unpressed music shortcut: " + language + width);
        GetViewport().PushInput(new InputEventMouseMotion { Position = GetViewport().GetVisibleRect().Size * new Vector2(.65f, .7f) }, true);
        await Wait(.1);
        Check(GetViewport().GuiGetFocusOwner() == note && note.GetThemeStylebox("focus") is not StyleBoxEmpty,
            "pointer movement alone does not erase keyboard focus: " + language + width);
    }
}

