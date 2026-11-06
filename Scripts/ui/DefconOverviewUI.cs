using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The nuclear alert, on a screen of its own: what the DEFCON badge in the top bar opens.
///
/// The badge used to open the situation desk, which said nothing about the alert at all. This is
/// the ladder from routine to launch-on-warning with the world's place on it, why it is there,
/// what it costs, who holds the weapons and how alarmed each is - and, for a holder, the four acts
/// (NuclearPosture): raise the alert, stand down, test a weapon, pick up the red telephone.
/// </summary>
public partial class DealOrForceGame
{
    private Control _defconLayer;
    private VBoxContainer _defconRows;

    private void OpenDefconOverview()
    {
        if (_campaign is null) return;
        PlayStrategicCue("ui_select");
        if (_defconLayer is null) BuildDefconOverview();
        RefreshDefconOverview();
        _defconLayer!.Visible = true;
        _defconLayer.MoveToFront();
    }

    private void CloseDefconOverview()
    {
        if (_defconLayer is not null) _defconLayer.Visible = false;
    }

    private void BuildDefconOverview()
    {
        _defconLayer = FullLayer("DefconOverview");
        _defconLayer.Visible = false;
        _defconLayer.MouseFilter = MouseFilterEnum.Stop;
        AddChild(_defconLayer);
        var veil = new ColorRect { Color = Ui.Colour("surface.overlay", 0.82f), MouseFilter = MouseFilterEnum.Stop };
        veil.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        veil.GuiInput += input =>
        {
            if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) CloseDefconOverview();
        };
        _defconLayer.AddChild(veil);
        var panel = TexturedPanel(_defconLayer, PanelSurface.Modal);
        panel.Name = "DefconPanel";
        Ui.Place(panel, "modal.navigator");
        var rows = PanelRows(panel, 22, 16, 10);
        var close = ActionButton("×", CloseDefconOverview, 36, 32);
        close.Name = "DefconClose";
        ScreenHeader(rows, "Nuclear alert",
            "Where the world stands between routine and launch on warning, why, and what it costs. Every nuclear power sets its own alert; the world is never calmer than the most alarmed.",
            false, close);
        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        rows.AddChild(scroll);
        _defconRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _defconRows.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_defconRows);
    }

    private void RefreshDefconOverview()
    {
        if (_defconRows is null || _campaign is null) return;
        foreach (var child in _defconRows.GetChildren()) child.QueueFree();
        var world = DefconSystem.CurrentLevel(_campaign);
        var player = _campaign.Player;
        var holder = player is not null && NuclearPosture.IsHolder(_campaign, player);

        // The ladder, 5 at the top: the world's rung lit, the player's own forces marked.
        SectionRule(_defconRows, "The ladder");
        for (var level = 5; level >= 1; level--)
        {
            var line = new HBoxContainer();
            line.AddThemeConstantOverride("separation", 12);
            var lit = level == world;
            var badge = Title(Tr(DefconSystem.LevelBadge(level)), lit ? 16 : 13,
                lit ? Ui.Colour(DefconSystem.LevelColourToken(level)) : Ui.Colour("text.muted"));
            badge.CustomMinimumSize = new Vector2(330, 0);
            line.AddChild(badge);
            // Where the world and our own forces stand, as labelled tags in a column of their own on
            // the rung itself. A yellow sentence with an arrow, wrapping in the middle, read as a
            // caption nobody could place (the user asked twice what it meant).
            var tags = new HBoxContainer { CustomMinimumSize = new Vector2(240, 0) };
            tags.AddThemeConstantOverride("separation", 6);
            if (lit) tags.AddChild(DefconTag(Tr("WORLD NOW"), Ui.Colour(DefconSystem.LevelColourToken(level)),
                Tr("The level the whole world stands at: the most alarmed of the nuclear powers, or a crisis hotspot.")));
            if (holder && player!.NuclearPosture == level)
                tags.AddChild(DefconTag(Tr("OUR FORCES"), Ui.Colour("text.accent"),
                    Tr("The alert our own nuclear forces are kept at. Raise or stand it down below.")));
            line.AddChild(tags);
            var text = Title(Tr(DefconSystem.LevelDescription(level)), 12, lit ? Ui.Colour("text.body") : Ui.Colour("text.faint"));
            text.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            line.AddChild(text);
            _defconRows.AddChild(line);
        }

        DefconSystem.EvaluateTarget(_campaign, out var hotspot);
        _defconRows.AddChild(Title(string.IsNullOrEmpty(hotspot)
            ? Tr("No critical nuclear-power hotspots active")
            : Loc.Format("Active crisis hotspot: {0}", hotspot), 13, Ui.Colour("text.body")));

        // What it costs this country now.
        if (player is not null)
        {
            SectionRule(_defconRows, "What it costs us");
            var upkeep = (DefconSystem.UpkeepMultiplier(_campaign, player) - 1f) * 100f;
            var recovery = (DefconSystem.ReadinessRecoveryMultiplier(_campaign, player.Id) - 1f) * 100f;
            var trade = (1f - DefconSystem.GlobalTradeMultiplier(_campaign)) * 100f;
            _defconRows.AddChild(Title(Loc.Format("Upkeep of forces on alert +{0:0}% · organisation recovers +{1:0}% faster · world trade −{2:0}%",
                upkeep, recovery, trade), 13, Ui.Colour("text.body")));
        }

        // The player's own alert and its four acts.
        SectionRule(_defconRows, "Our posture");
        if (!holder)
        {
            _defconRows.AddChild(Title(Tr("Your country holds no nuclear deterrent: the alert reaches it through upkeep, trade and the wars it settles."),
                13, Ui.Colour("text.muted")));
        }
        else
        {
            _defconRows.AddChild(Title(Loc.Format("Our forces stand at DEFCON {0}.", player!.NuclearPosture), 14, Ui.Colour("text.bright")));
            var acts = new HFlowContainer();
            acts.AddThemeConstantOverride("h_separation", 8);
            acts.AddThemeConstantOverride("v_separation", 6);
            _defconRows.AddChild(acts);
            foreach (var (action, label, help) in new[]
                     {
                         ("raise", "Raise the alert", "One step towards launch on warning: our forces recover faster and every country at war with us feels it in its war support. The upkeep rises, the world is never calmer than us, and at DEFCON 2 everybody not on our side remembers it. Below DEFCON 4 every ally is the dearer to attack."),
                         ("stand_down", "Stand down", "One step back towards routine: cheaper, and the world calms with us."),
                         ("test", "Test a weapon", "Credibility bought with every rival's tension and most of the world's opinion. Once a year at most.")
                     })
            {
                var captured = action;
                var why = NuclearPosture.Refusal(_campaign, player, action);
                var button = ActionButton(Tr(label), () => PostureOrder(captured, -1), 0, 32);
                button.Name = "Posture_" + action;
                button.Disabled = why.Length > 0;
                button.TooltipText = Ui.Tip(why.Length > 0 ? why : Tr(help));
                acts.AddChild(button);
            }

            // The last resort, behind a question: one click must never end a world.
            var strikeWhy = NuclearPosture.Refusal(_campaign, player, "strike");
            var answering = NuclearPosture.Avenging(_campaign, player) is not null;
            var strike = ActionButton("Plan strike on armed forces", () => ConfirmNuclearStrike(false), 0, 32);
            strike.Name = "Posture_strike";
            strike.Disabled = false; // Planning is available even when execution needs a higher alert.
            strike.Modulate = strikeWhy.Length > 0 ? Colors.White : Ui.Colour("state.critical");
            strike.TooltipText = Ui.Tip(strikeWhy.Length > 0 ? strikeWhy : Tr(answering ? AnswerHelp : StrikeHelp));
            acts.AddChild(strike);

            // The strategic use: not the enemy's army but the enemy's country.
            var citiesWhy = NuclearPosture.Refusal(_campaign, player, "devastate");
            var cities = ActionButton("Plan strike on cities",
                () => ConfirmNuclearStrike(true), 0, 32);
            cities.Name = "Posture_devastate";
            cities.Disabled = false;
            cities.Modulate = citiesWhy.Length > 0 ? Colors.White : Ui.Colour("state.critical");
            cities.TooltipText = Ui.Tip(citiesWhy.Length > 0 ? citiesWhy : Tr(CitiesHelp));
            acts.AddChild(cities);
        }

        // Every holder, its alert, and for the player the red telephone to it.
        SectionRule(_defconRows, "The nuclear powers");
        foreach (var other in _campaign.Nations.Where(n => n.Population > 0 && NuclearPosture.IsHolder(_campaign, n))
                     .OrderBy(n => n.NuclearPosture).ThenBy(n => n.DisplayName))
        {
            var line = new HBoxContainer();
            line.AddThemeConstantOverride("separation", 10);
            // A fixed box: flags differ in width, and the alert column should not wander with them.
            var flagBox = new CenterContainer { CustomMinimumSize = new Vector2(52, 24) };
            flagBox.AddChild(FlagBadge(other.Tag, 22, other.DisplayName));
            line.AddChild(flagBox);
            var name = Title(other.DisplayName, 13, Ui.Colour(other.Id == player?.Id ? "text.accent" : "text.body"));
            name.CustomMinimumSize = new Vector2(260, 0);
            line.AddChild(name);
            var posture = Title($"DEFCON {other.NuclearPosture}", 13, Ui.Colour(DefconSystem.LevelColourToken(other.NuclearPosture)));
            posture.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            line.AddChild(posture);
            if (holder && other.Id != player!.Id)
            {
                var target = other;
                var why = NuclearPosture.Refusal(_campaign, player, "hotline", target);
                var call = ActionButton(Tr("Red telephone"), () => PostureOrder("hotline", target.Id), 150, 28);
                call.Name = "Hotline_" + other.Tag;
                call.Disabled = why.Length > 0;
                call.TooltipText = Ui.Tip(why.Length > 0 ? why : Tr("Both of us step back from the brink, and the tension between us falls."));
                line.AddChild(call);
            }
            _defconRows.AddChild(line);
        }
    }

    private const string StrikeHelp =
        "The strongest enemy force within reach - on our soil first, then on theirs - loses most of its men and all its order, the region is left in ruins, and the country it belongs to loses the stomach for the war. Every government on earth remembers it, allies included, and the world spends a year at the brink.";

    private const string CitiesHelp =
        "Not an army but a country: the weapon falls on the enemy's capital. A sixth of the region's people die, the region is ruined for a generation, and the enemy loses stability and the will to fight. No first use is condemned like this one, and a holder struck this way will answer on our cities.";

    private const string AnswerHelp =
        "Our forces were struck. The answer falls on the strongest force of the country that struck them, wherever it stands. The world condemns an answer less than a first use, but the brink comes no further away.";

    private void ConfirmNuclearStrike(bool cities)
    {
        OpenNuclearStrikeReview(cities);
    }

    private void PostureOrder(string action, int targetId)
    {
        if (_campaign?.Player is not { } player) return;
        _campaign.Commands.Enqueue(new NuclearPostureCommand { IssuerId = player.Id, Action = action, TargetId = targetId });
        _campaign.Commands.ApplyAll(_campaign);
        PlayStrategicCue("ui_select");
        RefreshDefconOverview();
        Refresh();
    }

    /// <summary>A small framed tag on a rung of the ladder: a word in the tag's colour, its meaning on hover.</summary>
    private Control DefconTag(string text, Color colour, string tip)
    {
        var panel = new PanelContainer { MouseFilter = MouseFilterEnum.Stop, TooltipText = Ui.Tip(tip), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        var style = new StyleBoxFlat { BgColor = new Color(colour, 0.16f), BorderColor = colour, ContentMarginLeft = 7, ContentMarginRight = 7, ContentMarginTop = 2, ContentMarginBottom = 2 };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(3);
        panel.AddThemeStyleboxOverride("panel", style);
        var label = Title(text, 10, colour, upper: true);
        label.AutowrapMode = TextServer.AutowrapMode.Off;
        panel.AddChild(label);
        return panel;
    }
}
