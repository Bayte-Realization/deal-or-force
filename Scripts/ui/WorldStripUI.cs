using Godot;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>
/// The state of the world, in one fixed place: the foot of the date plate.
///
/// The alert level and the world's tension were a button on the top bar, then a row among the
/// alerts - a row that moved up and down the list and could be cut off by "+N more". The owner
/// asked where they are when he is not reading hints, and the honest answer was "nowhere fixed".
/// They are not a task; they are a reading, like the date and the speed above them. So the date
/// plate says three things: what day it is, how fast time runs, and how close the world is to war.
///
/// Each reading is its number in the colour of its state and a small drawn meter: lamps for the
/// alert ladder (one lit in peace, all five on the brink), a bar for tension. A click opens the
/// screen behind it. Either is absent when a scenario or mod has its mechanic switched off, and
/// the strip with them; the level's name is localisation, so another era calls it what it likes.
/// </summary>
public partial class DealOrForceGame
{
    private HBoxContainer _worldStrip;
    private Button _worldDefcon, _worldTension;
    private int _worldDefconLevel = 5;
    private float _worldTensionValue;

    private void BuildWorldStrip(VBoxContainer under)
    {
        _worldStrip = new HBoxContainer { Name = "DatePlateWorld" };
        _worldStrip.AddThemeConstantOverride("separation", 6);
        under.AddChild(_worldStrip);

        _worldDefcon = WorldReading("WorldDefcon", () => { OpenDefconOverview(); UpdateScreenButtons(); });
        _worldDefcon.Draw += () =>
        {
            // Five lamps at the right end: peace lights one, the brink lights all five.
            var colour = Ui.Colour(DefconSystem.LevelColourToken(_worldDefconLevel));
            var dark = Ui.Colour("surface.line_faint");
            const float lamp = 7f, gap = 3f;
            var x = _worldDefcon.Size.X - 8f - 5 * lamp - 4 * gap;
            var y = (_worldDefcon.Size.Y - lamp) * .5f;
            for (var i = 0; i < 5; i++)
                _worldDefcon.DrawRect(new Rect2(x + i * (lamp + gap), y, lamp, lamp), i < 6 - _worldDefconLevel ? colour : dark);
        };
        _worldTension = WorldReading("WorldTension", () =>
        {
            _conflictTab = 5; _conflictPair = -1; _conflictWar = -1;
            OpenConflictsScreen(); UpdateScreenButtons();
        });
        _worldTension.Draw += () =>
        {
            // A bar along the foot of the reading: how much of the way to a world war.
            var width = _worldTension.Size.X - 12f;
            var y = _worldTension.Size.Y - 5f;
            _worldTension.DrawRect(new Rect2(6f, y, width, 2f), Ui.Colour("surface.line_faint"));
            _worldTension.DrawRect(new Rect2(6f, y, width * Mathf.Clamp(_worldTensionValue / 100f, 0f, 1f), 2f),
                Ui.Colour(AlertListRules.TensionDot(_worldTensionValue)));
        };
    }

    private Button WorldReading(string name, System.Action open)
    {
        var button = ActionButton("", open, 0, 24);
        button.Name = name;
        button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        button.Alignment = HorizontalAlignment.Left;
        button.ClipText = true;
        button.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        button.AddThemeFontSizeOverride("font_size", 12);
        StyleHudButton(button, false);
        _worldStrip.AddChild(button);
        return button;
    }

    private void RefreshWorldStrip()
    {
        if (_worldStrip is null || !GodotObject.IsInstanceValid(_worldStrip) || _campaign is null) return;
        var defcon = DefconSystem.Enabled(_campaign);
        var tension = MechanicFeatures.SystemEnabled(_campaign, nameof(WorldWar));
        _worldStrip.Visible = defcon || tension;
        _worldDefcon.Visible = defcon;
        _worldTension.Visible = tension;
        if (defcon)
        {
            _worldDefconLevel = DefconSystem.CurrentLevel(_campaign);
            // "DEFCON 4 · Intelligence Watch": the number here, the name of the rung in the tooltip.
            var badge = Tr(DefconSystem.LevelBadge(_worldDefconLevel));
            var cut = badge.IndexOf(" · ", System.StringComparison.Ordinal);
            _worldDefcon.Text = cut > 0 ? badge[..cut] : badge;
            _worldDefcon.AddThemeColorOverride("font_color", Ui.Colour(DefconSystem.LevelColourToken(_worldDefconLevel)));
            _worldDefcon.TooltipText = Ui.Tip(badge + "\n\n" + DefconSystem.FormatSummary(_campaign) + "\n\n" +
                Tr("Click for the ladder, the nuclear powers and our posture."));
            _worldDefcon.QueueRedraw();
        }
        if (tension)
        {
            _worldTensionValue = WorldTensionToday();
            _worldTension.Text = Loc.Format("Tension {0}", Mathf.RoundToInt(_worldTensionValue));
            _worldTension.AddThemeColorOverride("font_color", Ui.Colour(_worldTensionValue >= 40f
                ? AlertListRules.TensionDot(_worldTensionValue) : "text.body_soft"));
            _worldTension.TooltipText = Ui.Tip(Loc.Format("World tension {0} · {1}", Mathf.RoundToInt(_worldTensionValue),
                    Tr(_worldTensionValue >= 80f ? "on the brink" : _worldTensionValue >= 60f ? "dangerous" : _worldTensionValue >= 40f ? "tense" : "calm")) + "\n\n" +
                Tr("How close the great powers are to war with one another. The worst rivalry counts in full, the next two in part. When it runs high, incidents between the blocs become likelier.") +
                "\n\n" + Tr("Click for the great powers' rivalries."));
            _worldTension.QueueRedraw();
        }
    }
}
