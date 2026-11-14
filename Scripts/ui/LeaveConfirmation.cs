using System;
using System.Linq;
using Godot;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>
/// "Are you sure?" before the game is left - to the main menu or to the desktop. A misplaced
/// click used to throw a campaign away (the main-menu button asked only by changing its own
/// caption, and quitting did not ask at all); the owner asked for a plain question instead.
/// One dialog for every place that leaves, with buttons large enough for a finger.
/// </summary>
public static class LeaveConfirmation
{
    /// <summary>Ask on <paramref name="host"/>; <paramref name="leave"/> runs only on "yes".</summary>
    public static void Ask(Node host, string title, string question, string confirm, Action leave)
    {
        if (host is null || !GodotObject.IsInstanceValid(host) || leave is null) return;
        // One question at a time: a second click on the same button does not stack dialogs.
        if (host.GetChildren().OfType<ConfirmationDialog>().Any(dialog => dialog.Visible && dialog.HasMeta("leave_confirmation")))
            return;
        var dialog = new ConfirmationDialog
        {
            Name = "LeaveConfirmation",
            Title = Loc.Get(title),
            DialogText = Loc.Get(question),
            OkButtonText = Loc.Get(confirm),
            CancelButtonText = Loc.Get("Cancel"),
            Exclusive = true,
            Unresizable = true,
            ProcessMode = Node.ProcessModeEnum.Always
        };
        dialog.SetMeta("leave_confirmation", true);
        // The game's own skin, not Godot's grey: the shell colours, the title band and the
        // instrument buttons the editors already use (ScenarioEditorTheme).
        var theme = ScenarioEditorTheme.Create();
        theme.DefaultFontSize = 17;
        theme.SetStylebox("panel", "AcceptDialog", ScenarioEditorTheme.Surface("shell.background", "shell.border", 18));
        theme.SetColor("font_color", "Label", Ui.Colour("text.body"));
        // AcceptDialog sizes its own buttons from these, over any minimum set on the buttons.
        theme.SetConstant("buttons_min_width", "AcceptDialog", 160);
        theme.SetConstant("buttons_min_height", "AcceptDialog", 48);
        theme.SetConstant("buttons_separation", "AcceptDialog", 24);
        dialog.Theme = theme;
        dialog.GetLabel().HorizontalAlignment = HorizontalAlignment.Center;
        dialog.GetLabel().AutowrapMode = TextServer.AutowrapMode.WordSmart;
        foreach (var button in new[] { dialog.GetOkButton(), dialog.GetCancelButton() })
        {
            button.CustomMinimumSize = new Vector2(160, 48);
            button.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        }
        dialog.Confirmed += () =>
        {
            dialog.QueueFree();
            leave();
        };
        dialog.Canceled += dialog.QueueFree;
        host.AddChild(dialog);
        dialog.PopupCentered(new Vector2I(540, 190));
        // The safe answer has the focus: Enter on a stray keypress keeps the player in the game.
        dialog.GetCancelButton().CallDeferred(Control.MethodName.GrabFocus);
    }

    /// <summary>Quit to the desktop after asking.</summary>
    public static void QuitToDesktop(Node host, bool inCampaign) =>
        Ask(host, "Quit to desktop",
            inCampaign
                ? "Are you sure you want to quit? Anything not saved since your last save will be lost."
                : "Are you sure you want to quit?",
            "Quit", () => ApplicationExit.Request(host));

    /// <summary>Leave the campaign for the main menu after asking.</summary>
    public static void ReturnToMainMenu(Node host, Action leave) =>
        Ask(host, "Return to main menu",
            "Are you sure? Anything not saved since your last save will be lost.",
            "Leave", leave);
}
