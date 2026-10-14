using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private void ShowModFrontendResult(string note)
    {
        var dialog = new AcceptDialog { Name = "ModActionResult", Title = Tr("Mod action"),
            DialogText = Tr(note ?? ""), DialogAutowrap = true, Exclusive = true };
        AddChild(dialog);
        dialog.Confirmed += dialog.QueueFree;
        dialog.Canceled += dialog.QueueFree;
        dialog.PopupCentered(new Vector2I(600, 180));
    }
}
