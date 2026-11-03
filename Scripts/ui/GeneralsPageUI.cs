using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The Book of war's third page: the country's commanders.
///
/// Every army group has had a commander since the generals came in - named, with a skill, a
/// temperament and a record - but the only place they showed was the subtitle of the army panel
/// with several divisions of one group selected, and nothing could be done about one. This page
/// lists them all, group by group, and relieves one: three officers are put forward for the post
/// and the player picks. The price, the waiting time and the war support a victorious commander's
/// removal costs are the ones the command and the computer's governments go through (Generals).
/// </summary>
public partial class DealOrForceGame
{
    /// <summary>The group whose relief is being considered, its candidates shown; -1 for none.</summary>
    private int _generalsReliefGroup = -1;

    /// <summary>Opens the Book of war on the commanders.</summary>
    private void OpenGeneralsScreen()
    {
        _doctrineView = "generals";
        OpenDoctrineScreen();
    }

    private void GeneralsPage(Nation n, VBoxContainer rows)
    {
        PolicyHeading(rows, "GENERALS");
        PolicyText(rows, Loc.Get(
            "Each army group has a commander. Their skill moves the weight of every formation under them, their temperament changes how they attack and how they defend, and they learn from victories and lose standing in defeats. A commander whose army breaks may fall. Relieving one costs political credit, and a commander must hold the group a while before they can be relieved."));

        var groups = _campaign.ArmyGroups.Where(group => group.NationId == n.Id).OrderBy(group => group.Id).ToArray();
        if (groups.Length == 0)
        {
            PolicyText(rows, Loc.Get("No army groups yet: a group, and its commander, comes with the first formation raised."));
            return;
        }
        foreach (var group in groups)
        {
            Generals.EnsureCommander(_campaign, group);
            var members = _campaign.Armies.Where(army => army.ArmyGroupId == group.Id).ToArray();
            var card = new PanelContainer { Name = "General_" + group.Id };
            card.AddThemeStyleboxOverride("panel", WarInsetStyle(12, 10));
            AtlasSurfaceSkin.Attach(card, AtlasSurfaceSkin.Level.Inset, "surface.inset");
            rows.AddChild(card);
            var body = new VBoxContainer();
            body.AddThemeConstantOverride("separation", 5);
            card.AddChild(body);

            var trait = Generals.Trait(group.CommanderTrait);
            // The commander's emblem beside the name: the temperament read at a glance.
            var head = new HBoxContainer();
            head.AddThemeConstantOverride("separation", 10);
            head.AddChild(TemperamentEmblem(trait.Id, 34));
            var name = Title(group.Commander, 16, Ui.Colour("text.bright"));
            name.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            head.AddChild(name);
            body.AddChild(head);
            body.AddChild(Title(Loc.Format("{0} · {1:N0} formations · {2:N0} men",
                group.Name, members.Length, members.Sum(army => army.Personnel)), 12, Ui.Colour("text.caption")));
            body.AddChild(Title(Loc.Format("{0} · skill {1:0}% · {2} won / {3} lost · in command since {4}",
                Tr(trait.Name), group.StaffSkill * 100f, group.Victories, group.Defeats,
                group.CommanderSinceDay >= 0 ? LandArrangementSystem.DateOfDay(_campaign, group.CommanderSinceDay) : "—"),
                13, Ui.Colour("text.body")));
            body.AddChild(Title(Tr(trait.Description), 12, Ui.Colour("text.body_soft")));
            if (group.CommandersLost > 0 || group.CommandersRelieved > 0)
                body.AddChild(Title(Loc.Format("Commanders before: {0} fallen, {1} relieved", group.CommandersLost,
                    group.CommandersRelieved), 12, Ui.Colour("text.caption")));

            var may = Generals.MayRelieve(_campaign, n, group, out var refusal);
            var cost = Generals.ReliefWarSupportCost(_campaign, group);
            var open = _generalsReliefGroup == group.Id;
            var relieve = ActionButton(open ? Loc.Get("Keep the commander")
                    : Loc.Format("Relieve of command ({0:0})", _campaign.Defines.CommanderReliefPoliticalCost),
                () =>
                {
                    _generalsReliefGroup = _generalsReliefGroup == group.Id ? -1 : group.Id;
                    _doctrineScreenDay = -1;
                    RefreshDoctrineScreen();
                }, 0, 30, false, "res://game/gfx/interface/icons/icon_military.png");
            relieve.Name = "RelieveCommander_" + group.Id;
            relieve.Disabled = !may && !open;
            relieve.TooltipText = Ui.Tip(!may ? refusal
                : cost > 0f ? Loc.Format("A commander who has won costs war support to remove in wartime: {0:0.#} points.", cost * 100f)
                : Loc.Get("Three officers are put forward; choose who takes the group."));
            body.AddChild(relieve);
            if (!open || !may) continue;

            if (cost > 0f)
                body.AddChild(Title(Loc.Format("The country will not like it: war support −{0:0.#} points.", cost * 100f),
                    12, Ui.Colour("text.accent")));
            var candidates = Generals.Candidates(_campaign, group);
            for (var i = 0; i < candidates.Length; i++)
            {
                var officer = candidates[i];
                var index = i;
                var line = new HBoxContainer();
                line.AddThemeConstantOverride("separation", 8);
                body.AddChild(line);
                var officerTrait = Generals.Trait(officer.Trait);
                var label = Title(Loc.Format("{0} · {1} · skill {2:0}%", officer.Name, Tr(officerTrait.Name), officer.Skill * 100f),
                    13, Ui.Colour("text.body"));
                label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                label.TooltipText = Ui.Tip(Tr(officerTrait.Description));
                label.MouseFilter = MouseFilterEnum.Pass;
                line.AddChild(label);
                var appoint = ActionButton(Loc.Get("Appoint"), () =>
                {
                    if (_campaign?.Player is not { } player) return;
                    _generalsReliefGroup = -1;
                    ApplyDoctrineCommand(new RelieveCommanderCommand { IssuerId = player.Id, GroupId = group.Id, Candidate = index });
                }, 120, 28, true);
                appoint.Name = $"AppointCommander_{group.Id}_{index}";
                appoint.TooltipText = Ui.Tip(Tr(officerTrait.Description));
                line.AddChild(appoint);
            }
        }
    }

    /// <summary>
    /// A temperament's emblem (gfx/interface/icons/icon_general_&lt;id&gt;.png, replaceable by a mod
    /// through the sprite catalogue); a mod's temperament without one shows the plain shield.
    /// </summary>
    private TextureRect TemperamentEmblem(string traitId, int size) => new()
    {
        Texture = Art("icon_general_" + traitId, "res://game/gfx/interface/icons/icon_general_" + traitId + ".png") ??
                  Art("icon_general_steady", "res://game/gfx/interface/icons/icon_general_steady.png"),
        CustomMinimumSize = new Vector2(size, size),
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        MouseFilter = MouseFilterEnum.Ignore
    };

    /// <summary>The right-hand column on the generals page: the temperaments and what each does.</summary>
    private void GeneralsSide(VBoxContainer rows)
    {
        PolicyHeading(rows, "TEMPERAMENTS");
        foreach (var trait in Generals.All)
        {
            var line = new HBoxContainer();
            line.AddThemeConstantOverride("separation", 8);
            var emblem = TemperamentEmblem(trait.Id, 22);
            emblem.SizeFlagsVertical = SizeFlags.ShrinkBegin;
            line.AddChild(emblem);
            var text = Title(Loc.Format("{0} — attack ×{1}, defence ×{2}. {3}", Tr(trait.Name),
                Fmt.Number(trait.Attack), Fmt.Number(trait.Defence), Tr(trait.Description)), 12, Ui.Colour("text.body"));
            text.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            line.AddChild(text);
            rows.AddChild(line);
        }
        PolicyHeading(rows, "THE RULES");
        var d = _campaign.Defines;
        PolicyText(rows, Loc.Format(
            "Relieving a commander costs {0:0} political credit and is possible {1} days after they took the group. In wartime, removing one who has won costs war support: {2:0.#} points for each battle won beyond those lost, at most {3:0.#}. The computer's governments relieve commanders by the same rule.",
            d.CommanderReliefPoliticalCost, d.CommanderReliefCooldownDays,
            d.CommanderReliefWarSupportPerVictory * 100f, d.CommanderReliefWarSupportMax * 100f));
    }
}
