namespace DealOrForce;

public partial class DealOrForceGame
{
    private void ApplyCustomCursors() => GameCursorService.Ensure(this).Refresh();
}
