using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The moment a nuclear weapon goes off anywhere in the world: a picture, a headline and two lines,
/// over the map for a few seconds. It does not pause the game and does not ask anything - the
/// decision was somebody else's - but nobody, observer included, can miss it.
/// </summary>
public partial class DealOrForceGame
{
    private const double NuclearAlertSeconds = 15.0;
    private const string NuclearAlertPicture = "res://game/gfx/events/event_nuclear_strike.jpg";

    private PanelContainer _nuclearAlert;
    private TextureRect _nuclearAlertPicture;
    private Label _nuclearAlertHeadline;
    private Label _nuclearAlertText;
    private Campaign _nuclearAlertWorld;
    private long _nuclearAlertSeen;
    private double _nuclearAlertLeft;

    private static bool IsNuclearUse(string kind) =>
        kind is "nuclear_strike" or "nuclear_retaliation" or "nuclear_devastation";

    /// <summary>Called with the strategic HUD refresh, which runs whenever the chronicle grows.</summary>
    private void RefreshNuclearAlert()
    {
        if (_campaign is null) return;
        var entries = _campaign.Chronicle.Entries;
        var newest = entries.Count > 0 ? entries[^1].Sequence : 0;
        // What happened before this world was opened is history, not an alarm.
        if (!ReferenceEquals(_nuclearAlertWorld, _campaign))
        {
            _nuclearAlertWorld = _campaign;
            _nuclearAlertSeen = newest;
            HideNuclearAlert();
            return;
        }
        if (newest <= _nuclearAlertSeen) return;
        var seen = _nuclearAlertSeen;
        _nuclearAlertSeen = newest;
        // The worst of what is new leads: a city outranks a battlefield.
        var use = entries.Where(e => e.Sequence > seen && IsNuclearUse(e.Kind))
            .OrderByDescending(e => e.Kind == "nuclear_devastation")
            .ThenByDescending(e => e.Sequence)
            .FirstOrDefault();
        if (use is not null) ShowNuclearAlert(use);
    }

    private void ShowNuclearAlert(ChronicleEntry use)
    {
        BuildNuclearAlert();
        var headline = use.Kind switch
        {
            "nuclear_devastation" => Tr("{0} has struck the cities of {1}"),
            "nuclear_retaliation" => Tr("{0} has answered in kind against {1}"),
            _ => Tr("{0} has used a nuclear weapon against {1}")
        };
        _nuclearAlertHeadline.Text = string.Format(headline, use.ActorName, use.TargetName);
        _nuclearAlertText.Text = NewsFeed.TextFor(_campaign, use) + "\n" + Tr(use.Kind == "nuclear_devastation"
            ? "Hundreds of thousands are dead. Every capital on earth now asks whether it is next."
            : "The taboo of eighty years is broken. Every government on earth will remember who broke it.");
        _nuclearAlert.Visible = true;
        _nuclearAlert.Modulate = Colors.White;
        _nuclearAlertLeft = NuclearAlertSeconds;
        PlaceNuclearAlert();
    }

    private void BuildNuclearAlert()
    {
        if (_nuclearAlert is not null) return;
        _nuclearAlert = new PanelContainer { Name = "NuclearAlert", MouseFilter = Control.MouseFilterEnum.Stop, Visible = false };
        _nuclearAlert.AddThemeStyleboxOverride("panel", ShellPanelStyle());
        _nuclearAlert.GuiInput += @event =>
        {
            if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) HideNuclearAlert();
        };
        _hudLayer.AddChild(_nuclearAlert);

        var body = new VBoxContainer();
        body.AddThemeConstantOverride("separation", 8);
        _nuclearAlert.AddChild(body);

        _nuclearAlertPicture = new TextureRect
        {
            Name = "NuclearAlertPicture",
            CustomMinimumSize = new Vector2(560, 300),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Texture = LoadNuclearAlertPicture()
        };
        body.AddChild(_nuclearAlertPicture);

        var mast = new HBoxContainer();
        body.AddChild(mast);
        var stamp = Title(Tr("FLASH · NUCLEAR DETONATION"), 12, Ui.Colour("state.critical"), true);
        stamp.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        mast.AddChild(stamp);
        var close = ActionButton("×", HideNuclearAlert, 30, 26);
        close.Name = "NuclearAlertDismiss";
        mast.AddChild(close);

        _nuclearAlertHeadline = Title("", 20, Ui.Colour("text.bright"));
        _nuclearAlertHeadline.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _nuclearAlertHeadline.CustomMinimumSize = new Vector2(560, 0);
        body.AddChild(_nuclearAlertHeadline);
        _nuclearAlertText = Title("", 13, Ui.Colour("text.body"));
        _nuclearAlertText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _nuclearAlertText.CustomMinimumSize = new Vector2(560, 0);
        body.AddChild(_nuclearAlertText);
    }

    /// <summary>The imported picture, or the file itself when the editor has not imported it yet.</summary>
    private static Texture2D LoadNuclearAlertPicture()
    {
        if (ResourceLoader.Exists(NuclearAlertPicture)) return GD.Load<Texture2D>(NuclearAlertPicture);
        var image = Image.LoadFromFile(ProjectSettings.GlobalizePath(NuclearAlertPicture));
        return image is null ? null : ImageTexture.CreateFromImage(image);
    }

    private void PlaceNuclearAlert()
    {
        if (_nuclearAlert is null) return;
        var view = GetViewport().GetVisibleRect().Size;
        _nuclearAlert.ResetSize();
        var size = _nuclearAlert.GetCombinedMinimumSize();
        _nuclearAlert.Position = new Vector2((view.X - size.X) * 0.5f, Mathf.Max(96f, view.Y * 0.16f));
    }

    /// <summary>Counted in real seconds, so a paused or fast game shows it for the same time.</summary>
    private void TickNuclearAlert(double delta)
    {
        if (_nuclearAlert is not { Visible: true }) return;
        _nuclearAlertLeft -= delta;
        if (_nuclearAlertLeft <= 0) HideNuclearAlert();
        else if (_nuclearAlertLeft < 1.0) _nuclearAlert.Modulate = new Color(1, 1, 1, (float)_nuclearAlertLeft);
    }

    private void HideNuclearAlert()
    {
        if (_nuclearAlert is null) return;
        _nuclearAlert.Visible = false;
        _nuclearAlertLeft = 0;
    }
}
