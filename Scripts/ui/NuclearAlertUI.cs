using System;
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
    private Label _nuclearAlertStamp;
    private Button _nuclearAlertDismiss;
    private HBoxContainer _nuclearAlertMast;
    private VBoxContainer _nuclearAlertCopy;
    private ScrollContainer _nuclearAlertScroll;
    private Vector2 _nuclearAlertCanvas, _nuclearAlertPixels;
    private float _nuclearAlertPreference = -1;
    private int _nuclearAlertReflowFrames;
    private double _nuclearAlertLayoutPoll;
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
        _nuclearAlertStamp.Text = Tr("FLASH · NUCLEAR DETONATION");
        _nuclearAlertScroll.ScrollVertical = 0;
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
        _nuclearAlertReflowFrames = 4;
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

        var body = new VBoxContainer { Name = "NuclearAlertBody" };
        body.AddThemeConstantOverride("separation", 0);
        _nuclearAlert.AddChild(body);

        _nuclearAlertPicture = new TextureRect
        {
            Name = "NuclearAlertPicture",
            CustomMinimumSize = new Vector2(0, 300),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Texture = LoadNuclearAlertPicture()
        };
        body.AddChild(_nuclearAlertPicture);

        var padding = new MarginContainer();
        foreach (var edge in new[] { "left", "right", "top", "bottom" })
            padding.AddThemeConstantOverride("margin_" + edge, 12);
        body.AddChild(padding);
        var copyColumn = new VBoxContainer(); copyColumn.AddThemeConstantOverride("separation", 8);
        padding.AddChild(copyColumn);
        _nuclearAlertMast = new HBoxContainer(); copyColumn.AddChild(_nuclearAlertMast);
        _nuclearAlertStamp = Title(Tr("FLASH · NUCLEAR DETONATION"), 12, Ui.Colour("state.critical"), true);
        _nuclearAlertStamp.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _nuclearAlertMast.AddChild(_nuclearAlertStamp);
        _nuclearAlertDismiss = ActionButton("×", HideNuclearAlert, 32, 32);
        _nuclearAlertDismiss.Name = "NuclearAlertDismiss";
        _nuclearAlertMast.AddChild(_nuclearAlertDismiss);

        // Scroll ONLY the copy. The picture and dismiss action stay visible; a long
        // translated/modded dispatch cannot push the close button below the screen.
        _nuclearAlertScroll = new ScrollContainer { Name = "NuclearAlertCopyScroll",
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto, FollowFocus = true };
        copyColumn.AddChild(_nuclearAlertScroll);
        _nuclearAlertCopy = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _nuclearAlertCopy.AddThemeConstantOverride("separation", 8);
        _nuclearAlertScroll.AddChild(_nuclearAlertCopy);

        _nuclearAlertHeadline = Title("", 20, Ui.Colour("text.bright"));
        _nuclearAlertHeadline.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _nuclearAlertHeadline.Name = "NuclearAlertHeadline";
        _nuclearAlertCopy.AddChild(_nuclearAlertHeadline);
        _nuclearAlertText = Title("", 14, Ui.Colour("text.body"));
        _nuclearAlertText.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _nuclearAlertText.Name = "NuclearAlertText";
        _nuclearAlertCopy.AddChild(_nuclearAlertText);
        foreach (var label in new[] { _nuclearAlertStamp, _nuclearAlertHeadline, _nuclearAlertText })
            if (label.LabelSettings is not null) label.LabelSettings = (LabelSettings)label.LabelSettings.Duplicate();
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
        var view = _hudLayer.Size;
        var pixels = (GetViewport().GetStretchTransform() * _hudLayer.GetGlobalTransformWithCanvas()).Scale.Abs();
        var preference = Math.Clamp(SettingsCatalog.Number("interface_scale"), .5f, 2.5f);
        if (view != _nuclearAlertCanvas || !pixels.IsEqualApprox(_nuclearAlertPixels) || Math.Abs(preference - _nuclearAlertPreference) > .001f)
        {
            _nuclearAlertCanvas = view; _nuclearAlertPixels = pixels; _nuclearAlertPreference = preference;
            _nuclearAlertReflowFrames = 4;
        }
        if (_nuclearAlertReflowFrames <= 0) return;
        _nuclearAlertReflowFrames--;
        var scale = MathF.Max(.01f, MathF.Min(pixels.X, pixels.Y));
        void Font(Label label, int authored, float floor)
        {
            var size = DecisionReadability.FontSize(authored, scale, preference, floor);
            if (label.LabelSettings is not null) { if (label.LabelSettings.FontSize != size) label.LabelSettings.FontSize = size; }
            else if (label.GetThemeFontSize("font_size") != size) label.AddThemeFontSizeOverride("font_size", size);
        }
        Font(_nuclearAlertHeadline, 20, 14); Font(_nuclearAlertText, 14, 14); Font(_nuclearAlertStamp, 12, 12);
        var hit = DecisionReadability.TargetHeight(32, scale, preference);
        _nuclearAlertDismiss.CustomMinimumSize = new Vector2(hit, hit);
        var bounds = DecisionReadability.Fit(view.X, view.Y, scale, (view.X - 560) / 2,
            MathF.Max(96, view.Y * .16f), 560, view.Y, 440, 0, 20);
        var top = MathF.Max(20 / scale, MathF.Min(MathF.Max(96, view.Y * .16f), view.Y * .2f));
        var room = MathF.Max(1, view.Y - top - 20 / scale);
        var picture = _nuclearAlertPicture.Texture is not null ? MathF.Min(bounds.Width * .53f, room * .46f) : 0;
        _nuclearAlertPicture.Visible = picture > 0;
        _nuclearAlertPicture.CustomMinimumSize = new Vector2(0, picture);
        var chrome = picture + 32 + _nuclearAlertMast.GetCombinedMinimumSize().Y +
            _nuclearAlert.GetThemeStylebox("panel").GetMinimumSize().Y;
        var copy = MathF.Min(_nuclearAlertCopy.GetCombinedMinimumSize().Y, MathF.Max(1, room - chrome));
        _nuclearAlertScroll.CustomMinimumSize = new Vector2(0, copy);
        _nuclearAlert.Position = new Vector2(bounds.X, top);
        // Wrapped text minimums are lazy: the first query can measure a paragraph at
        // yesterday's width. Reassert content height after four container-sort frames,
        // not just the first ResetSize that left a 900px blank panel permanently.
        _nuclearAlert.Size = new Vector2(bounds.Width, chrome + copy);
    }

    /// <summary>Counted in real seconds, so a paused or fast game shows it for the same time.</summary>
    private void TickNuclearAlert(double delta)
    {
        if (_nuclearAlert is not { Visible: true }) return;
        _nuclearAlertLayoutPoll -= delta;
        if (_nuclearAlertReflowFrames > 0 || _nuclearAlertLayoutPoll <= 0)
        { _nuclearAlertLayoutPoll = .15; PlaceNuclearAlert(); }
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
