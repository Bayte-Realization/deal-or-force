using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    // A direct route to the real cabinet, not a duplicate set of personnel controls.
    private async void OpenPoliticalAppointments()
    {
        OpenScreen("State Affairs");
        // One deferred call can precede the nested containers' sort pass. Also the
        // whole cabinet is taller than the viewport: align its start, not its bottom.
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (!GodotObject.IsInstanceValid(_rulersCabinetHost) || !_rulersCabinetHost.IsVisibleInTree()) return;
        for (var parent = _rulersCabinetHost.GetParent(); parent is not null; parent = parent.GetParent())
            if (parent is ScrollContainer scroll)
            {
                scroll.ScrollVertical += (int)(_rulersCabinetHost.GlobalPosition.Y - scroll.GlobalPosition.Y);
                break;
            }
    }
}
