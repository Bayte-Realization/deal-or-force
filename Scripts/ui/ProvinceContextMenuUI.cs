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

        // A layer over the map that closes the card on a click outside it.
        // The wheel is not a click. At the end of the list the scroll stops taking it,
        // and the leftover tick used to land here and dismiss the card.
        _provinceCardLayer = new Control { Name = "ProvinceCardLayer", MouseFilter = MouseFilterEnum.Stop };
        _provinceCardLayer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _provinceCardLayer.GuiInput += e =>
        {
            if (e is InputEventMouseButton wheel && wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown
                    or MouseButton.WheelLeft or MouseButton.WheelRight)
            {
                _provinceCardLayer.AcceptEvent();
                return;
            }
            if (e is InputEventMouseButton { Pressed: true }) CloseProvinceCard();
            else if (e is InputEventKey { Pressed: true, Keycode: Key.Escape }) CloseProvinceCard();
        };
        _hudLayer.AddChild(_provinceCardLayer);
        _provinceCard = TexturedPanel(_provinceCardLayer, PanelSurface.Command);
        _provinceCard.Name = "ProvinceCard";
        // Top-left, sized to its rows. A full-rect parent would otherwise stretch this
        // panel down the whole map and leave an empty frame under the last button.
        _provinceCard.SetAnchorsPreset(LayoutPreset.TopLeft);
        _provinceCard.GrowHorizontal = GrowDirection.End;
        _provinceCard.GrowVertical = GrowDirection.End;
        _provinceCard.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        _provinceCard.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        _provinceCard.CustomMinimumSize = new Vector2(320, 0);
        _provinceCard.MouseFilter = MouseFilterEnum.Stop;
        var rows = ScrollablePanelRows(_provinceCard, 10, 8, 3);

        // ---- the head: flag, region, owner, its people and its order.
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 10);
        rows.AddChild(head);
        head.AddChild(FlagBadge(owner.Tag, 30, owner.DisplayName));
        var names = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        names.AddThemeConstantOverride("separation", 0);
        head.AddChild(names);
        names.AddChild(Fixed(Title(region?.DisplayName ?? owner.DisplayName, 17, Ui.Colour("text.bright"))));
        var sub = region is null
            ? owner.DisplayName
            : Simulation.Loc.Format("{0} · {1} people · stability {2}", owner.DisplayName, Fmt.People(region.Population), Fmt.Percent(region.Stability));
        names.AddChild(Fixed(Title(sub, 11, Ui.Colour("text.caption"))));
        var close = ActionButton("×", CloseProvinceCard, 26, 24);
        close.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        head.AddChild(close);

        // ---- the facts.
        var facts = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
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
        if (region is not null)
        {
            var ground = _campaign.ProvinceMap;
            var roads = ground is null ? 1f : WaterConditions.RoadThroughput(_campaign, ground.CenterOf(pick.ProvinceId).Latitude);
            if (roads < 0.995f)
                Fact(Simulation.Loc.Format("winter roads {0:P0}", roads), "text.accent_dim");
            if (region.PoliticalDivergence >= 0f && region.GovernmentApproval >= 0f)
                Fact(Simulation.Loc.Format("laws {0:P0} · government {1:P0}", region.LawApproval, region.GovernmentApproval),
                    region.GovernmentApproval < 0.42f ? "state.warning" : "text.body_soft");
            if (region.SecessionPressure >= 0.08f)
                Fact(Simulation.Loc.Format("leaving {0:P0}", region.SecessionPressure),
                    region.SecessionPressure >= 0.5f ? "state.warning" : "text.accent_dim");
            if (_campaign.FeatureEnabled("political_cohorts"))
            {
                var (_, chip) = PoliticalCohorts.CauseOf(_campaign, owner, region);
                if (chip.Length > 0 && PoliticalCohorts.UnrestHeat(region) >= 0.35f)
                    Fact(chip, "state.warning");
            }
            if (_campaign.FeatureEnabled("historical_norms") && region.RememberedOwner >= 0)
                Fact(region.BorderHabit >= 0.75f
                        ? Tr("an old border")
                        : Simulation.Loc.Format("a new border, {0:0.0} years", region.SettledBorderDays / 365f),
                    region.BorderHabit >= 0.75f ? "text.body_soft" : "state.warning");
        }
        var movements = region is null ? new List<(Nation Movement, float Support)>() : MovementsIn(region);
        foreach (var (movement, support) in movements.Take(2))
            Fact(Simulation.Loc.Format("{0} {1}", movement.DisplayName, Fmt.Percent(support)), "text.body_soft");

        // ---- the actions.
        void Section(string caption) => rows.AddChild(Fixed(Caption(caption)));
        void Action(string icon, string text, System.Action act, string refusal = "", string help = "")
        {
            var button = ActionButton(Tr(text), () => { act(); }, 0, 24);
            // A stable hook for native acceptance. Only this action needs one; naming every
            // button after translated text would put unsupported characters into node names.
            if (text == "Garrison here") button.Name = "ProvinceAction_Garrison_here";
            if (text == "Develop here") button.Name = "ProvinceAction_Develop_here";
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.Alignment = HorizontalAlignment.Left;
            var path = SpriteCatalog.TextureOf("icon_" + icon, $"res://game/gfx/interface/icons/icon_{icon}.png");
            if (!string.IsNullOrEmpty(path) && ResourceLoader.Exists(path))
            {
                button.Icon = GD.Load<Texture2D>(path);
                button.ExpandIcon = true;
                button.AddThemeConstantOverride("icon_max_width", 18);
            }
            button.Disabled = refusal.Length > 0;
            var tip = refusal.Length > 0 ? refusal : help;
            if (_observerMode && refusal.Length == 0 && tip.Length > 0)
                tip = tip + "\n" + Tr("Observer mode still acts as the country in the corner. The other governments keep running.");
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
                Action("construction", "Develop here", () =>
                {
                    CloseProvinceCard();
                    _selectedState = region;
                    OpenEconomyTab("Construction");
                }, help: Tr("Choose what to build in this region. The queue, the price and the effect are on one screen."));
                Action("military", "Recruit here", () => { CloseProvinceCard(); OpenRecruitment(region.MapIndex); });
                var garrison = GarrisonPlanner.NearestFree(_campaign, player, pick.ProvinceId);
                if (garrison is null)
                    Action("military", "Garrison here", () => { },
                        refusal: Tr("No free division: every formation is fighting, marching or at sea."),
                        help: Tr("Station a division here to hold the province. It remains a normal formation with normal upkeep."));
                else
                {
                    var garrisonId = garrison.Id;
                    Action("military", "Garrison here", () =>
                    {
                        OrderGarrison(garrisonId, pick.ProvinceId);
                        CloseProvinceCard();
                    }, help: Tr("Orders the nearest free division to march here and hold the province. It remains a normal formation with normal upkeep and can be ordered away again."));
                }
                if (region.WarDamage >= 0.02f)
                    Action("relief", "Rebuild what the war broke", () =>
                    {
                        _campaign.Commands.Enqueue(new RebuildRegionCommand { IssuerId = player.Id, StateMapIndex = region.MapIndex });
                        _campaign.Commands.ApplyAll(_campaign);
                        CloseProvinceCard();
                    });
                Action("laws", "Regional laws", () => { CloseProvinceCard(); _selectedState = region; OpenScreen("Laws"); SelectPolicyTab("Regional"); });
                if (region.PoliticalDivergence >= 0.35f && region.GovernmentApproval >= 0f && region.GovernmentApproval < 0.5f)
                    Action("unrest", "Why this region is uneasy", () =>
                    {
                        CloseProvinceCard();
                        _crisisSelectedRegion = region.MapIndex;
                        OpenScreen("Laws");
                        SelectPolicyTab("Crises");
                    }, help: Tr("The opinion here, and how close it is to leaving. Opening it changes nothing."));
                var settling = player.DomesticCrisis is { ConciliationUntilDay: >= 0 } memory
                    && memory.ConciliationUntilDay > _campaign.ElapsedDays && memory.ConciliationRegion == region.MapIndex;
                if (settling || PoliticalCohorts.UnrestHeat(region) >= _campaign.Defines.CrisisConciliationHeat || region.SecessionPressure >= 0.12f)
                {
                    var refusal = DomesticCrises.ConciliationRefusal(_campaign, player, region);
                    Action("laws", settling ? "Settlement under way" : "Settle this region", () =>
                    {
                        _campaign.Commands.Enqueue(new ConciliateRegionCommand { IssuerId = player.Id, StateMapIndex = region.MapIndex });
                        _campaign.Commands.ApplyAll(_campaign);
                        CloseProvinceCard();
                    }, refusal, Simulation.Loc.Format(
                        "Pay {0} to cool this region for {1} days: the pull to leave, the anger, and the distance from the government. One region at a time. A border is not redrawn.",
                        Fmt.Money(DomesticCrises.ConciliationCost(_campaign, player, region)),
                        _campaign.Defines.CrisisConciliationDays));
                }
                var capitalRefusal = CapitalCore.Refusal(_campaign, player, region);
                if (capitalRefusal.Length == 0 || capitalArmed)
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

        // ---- forces far away: the selected formations flown and shipped here, where no march goes.
        var chosen = _selectedArmies.Where(a => a.NationId == player.Id && _campaign.Armies.Contains(a)).ToList();
        if (chosen.Count > 0 && pick.ProvinceId > 0)
        {
            Section("OUR SELECTED FORCES");
            var why = Expeditions.AirliftRefusal(_campaign, player, chosen[0], pick.ProvinceId);
            var days = chosen.Max(a => Expeditions.Quote(_campaign, a, pick.ProvinceId).Days);
            var cost = chosen.Sum(a => Expeditions.Quote(_campaign, a, pick.ProvinceId).Cost);
            Action("military", Simulation.Loc.Format("Fly and ship {0} formation(s) here", chosen.Count), () =>
            {
                var moved = 0;
                foreach (var army in chosen)
                {
                    var order = new ExpeditionCommand { IssuerId = player.Id, ArmyId = army.Id, Action = Expeditions.Airlift, TargetId = pick.ProvinceId };
                    _campaign.Commands.Enqueue(order);
                    _campaign.Commands.ApplyAll(_campaign);
                    if (order.Done) moved++;
                }
                SetArmyFeedback(Simulation.Loc.Format("{0} formation(s) on their way by air and sea: ready in {1} day(s).", moved, days));
                CloseProvinceCard();
            }, why, Simulation.Loc.Format(
                "By air and sea, where no march goes - an exclave, an ally across the water, a base abroad: {0} day(s) out of action and {1}. They arrive unready to attack.",
                days, Fmt.Money(cost)));
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
            if (atWar)
            {
                var flight = ReconFlightFor(player, pick.ProvinceId);
                Action("drone", "Send reconnaissance flights here", () => { ReconFromMenu(flight); CloseProvinceCard(); },
                    flight is null ? Tr("No aircraft or drone of ours that can fly reconnaissance reaches this province.") : "");
            }
            // Volunteers: our formations under their flag in their war, while we stay at peace.
            if (!atWar && _campaign.Wars.Any(w => !w.Ended && w.Involves(foreign.Id)))
            {
                var host = foreign;
                var refusal = chosen.Count == 0 ? Tr("Select the divisions to send first.") : Expeditions.VolunteerRefusal(_campaign, player, host);
                Action("military", Simulation.Loc.Format("Send {0} formation(s) as volunteers", chosen.Count), () =>
                {
                    var sent = 0;
                    foreach (var army in chosen)
                    {
                        var order = new ExpeditionCommand { IssuerId = player.Id, ArmyId = army.Id, Action = Expeditions.Volunteer, TargetId = host.Id };
                        _campaign.Commands.Enqueue(order);
                        _campaign.Commands.ApplyAll(_campaign);
                        if (order.Done) sent++;
                    }
                    _selectedArmies.Clear();
                    SetArmyFeedback(Simulation.Loc.Format("{0} formation(s) leave to fight for {1}. They come home when its war ends.", sent, host.DisplayName));
                    CloseProvinceCard();
                }, refusal, Simulation.Loc.Format(
                    "Our divisions fight under {0}'s flag and command until its war ends; we stay at peace. {0} will remember it - and so will everyone it fights. Costs {1:0} political credit.",
                    host.DisplayName, _campaign.Defines.VolunteerPoliticalCost));
            }
            var ourVolunteers = _campaign.Armies.Where(a => a.LentById == player.Id && a.NationId == foreign.Id).ToList();
            if (ourVolunteers.Count > 0)
                Action("military", Simulation.Loc.Format("Call our volunteers home ({0})", ourVolunteers.Count), () =>
                {
                    foreach (var army in ourVolunteers)
                    {
                        _campaign.Commands.Enqueue(new ExpeditionCommand { IssuerId = player.Id, ArmyId = army.Id, Action = Expeditions.Recall });
                        _campaign.Commands.ApplyAll(_campaign);
                    }
                    CloseProvinceCard();
                }, help: Tr("Formations in battle finish it first."));
            if (region is not null)
                Action("unrest", "Disputes over this region", () => { CloseProvinceCard(); _territoryRegion = region.MapIndex; _conflictTab = 3; OpenScreen("Conflicts"); });
            AddCovertCardActions(foreign, region, Section, Action);
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

        if (ours && region is not null)
        {
            Nation hosted = null;
            foreach (var nation in _campaign.Nations)
            {
                var hq = nation.Movement;
                if (hq is not { Landless: true, Dissolved: false }) continue;
                if (hq.HeadquartersStateMapIndex != region.MapIndex) continue;
                if (Movements.HostOf(_campaign, nation) != player) continue;
                if (MovementRules.PatienceChip(_campaign, nation).Length == 0) continue;
                hosted = nation;
                break;
            }
            if (hosted is not null)
            {
                Fact(MovementRules.PatienceChip(_campaign, hosted), "state.warning");
                var expel = MovementDealings.Refusal(_campaign, player, hosted, "expel");
                var guest = hosted;
                Action("close", "Expel the headquarters", () =>
                {
                    _campaign.Commands.Enqueue(new MovementDealingCommand { IssuerId = player.Id, MovementId = guest.Id, Action = "expel" });
                    _campaign.Commands.ApplyAll(_campaign);
                    CloseProvinceCard();
                }, expel, MovementRules.Patience(_campaign, guest));
            }
        }

        // Down and to the right of the cursor, so the head is never under it; kept on screen.
        // The scroll does not pass its child's height up, so the card is measured from the
        // rows and then capped. Anything past about half the window scrolls inside the card.
        var content = rows.GetCombinedMinimumSize();
        var screen = GetViewport().GetVisibleRect().Size;
        var width = Math.Max(320f, content.X + 4f);
        var height = Math.Min(Math.Max(content.Y + 4f, 72f), screen.Y * 0.46f);
        _provinceCard.Size = new Vector2(width, height);
        var x = Math.Clamp(at.X + 16f, 8f, MathF.Max(8f, screen.X - width - 8f));
        var y = Math.Clamp(at.Y + 10f, 8f, MathF.Max(8f, screen.Y - height - 8f));
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

    private ArmyMoveResult _lastGarrisonResult;

    private ArmyMoveResult OrderGarrison(int armyId, int provinceId)
    {
        var move = new MoveArmyCommand { IssuerId = _campaign.Player.Id, ArmyId = armyId, TargetProvinceId = provinceId };
        move.Apply(_campaign);
        _lastGarrisonResult = move.Result;
        SetArmyFeedback(GarrisonPlanner.ResultText(move.Result));
        return move.Result;
    }

    /// <summary>
    /// Native acceptance for the province card's development action: the button a player
    /// presses must open Economy → Construction with this exact region selected, not merely
    /// navigate somewhere called "Economy".
    /// </summary>
    internal bool VerificationDevelop(out string report)
    {
        report = "";
        var map = _campaign?.ProvinceMap;
        var player = _campaign?.Player;
        if (map is null || player is null) { report = "no campaign"; return false; }
        var province = 0;
        for (var candidate = 1; candidate <= map.ProvinceCount; candidate++)
        {
            if (map.ProvinceToNation[candidate] != player.MapIndex || map.IsWater(candidate)) continue;
            province = candidate;
            break;
        }
        if (province == 0) { report = "no owned land province"; return false; }
        var state = _campaign.States.FirstOrDefault(entry => entry.MapIndex == map.ProvinceToState[province]);
        if (state is null) { report = "province has no region"; return false; }
        var centre = map.CenterOf(province);
        ShowProvinceContextMenu(new GlobePick(player, state, province, centre.Latitude, centre.Longitude));
        var button = _provinceCard?.FindChild("ProvinceAction_Develop_here", true, false) as Button;
        if (button is null) { report = "development button missing"; CloseProvinceCard(); return false; }
        if (button.Disabled) { report = "development button disabled: " + button.TooltipText; CloseProvinceCard(); return false; }
        button.EmitSignal(BaseButton.SignalName.Pressed);
        var reached = _dossierScreen == "Economy" && _dossierTab == "Construction" &&
                      _selectedState?.MapIndex == state.MapIndex;
        report = reached
            ? "Economy/Construction opened on region " + state.MapIndex
            : $"wrong destination: screen={_dossierScreen} tab={_dossierTab} region={_selectedState?.MapIndex}";
        CloseProvinceCard();
        return reached;
    }

    /// <summary>
    /// Native acceptance for the province card's garrison action: open the card for a real
    /// province of the player, press the button a player would press, and read the real
    /// campaign for a division that now has that province as its destination. UI text is not
    /// enough; the order has to reach the simulation.
    /// </summary>
    internal bool VerificationGarrison(out string report)
    {
        report = "";
        var player = _campaign?.Player;
        var map = _campaign?.ProvinceMap;
        if (player is null || map is null) { report = "no campaign"; return false; }
        var province = 0;
        for (var candidate = 1; candidate <= map.ProvinceCount; candidate++)
        {
            if (map.ProvinceToNation[candidate] != player.MapIndex || map.IsWater(candidate)) continue;
            if (_campaign.ProvinceController[candidate] != player.MapIndex) continue;
            if (_campaign.Armies.Any(army => army.ProvinceId == candidate)) continue;
            if (GarrisonPlanner.NearestFree(_campaign, player, candidate) is not { } nearest) continue;
            var route = map.FindRoute(nearest.ProvinceId, candidate, (ushort)player.MapIndex,
                _campaign.ProvinceController, allowUncontrolledStart: true);
            if (route.Length == 0) continue;
            province = candidate;
            break;
        }
        if (province == 0) { report = "no free province or no free division"; return false; }
        var state = _campaign.States.FirstOrDefault(entry => entry.MapIndex == map.ProvinceToState[province]);
        if (state is null) { report = "province has no region"; return false; }
        var centre = map.CenterOf(province);
        ShowProvinceContextMenu(new GlobePick(player, state, province, centre.Latitude, centre.Longitude));
        var button = _provinceCard?.FindChild("ProvinceAction_Garrison_here", true, false) as Button;
        if (button is null) { report = "garrison button missing"; CloseProvinceCard(); return false; }
        if (button.Disabled) { report = "garrison button disabled: " + button.TooltipText; CloseProvinceCard(); return false; }
        button.EmitSignal(BaseButton.SignalName.Pressed);
        var ordered = _lastGarrisonResult is ArmyMoveResult.Issued or ArmyMoveResult.AttackIssued or ArmyMoveResult.QueuedAfterBattle;
        report = ordered ? "division ordered to province " + province : "order refused: " + _lastGarrisonResult;
        CloseProvinceCard();
        return ordered;
    }
}
