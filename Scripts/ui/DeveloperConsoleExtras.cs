using DealOrForce.Simulation;

namespace DealOrForce;

public partial class DealOrForceGame
{
    /// <summary>Set once the game has been told to close, so asking twice does not start two clocks.</summary>
    private bool _touchingGrass;

    /// <summary>
    /// The one console command that takes its own advice: it says its piece, and eight seconds
    /// later the game closes. Nothing is saved first, and it says so - the joke is on the
    /// session, not on the campaign somebody forgot to save.
    /// </summary>
    private string ConsoleTouchGrass()
    {
        if (!_touchingGrass)
        {
            _touchingGrass = true;
            GetTree().CreateTimer(8.0, true).Timeout += () => ApplicationExit.Request(this);
        }
        return Loc.Get("console.touchgrass.verse") + "\n\n" +
               Loc.Get("The game closes in eight seconds. Nothing has been saved.");
    }
}
