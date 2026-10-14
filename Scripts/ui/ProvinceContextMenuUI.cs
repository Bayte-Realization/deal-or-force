using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The right button on a province, with nothing selected to command: a card at the cursor saying
/// what this ground is and what can be done about it.
///
/// The head names the region and its owner, with the owner's flag, its people and its order, and
/// who holds it if it is occupied. A line of facts follows - what the owner thinks of us, the
/// standoff between us, our network there, the movements that have its people, war damage. Then
/// the actions, by whom they concern: the region itself (open it, recruit, rebuild, its laws, the
/// capital), its owner (diplomacy, a deal, an intelligence network, a reconnaissance flight, the
/// disputes over it), and the movements working there (fund or recognise them abroad, crack down
/// at home). Each is the action a screen already offers, reached from where the eye already is;
/// one that cannot be taken now is greyed, and says why.
/// </summary>
public partial class DealOrForceGame
{
    private Control _provinceCardLayer;
    private PanelContainer _provinceCard;

    private void CloseProvinceCard()
    {
        if (_provinceCardLayer is not null && IsInstanceValid(_provinceCardLayer)) _provinceCardLayer.QueueFree();
        _provinceCardLayer = null;
        _provinceCard = null;
    }

    private void ShowProvinceContextMenu(GlobePick pick)
    {
        var player = _campaign?.Player;
        var map = _campaign?.ProvinceMap;
        if (player is null || map is null || pick.ProvinceId <= 0 || pick.ProvinceId > map.ProvinceCount) return;
        var controllers = _campaign.ProvinceController ?? map.ProvinceToNation;
        var controller = _campaign.Nations.Find(n => n.MapIndex == controllers[pick.ProvinceId]);
        var owner = _campaign.Nations.Find(n => n.MapIndex == map.ProvinceToNation[pick.ProvinceId]) ?? controller;
        if (owner is null) { OpenDiplomacyWithOwnerOf(pick.ProvinceId); return; }
        var mouse = GetViewport().GetMousePosition();
        BuildProvinceCard(pick, owner, controller ?? owner, mouse);
    }

    private void BuildProvinceCard(GlobePick pick, Nation owner, Nation controller, Vector2 at, bool capitalArmed = false)
    {
        CloseProvinceCard();
        var player = _campaign.Player;
        var region = pick.State;

        // A layer over the map that closes the card on any click outside it.
        _provinceCardLayer = new Control { Name = "ProvinceCardLayer", MouseFilter = MouseFilterEnum.Stop };
        _provinceCardLayer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _provinceCardLayer.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true }) CloseProvinceCard();
            else if (e is InputEventKey { Pressed: true, Keycode: Key.Escape }) CloseProvinceCard();
        };
        _hudLayer.AddChild(_provinceCardLayer);
        _provinceCard = TexturedPanel(_provinceCardLayer, PanelSurface.Command);
        _provinceCard.Name = "ProvinceCard";
        _provinceCard.CustomMinimumSize = new Vector2(330, 0);
        _provinceCard.MouseFilter = MouseFilterEnum.Stop;
        var rows = PanelRows(_provinceCard, 14, 12, 6);

        // ---- the head: flag, region, owner, its people and its order.
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 10);
        rows.AddChild(head);
        head.AddChild(FlagBadge(owner.Tag, 30, owner.DisplayName));
        var names = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        names.AddThemeConstantOverride("separation", 0);
        head.AddChild(names);
        names.AddChild(Title(region?.DisplayName ?? owner.DisplayName, 17, Ui.Colour("text.bright")));
        var sub = region is null
            ? owner.DisplayName
            : Simulation.Loc.Format("{0} · {1} people · stability {2}", owner.DisplayName, Fmt.People(region.Population), Fmt.Percent(region.Stability));
        names.AddChild(Title(sub, 11, Ui.Colour("text.caption")));
        var close = ActionButton("×", CloseProvinceCard, 26, 24);
        close.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        head.AddChild(close);

        // ---- the facts.
        var facts = new HFlowContainer();
        facts.AddThemeConstantOverride("h_separation", 6);
        facts.AddThemeConstantOverride("v_separation", 4);
        rows.AddChild(facts);
        void Fact(string text, string token) => facts.AddChild(Title(text, 11, Ui.Colour(token)));
        if (controller.Id != owner.Id) Fact(Simulation.Loc.Format("held by {0}", controller.DisplayName), "state.warning");
        var other = controller.Id != player.Id ? controller : owner.Id != player.Id ? owner : null;
        if (other is not null && other.Movement is null)
        {
            var relation = CampaignDiplomacy.Peek(_campaign, player.Id, other.Id);
            if (CampaignDiplomacy.AtWar(_campaign, player.Id, other.Id)) Fact(Tr("at war with us"), "state.critical");
            else if (relation is not null)
            {
                Fact(Simulation.Loc.Format("opinion {0:+0;-0;0}", relation.Opinion), relation.Opinion < 0 ? "state.warning" : "text.body_soft");
                var stage = Escalation.StageOf(relation);
                if (stage >= TensionStage.Friction) Fact(StageName(stage), stage >= TensionStage.Crisis ? "state.warning" : "text.accent_dim");
            }
            var network = IntelNetworks.Strength(_campaign, player.Id, other.Id);
            if (network > 0f) Fact(Simulation.Loc.Format("our network {0}", Fmt.Percent(network)), "text.accent");
        }
        if (region is not null && region.WarDamage >= 0.02f) Fact(Simulation.Loc.Format("war damage {0}", Fmt.Percent(region.WarDamage)), "state.warning");
        var movements = region is null ? new List<(Nation Movement, float Support)>() : MovementsIn(region);
        foreach (var (movement, support) in movements.Take(2))
            Fact(Simulation.Loc.Format("{0} {1}", movement.DisplayName, Fmt.Percent(support)), "text.body_soft");

        // ---- the actions.
        void Section(string caption) => rows.AddChild(Caption(caption));
        void Action(string icon, string text, System.Action act, string refusal = "", string help = "")
        {
            var button = ActionButton(Tr(text), () => { act(); }, 0, 30);
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.Alignment = HorizontalAlignment.Left;
            var path = SpriteCatalog.TextureOf("icon_" + icon, $"res://game/gfx/interface/icons/icon_{icon}.png");
            if (!string.IsNullOrEmpty(path) && ResourceLoader.Exists(path))
            {
                button.Icon = GD.Load<Texture2D>(path);
                button.ExpandIcon = true;
                button.AddThemeConstantOverride("icon_max_width", 18);
            }
            button.Disabled = refusal.Length > 0 || _observerMode;
            var tip = refusal.Length > 0 ? refusal : help;
            if (tip.Length > 0) button.TooltipText = Ui.Tip(tip);
            rows.AddChild(button);
        }

        var ours = region is not null && region.NationMapIndex == player.MapIndex;
        if (region is not null)
        {
            Section("THIS REGION");
            Action("region", "Open the region", () => { CloseProvinceCard(); OnCountryPicked(pick); },
                help: Tr("Its people, buildings, garrison and what can be built there."));
            if (ours)
            {
                Action("military", "Recruit here", () => { CloseProvinceCard(); OpenRecruitment(region.MapIndex); });
                Action("relief", "Rebuild what the war broke", () =>
                {
                    _campaign.Commands.Enqueue(new RebuildRegionCommand { IssuerId = player.Id, StateMapIndex = region.MapIndex });
                    _campaign.Commands.ApplyAll(_campaign);
                    CloseProvinceCard();
                }, region.WarDamage >= 0.02f ? "" : Tr("Nothing here is in ruins."));
                Action("laws", "Regional laws", () => { CloseProvinceCard(); _selectedState = region; OpenScreen("Laws"); SelectPolicyTab("Regional"); });
                var capitalRefusal = CapitalCore.Refusal(_campaign, player, region);
                Action("nation", capitalArmed
                        ? Simulation.Loc.Format("Confirm: move the capital here ({0})", Fmt.Money(CapitalCore.Cost(_campaign, player)))
                        : "Move the capital here",
                    () =>
                    {
                        // The second press confirms: a capital is not moved by a slip of the hand.
                        if (!capitalArmed) { BuildProvinceCard(pick, owner, controller, at, capitalArmed: true); return; }
                        _campaign.Commands.Enqueue(new MoveCapitalCommand { IssuerId = player.Id, StateMapIndex = region.MapIndex });
                        _campaign.Commands.ApplyAll(_campaign);
                        CloseProvinceCard();
                    }, capitalRefusal.Length > 0 ? Tr(capitalRefusal) : "",
                    Simulation.Loc.Format("The government moves here. It costs {0} and a little order while it settles.", Fmt.Money(CapitalCore.Cost(_campaign, player))));
            }
        }

        var foreign = other;
        if (foreign is not null && foreign.Movement is null)
        {
            Section(foreign.DisplayName.ToUpperInvariant());
            Action("diplomacy", "Diplomacy", () => { CloseProvinceCard(); OpenDiplomacyWithOwnerOf(pick.ProvinceId); });
            var dealOk = Ultimatum.CanNegotiateDeal(_campaign, player, foreign, out var dealRefusal);
            Action("treaty", "Propose a deal", () =>
            {
                CloseProvinceCard();
                _selectedNation = foreign;
                OpenScreen("Diplomacy");
                OpenDealComposer(foreign);
            }, dealOk ? "" : Tr(dealRefusal), Tr("Cash, regions and promises, both sides on one table."));
            if (player.IntelNetworks.ContainsKey(foreign.Id))
                Action("search", Simulation.Loc.Format("Recall the network ({0})", Fmt.Percent(IntelNetworks.Strength(_campaign, player.Id, foreign.Id))),
                    () => { IntelFromMenu(foreign, recall: true); CloseProvinceCard(); });
            else
                Action("search", "Found an intelligence network", () => { IntelFromMenu(foreign, recall: false); CloseProvinceCard(); },
                    IntelNetworks.Refusal(_campaign, player, foreign),
                    Simulation.Loc.Format("Agents in their country, for {0}. It grows week by week and shows more as it grows.", Fmt.Money(IntelNetworks.Cost(_campaign, player))));
            var atWar = CampaignDiplomacy.AtWar(_campaign, player.Id, foreign.Id);
            var flight = ReconFlightFor(player, pick.ProvinceId);
            Action("drone", "Send reconnaissance flights here", () => { ReconFromMenu(flight); CloseProvinceCard(); },
                !atWar ? Tr("Reconnaissance flies over an enemy's ground, in a war.") :
                flight is null ? Tr("No aircraft or drone of ours that can fly reconnaissance reaches this province.") : "");
            if (region is not null)
                Action("unrest", "Disputes over this region", () => { CloseProvinceCard(); _territoryRegion = region.MapIndex; _conflictTab = 3; OpenScreen("Conflicts"); });
        }

        if (movements.Count > 0)
        {
            Section("MOVEMENTS HERE");
            foreach (var (movement, _) in movements.Take(2))
            {
                var action = ours ? "crack_down" : "sponsor";
                var label = ours ? Simulation.Loc.Format("Crack down on {0}", movement.DisplayName) : Simulation.Loc.Format("Fund {0}", movement.DisplayName);
                var refusal = MovementDealings.Refusal(_campaign, player, movement, action);
                var captured = movement;
                Action(ours ? "unrest" : "movement_fundraise", label, () =>
                {
                    _campaign.Commands.Enqueue(new MovementDealingCommand { IssuerId = player.Id, MovementId = captured.Id, Action = action });
                    _campaign.Commands.ApplyAll(_campaign);
                    CloseProvinceCard();
                }, refusal);
            }
        }

        // Down and to the right of the cursor, so the head is never under it; kept on screen.
        _provinceCard.ResetSize();
        var size = _provinceCard.GetCombinedMinimumSize();
        var screen = GetViewport().GetVisibleRect().Size;
        var x = Math.Clamp(at.X + 16f, 8f, MathF.Max(8f, screen.X - size.X - 8f));
        var y = Math.Clamp(at.Y + 10f, 8f, MathF.Max(8f, screen.Y - size.Y - 8f));
        _provinceCard.Position = new Vector2(x, y);
    }

    /// <summary>The movements that have this region's people, the strongest first.</summary>
    private List<(Nation Movement, float Support)> MovementsIn(ScenarioState region)
    {
        var found = new List<(Nation, float)>();
        foreach (var nation in _campaign.Nations)
        {
            if (nation.Movement is not { Dissolved: false } state) continue;
            var support = state.Support.GetValueOrDefault(region.MapIndex);
            if (support >= 0.05f) found.Add((nation, support));
        }
        return found.OrderByDescending(entry => entry.Item2).ThenBy(entry => entry.Item1.Id).ToList();
    }

    private void IntelFromMenu(Nation target, bool recall)
    {
        var command = new IntelNetworkCommand { IssuerId = _campaign.Player.Id, TargetId = target.Id, Action = recall ? "recall" : "found" };
        _campaign.Commands.Enqueue(command);
        _campaign.Commands.ApplyAll(_campaign);
        SetArmyFeedback(recall ? Tr("The network is recalled. What it learned stays in the files.")
            : command.Refusal.Length > 0 ? Tr(command.Refusal) : Tr("The first agents are in place. The network will grow week by week."));
    }

    /// <summary>The formation of ours best placed to fly reconnaissance over a province: in reach, able to, the sharpest eyes first.</summary>
    private SupportFormation ReconFlightFor(Nation player, int province)
    {
        return _campaign.SupportFormations
            .Where(f => f.NationId == player.Id && SupportTypeCatalog.TryGet(f.TypeId, out var type) &&
                        type.Missions.Contains(AirReconnaissance.Mission) && SupportCore.InRange(_campaign, f, type, province))
            .OrderByDescending(f => SupportTypeCatalog.Get(f.TypeId).Recon * f.Strength).ThenBy(f => f.Id).FirstOrDefault();
    }

    private void ReconFromMenu(SupportFormation formation)
    {
        if (formation is null) return;
        _campaign.Commands.Enqueue(new SetSupportMissionCommand { IssuerId = _campaign.Player.Id, FormationId = formation.Id, Mission = AirReconnaissance.Mission });
        _campaign.Commands.ApplyAll(_campaign);
        var type = SupportTypeCatalog.Get(formation.TypeId);
        SetArmyFeedback(Simulation.Loc.Format("{0} flies reconnaissance from its base: the fog lifts over enemy ground within {1:0} km.",
            Tr(type?.Name ?? formation.TypeId), type is null ? 0f : AirReconnaissance.SightKm(_campaign, formation, type)));
    }
}
