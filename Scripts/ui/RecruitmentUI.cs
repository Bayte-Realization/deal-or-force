using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _recruitmentLayer;
    private OptionButton _recruitmentTemplate, _recruitmentRegion;
    private SpinBox _recruitmentCount;
    private HSlider _recruitmentSlider;
    private Label _recruitmentBatchHeading;
    private Label _recruitmentDetails;
    private Button _recruitmentSubmit;
    private VBoxContainer _recruitmentQueue;
    private VBoxContainer _recruitmentStockpileHost;
    private HFlowContainer _recruitmentTemplateCards;
    private HBoxContainer _recruitmentModeButtonsHost;
    private VBoxContainer _recruitmentNavalHost;
    private MobilisationMode _recruitmentMode = MobilisationMode.Standard;
    private readonly Dictionary<MobilisationMode, Button> _recruitmentModeButtons = new();
    private string[] _recruitmentTemplates = Array.Empty<string>();
    private int _recruitmentViewer = -1;
    private string _recruitmentQueueState = "";
    private bool _updatingRecruitmentCount;

    private void OpenRecruitment(int region = -1)
    {
        if (_campaign?.Player is not { IsHuman: true }) return;
        if (_recruitmentLayer is null)
        {
            _recruitmentLayer = FullLayer("Recruitment");
            _hudLayer.AddChild(_recruitmentLayer);
            var shade = new ColorRect { Color = new Color(.015f, .026f, .041f, .82f), MouseFilter = MouseFilterEnum.Stop };
            _recruitmentLayer.AddChild(shade);
            shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            var panel = TexturedPanel(_recruitmentLayer);
            Ui.Place(panel, "modal.ledger");
            var rows = PanelRows(panel, 28, 22, 10);

            var header = new HBoxContainer();
            header.AddThemeConstantOverride("separation", 10);
            rows.AddChild(header);
            var title = Title("RECRUIT, MOBILISE & NAVAL COMMISSIONING", 24, Ui.Colour("text.accent"));
            title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            header.AddChild(title);
            header.AddChild(ActionButton("Production & Lines", () =>
            {
                _recruitmentLayer.Hide();
                OpenScreen("Production");
            }, 170, 32));
            header.AddChild(ActionButton("×", () => _recruitmentLayer.Hide(), 36, 32));

            rows.AddChild(Title("Mobilise field divisions and naval squadrons. Every batch draws directly from your strategic stockpiles, manpower reserves and treasury.", 13, Ui.Colour("text.body_soft")));

            var scroll = new ScrollContainer
            {
                SizeFlagsVertical = SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
            };
            rows.AddChild(scroll);
            var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            body.AddThemeConstantOverride("separation", 12);
            scroll.AddChild(body);

            // 1. Strategic Stockpile Depots Dashboard
            body.AddChild(Title("STRATEGIC STOCKPILE DEPOTS & DAILY BURN", 12, Ui.Colour("text.accent")));
            _recruitmentStockpileHost = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _recruitmentStockpileHost.AddThemeConstantOverride("separation", 6);
            body.AddChild(_recruitmentStockpileHost);

            // 2. Mobilisation Posture Selector
            body.AddChild(Title("1. MOBILISATION POSTURE & SPEED", 12, Ui.Colour("text.accent")));
            _recruitmentModeButtonsHost = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _recruitmentModeButtonsHost.AddThemeConstantOverride("separation", 8);
            body.AddChild(_recruitmentModeButtonsHost);
            foreach (var (mode, label, tip) in new[]
            {
                (MobilisationMode.Standard, "Standard Cadre Training (100% time · 1.0× cost)",
                    "Full peacetime curriculum. Standard batch limit (up to 6 per order), no stability or regional unrest penalty."),
                (MobilisationMode.RapidCallUp, "Rapid Call-Up (40% time · 1.35× cost · dynamic batch)",
                    "Accelerated wartime call-up: 2.5× faster deployment and dynamic batch ceiling based on barracks & population, at +35% treasury and +25% equipment draw."),
                (MobilisationMode.TotalMobilisation, "Total Emergency Mobilisation (15% time · 1.75× cost · mass levy)",
                    "All-out emergency mobilisation: deploys in days (15% training time) and unlocks massive single-order batches, but drains stockpiles heavily (+50%) and costs national stability & regional calm.")
            })
            {
                var captured = mode;
                var btn = ActionButton(Tr(label), () =>
                {
                    _recruitmentMode = captured;
                    RefreshRecruitment();
                }, 0, 36, captured == _recruitmentMode);
                btn.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                btn.TooltipText = Ui.Tip(Tr(tip));
                _recruitmentModeButtons[captured] = btn;
                _recruitmentModeButtonsHost.AddChild(btn);
            }

            // 3. Visual Template Selector Cards + Dropdown kept for compatibility
            body.AddChild(Title("2. FORMATION TEMPLATE", 12, Ui.Colour("text.accent")));
            _recruitmentTemplate = new OptionButton { CustomMinimumSize = new Vector2(0, 36), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            body.AddChild(_recruitmentTemplate);
            _recruitmentTemplate.ItemSelected += _ => RefreshRecruitment();

            _recruitmentTemplateCards = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _recruitmentTemplateCards.AddThemeConstantOverride("h_separation", 8);
            _recruitmentTemplateCards.AddThemeConstantOverride("v_separation", 8);
            body.AddChild(_recruitmentTemplateCards);

            // 4. Training Region & Dynamic Batch Size
            var regionAndCountRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            regionAndCountRow.AddThemeConstantOverride("separation", 16);
            body.AddChild(regionAndCountRow);

            var regCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            regCol.AddThemeConstantOverride("separation", 4);
            regionAndCountRow.AddChild(regCol);
            regCol.AddChild(Title("3. TRAINING & DEPLOYMENT REGION", 12, Ui.Colour("text.caption")));
            _recruitmentRegion = new OptionButton { CustomMinimumSize = new Vector2(0, 38), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            regCol.AddChild(_recruitmentRegion);
            _recruitmentRegion.ItemSelected += _ => RefreshRecruitment();

            var countCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            countCol.AddThemeConstantOverride("separation", 4);
            regionAndCountRow.AddChild(countCol);
            _recruitmentBatchHeading = Title("4. DIVISIONS IN THIS BATCH", 12, Ui.Colour("text.caption"));
            countCol.AddChild(_recruitmentBatchHeading);

            var sliderRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            sliderRow.AddThemeConstantOverride("separation", 8);
            countCol.AddChild(sliderRow);
            _recruitmentSlider = new HSlider
            {
                MinValue = 1, MaxValue = 6, Step = 1, Value = 1,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                SizeFlagsVertical = SizeFlags.ShrinkCenter
            };
            sliderRow.AddChild(_recruitmentSlider);
            _recruitmentCount = new SpinBox { MinValue = 1, MaxValue = 6, Step = 1, Value = 1, CustomMinimumSize = new Vector2(96, 36) };
            sliderRow.AddChild(_recruitmentCount);

            var quickBatchRow = new HBoxContainer();
            quickBatchRow.AddThemeConstantOverride("separation", 6);
            countCol.AddChild(quickBatchRow);
            foreach (var preset in new[] { 1, 5, 10, 25, 50 })
            {
                var p = preset;
                var qBtn = ActionButton($"+{p}", () =>
                {
                    var next = Math.Clamp((int)_recruitmentCount.Value + p, 1, (int)_recruitmentCount.MaxValue);
                    _recruitmentCount.Value = next;
                }, 52, 28);
                quickBatchRow.AddChild(qBtn);
            }
            var maxBtn = ActionButton("MAX", () => _recruitmentCount.Value = _recruitmentCount.MaxValue, 64, 28);
            quickBatchRow.AddChild(maxBtn);

            _recruitmentSlider.ValueChanged += val =>
            {
                if (_updatingRecruitmentCount) return;
                _updatingRecruitmentCount = true;
                _recruitmentCount.Value = val;
                _updatingRecruitmentCount = false;
                RefreshRecruitment();
            };
            _recruitmentCount.ValueChanged += val =>
            {
                if (_updatingRecruitmentCount) return;
                _updatingRecruitmentCount = true;
                _recruitmentSlider.Value = val;
                _updatingRecruitmentCount = false;
                RefreshRecruitment();
            };

            // 5. Detailed Bill & Submit Button
            var billPanel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            billPanel.AddThemeStyleboxOverride("panel", WarInsetStyle(16, 12));
            body.AddChild(billPanel);
            var billCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            billCol.AddThemeConstantOverride("separation", 8);
            billPanel.AddChild(billCol);

            _recruitmentDetails = Title("", 14, Ui.Colour("text.body_soft"));
            _recruitmentDetails.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            billCol.AddChild(_recruitmentDetails);

            _recruitmentSubmit = ActionButton("Begin training", () =>
            {
                _campaign.Commands.Enqueue(new RaiseArmyCommand
                {
                    IssuerId = _campaign.Player.Id,
                    StateMapIndex = _recruitmentRegion.GetSelectedId(),
                    TemplateId = RecruitmentTemplateId(),
                    Divisions = (int)_recruitmentCount.Value,
                    Mode = _recruitmentMode
                });
                _campaign.Commands.ApplyAll(_campaign);
                RefreshRecruitment();
                Refresh();
            }, 220, 42, true);
            billCol.AddChild(_recruitmentSubmit);

            // 6. Active Army Training Queue
            body.AddChild(Title("ACTIVE ARMY TRAINING QUEUE", 16, Ui.Colour("text.accent")));
            body.AddChild(Title("Occupation pauses training. Cancellation returns all recruits and up to 75% of the unspent procurement budget.", 12, Ui.Colour("text.caption")));
            _recruitmentQueue = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _recruitmentQueue.AddThemeConstantOverride("separation", 8);
            body.AddChild(_recruitmentQueue);

            // 7. Naval Shipbuilding & Rapid Fleet Mobilisation
            body.AddChild(Title("NAVAL SHIPYARDS & RAPID FLEET COMMISSIONING", 16, Ui.Colour("text.accent")));
            _recruitmentNavalHost = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _recruitmentNavalHost.AddThemeConstantOverride("separation", 8);
            body.AddChild(_recruitmentNavalHost);

            AddContractorRows(body);
        }
        _recruitmentViewer = _campaign.Player.Id;
        _recruitmentTemplates = UnitTypeCatalog.All.OrderBy(t => t.RecruitmentCost).Select(t => t.Id).ToArray();
        _recruitmentTemplate.Clear();
        foreach (var id in _recruitmentTemplates) _recruitmentTemplate.AddItem(Tr(UnitTypeCatalog.Get(id).Name));
        _recruitmentTemplate.Select(Math.Max(0, Array.IndexOf(_recruitmentTemplates, "line_infantry")));
        _recruitmentRegion.Clear();
        foreach (var state in _campaign.StatesOf(_campaign.Player).OrderByDescending(s => s.Buildings.GetValueOrDefault(BuildingKind.Barracks)).ThenBy(s => s.Name))
        {
            var b = state.Buildings.GetValueOrDefault(BuildingKind.Barracks);
            var label = b > 0 ? $"{state.DisplayName} (★ {b} barracks · {Fmt.People(state.Population)})" : $"{state.DisplayName} ({Fmt.People(state.Population)})";
            _recruitmentRegion.AddItem(label, state.MapIndex);
            if (state.MapIndex == region) _recruitmentRegion.Select(_recruitmentRegion.ItemCount - 1);
        }
        _recruitmentLayer.Show();
        _recruitmentQueueState = "";
        RefreshRecruitment();
    }

    private string RecruitmentTemplateId() => _recruitmentTemplates[Math.Max(0, _recruitmentTemplate.Selected)];

    private void RefreshRecruitment()
    {
        if (_recruitmentLayer is not { Visible: true } || _campaign?.Player is null) return;
        var player = _campaign.Player;
        if (player.Id != _recruitmentViewer) { _recruitmentLayer.Hide(); return; }

        StrategicStockpiles.EnsureSeeded(_campaign, player);

        // Update mode buttons highlight
        foreach (var (m, btn) in _recruitmentModeButtons)
            StyleButton(btn, m == _recruitmentMode);

        // Update stockpile dashboard
        ClearRuntimeChildren(_recruitmentStockpileHost);
        var stockCells = new List<(string, string, string, string)>();
        foreach (var goodId in StrategicStockpiles.TrackedGoods)
        {
            var have = StrategicStockpiles.Get(_campaign, player, goodId);
            var cap = StrategicStockpiles.Capacity(_campaign, player, goodId);
            var gain = player.DailyStockpileGain.GetValueOrDefault(goodId);
            var burn = player.DailyStockpileBurn.GetValueOrDefault(goodId);
            var net = gain - burn;
            var fill = cap <= 0f ? 0f : have / cap;
            var token = have <= 0.1f ? "state.negative" : fill < 0.25f || net < -0.05f ? "state.warning" : "state.positive";
            var valText = $"{have:0.0}/{cap:0} ({net:+0.00;-0.00;0.00}/d)";
            var tip = Simulation.Loc.Format(
                "{0} strategic depot: {1:0.0} of {2:0.0} capacity ({3:P0})\nDaily refill from industry & imports: +{4:0.00}\nDaily military & naval burn: -{5:0.00}\nWhen depleted (0.0), armies and fleets lose supply, equipment and combat effectiveness!",
                Tr(goodId), have, cap, fill, gain, burn);
            stockCells.Add((Tr(goodId), valText, token, tip));
        }
        _recruitmentStockpileHost.AddChild(BuildGrandStrategyMetricsGrid(stockCells.ToArray()));

        // Update visual template cards
        ClearRuntimeChildren(_recruitmentTemplateCards);
        for (var idx = 0; idx < _recruitmentTemplates.Length; idx++)
        {
            var capturedIdx = idx;
            var tmpl = UnitTypeCatalog.Get(_recruitmentTemplates[idx]);
            var selected = _recruitmentTemplate.Selected == idx;
            var days = RecruitmentSystem.TrainingDaysOf(player, tmpl, _recruitmentMode);
            var unitCost = RecruitmentSystem.CostOf(player, tmpl, _recruitmentMode);
            var glyph = CounterGlyph(tmpl.CounterSymbol);
            var cardBtn = ActionButton(
                $"{glyph} {Tr(tmpl.Name)}\n{Fmt.Money(unitCost)} · {days}d · SA {tmpl.SoftAttack:0.0} / HA {tmpl.HardAttack:0.0} / DEF {tmpl.Defense:0.0}",
                () =>
                {
                    _recruitmentTemplate.Select(capturedIdx);
                    RefreshRecruitment();
                }, 215, 52, selected);
            _recruitmentTemplateCards.AddChild(cardBtn);
        }

        var selectedRegionId = _recruitmentRegion.GetSelectedId();
        var selectedState = _campaign.States.Find(s => s.MapIndex == selectedRegionId);
        var maxBatch = RecruitmentSystem.MaxDivisionsPerBatch(_campaign, player, selectedState, _recruitmentMode);
        var queueCap = RecruitmentSystem.DynamicQueueCap(_campaign, player, _recruitmentMode);
        var activeBatches = _campaign.Recruitment.Count(o => o.NationId == player.Id);

        _updatingRecruitmentCount = true;
        _recruitmentCount.MaxValue = maxBatch;
        _recruitmentSlider.MaxValue = maxBatch;
        if (_recruitmentCount.Value > maxBatch) _recruitmentCount.Value = maxBatch;
        _recruitmentSlider.Value = _recruitmentCount.Value;
        _updatingRecruitmentCount = false;

        _recruitmentBatchHeading.Text = Simulation.Loc.Format(
            "4. DIVISIONS IN THIS BATCH · 1–{0} (QUEUE {1}/{2})",
            maxBatch, activeBatches, queueCap);

        var t = UnitTypeCatalog.Get(RecruitmentTemplateId());
        var count = (int)_recruitmentCount.Value;
        var reason = RecruitmentSystem.Validate(_campaign, player.Id, selectedRegionId, t.Id, count, allowDeficit: false, _recruitmentMode);
        var totalCost = RecruitmentSystem.TotalBatchCost(_campaign, player, t, count, _recruitmentMode, out var surcharge);
        var trainDays = RecruitmentSystem.TrainingDaysOf(player, t, _recruitmentMode);
        var bill = StrategicStockpiles.DivisionMaterialBill(t, count, _recruitmentMode);
        var billText = string.Join(" · ", bill.Select(kv =>
        {
            var have = StrategicStockpiles.Get(_campaign, player, kv.Key);
            return $"{Tr(kv.Key)} {kv.Value:0.#} (have {have:0.#})";
        }));

        var modeNote = _recruitmentMode switch
        {
            MobilisationMode.RapidCallUp => Simulation.Loc.Format("Rapid Call-Up: -{0:P1} stability · accelerated 40% training time",
                _campaign.Defines.RapidCallUpStabilityPerDivision * count),
            MobilisationMode.TotalMobilisation => Simulation.Loc.Format("TOTAL MOBILISATION: -{0:P1} stability · emergency 15% training time · regional strain",
                _campaign.Defines.TotalMobilisationStabilityPerDivision * count),
            _ => Tr("Standard peacetime/wartime cadre training (no stability penalty)")
        };

        _recruitmentDetails.Text = Simulation.Loc.Format(
            "{0} × {1} · {2:N0} personnel · {3}\n" +
            "Total cost: {4} ({5} days) · Surcharge for missing depot stock: {6}\n" +
            "Depot draw: {7}\n" +
            "Stats: Soft attack {8:0.00} · Hard attack {9:0.00} · Defense {10:0.00} · Breakthrough {11:0.00} · Armour {12:0.00} · Speed ×{13:0.00}\n" +
            "Available: Treasury {14} · Manpower reserve {15:N0}\n",
            count, Tr(t.Name), count * t.RecruitmentPersonnel, modeNote,
            Fmt.Money(totalCost), trainDays, Fmt.Money(surcharge),
            billText,
            t.SoftAttack, t.HardAttack, t.Defense, t.Breakthrough, t.Armour, t.Speed,
            Fmt.Money(player.Treasury), player.ManpowerReserve) +
            (reason.Length == 0 ? Tr("Ready to fund training and draw equipment from depots.") : Tr(reason));

        _recruitmentSubmit.Text = _recruitmentMode switch
        {
            MobilisationMode.RapidCallUp => Simulation.Loc.Format("RAPID CALL-UP: {0} × {1} ({2}d)", count, Tr(t.Name), trainDays),
            MobilisationMode.TotalMobilisation => Simulation.Loc.Format("EMERGENCY MOBILISE: {0} × {1} ({2}d)", count, Tr(t.Name), trainDays),
            _ => Simulation.Loc.Format("Begin training: {0} × {1} ({2}d)", count, Tr(t.Name), trainDays)
        };
        _recruitmentSubmit.Disabled = reason.Length > 0;

        RefreshRecruitmentNavalSection(player);
        RefreshContractors();

        var queueState = _recruitmentViewer + ":" + string.Join(";", _campaign.Recruitment.Where(o => o.NationId == _recruitmentViewer)
            .Select(o => $"{o.Id}:{o.TrainedDays}:{o.Mode}:{RecruitmentSystem.DeploymentProvince(_campaign, o.NationId, o.StateMapIndex)}"));
        if (queueState == _recruitmentQueueState || InterfaceInteractionGuard.Busy(_recruitmentQueue)) return;
        _recruitmentQueueState = queueState;
        foreach (var child in _recruitmentQueue.GetChildren()) { _recruitmentQueue.RemoveChild(child); child.QueueFree(); }
        foreach (var order in _campaign.Recruitment.Where(o => o.NationId == player.Id))
        {
            var block = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            block.AddThemeStyleboxOverride("panel", WarInsetStyle(12, 8));
            _recruitmentQueue.AddChild(block);
            var row = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            row.AddThemeConstantOverride("separation", 4);
            block.AddChild(row);

            var state = _campaign.States.Find(s => s.MapIndex == order.StateMapIndex);
            var paused = RecruitmentSystem.DeploymentProvince(_campaign, order.NationId, order.StateMapIndex) <= 0;
            var modeBadge = order.Mode == MobilisationMode.TotalMobilisation ? " [EMERGENCY]" :
                            order.Mode == MobilisationMode.RapidCallUp ? " [RAPID]" : "";
            row.AddChild(Title($"{order.Count} × {Tr(UnitTypeCatalog.Get(order.TemplateId).Name)}{modeBadge} — {state?.DisplayName}\n" +
                Loc.Format("{0}/{1} training days", order.TrainedDays, order.DurationDays) + " · " +
                Tr(paused ? "Training paused: region unavailable" : "Training in progress"), 13, Ui.Colour("text.body_soft")));
            row.AddChild(new ProgressBar { MaxValue = Math.Max(1, order.DurationDays), Value = order.TrainedDays, CustomMinimumSize = new Vector2(0, 14) });
            var id = order.Id;
            row.AddChild(ActionButton("Cancel training", () =>
            {
                _campaign.Commands.Enqueue(new CancelRecruitmentCommand { IssuerId = player.Id, OrderId = id });
                _campaign.Commands.ApplyAll(_campaign);
                _recruitmentQueueState = "";
                RefreshRecruitment();
            }, 140, 28));
        }
        if (_recruitmentQueue.GetChildCount() == 0)
            _recruitmentQueue.AddChild(Title("No divisions in training.", 13, Ui.Colour("text.caption")));
    }

    private void RefreshRecruitmentNavalSection(Nation player)
    {
        if (_recruitmentNavalHost is null) return;
        ClearRuntimeChildren(_recruitmentNavalHost);

        var slips = Shipbuilding.Slipways(_campaign, player, _recruitmentMode);
        var fleets = _campaign.Fleets.Where(f => f.NationId == player.Id && f.Hulls > 0).ToList();
        var buildDays = Shipbuilding.BuildDays(_campaign, player) * (_recruitmentMode switch
        {
            MobilisationMode.RapidCallUp => 0.40f,
            MobilisationMode.TotalMobilisation => 0.16f,
            _ => 1f
        });

        _recruitmentNavalHost.AddChild(Title(Simulation.Loc.Format(
            "Active naval slipways: {0:0.#} · Est. hull completion at current posture: ~{1:0} days · Daily yard outlay: {2}",
            slips, MathF.Max(14f, buildDays), Fmt.Money(Shipbuilding.DailyCost(_campaign, player))), 12, Ui.Colour("text.body_soft")));

        var newFleetRow = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        newFleetRow.AddThemeConstantOverride("h_separation", 8);
        newFleetRow.AddThemeConstantOverride("v_separation", 6);
        _recruitmentNavalHost.AddChild(newFleetRow);

        foreach (var (hulls, label) in new[]
        {
            (5, "Commission new 5-hull squadron"),
            (15, "Commission new 15-hull task force"),
            (30, "Mobilise 30-hull battle fleet")
        })
        {
            var h = hulls;
            var cost = _campaign.Defines.HullCost * h * (_recruitmentMode == MobilisationMode.TotalMobilisation ? 1.5f : 1.15f);
            var btn = ActionButton($"{Tr(label)} ({Fmt.Money(cost)})", () =>
            {
                _campaign.Commands.Enqueue(new CommissionNewFleetCommand
                {
                    IssuerId = player.Id,
                    ProvinceId = -1,
                    Hulls = h,
                    Mode = _recruitmentMode
                });
                _campaign.Commands.ApplyAll(_campaign);
                RefreshRecruitment();
                Refresh();
            }, 0, 32, h == 15);
            btn.Disabled = player.Treasury < cost;
            btn.TooltipText = Ui.Tip(Tr("Immediately commissions a new naval squadron in your primary coastal port and lays down the remaining hulls under the selected mobilisation posture."));
            newFleetRow.AddChild(btn);
        }

        foreach (var fleet in fleets)
        {
            var captured = fleet;
            var fCard = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            fCard.AddThemeStyleboxOverride("panel", WarInsetStyle(12, 8));
            _recruitmentNavalHost.AddChild(fCard);

            var fRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            fRow.AddThemeConstantOverride("separation", 10);
            fCard.AddChild(fRow);

            var fInfo = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            fInfo.AddChild(Title($"{Loc.Get(fleet.Name)} · {fleet.Hulls} hulls afloat · {fleet.HullsOrdered} on slipways ({fleet.HullOrderMode})", 13, Ui.Colour("text.bright")));
            fInfo.AddChild(Title(Simulation.Loc.Format("Strength {0:P0} · Supply {1:P0} · Readiness {2:P0}", fleet.Strength, fleet.Supply, fleet.Readiness), 11, Ui.Colour("text.caption")));
            fRow.AddChild(fInfo);

            foreach (var addCount in new[] { 5, 15, 30 })
            {
                var c = addCount;
                var addBtn = ActionButton($"+{c} hulls", () =>
                {
                    _campaign.Commands.Enqueue(new OrderHullsCommand
                    {
                        IssuerId = player.Id,
                        FleetId = captured.Id,
                        Count = c,
                        Mode = _recruitmentMode
                    });
                    _campaign.Commands.ApplyAll(_campaign);
                    RefreshRecruitment();
                    Refresh();
                }, 86, 30);
                fRow.AddChild(addBtn);
            }
            if (fleet.HullsOrdered > 0)
            {
                var cancelBtn = ActionButton("Cancel", () =>
                {
                    _campaign.Commands.Enqueue(new OrderHullsCommand
                    {
                        IssuerId = player.Id,
                        FleetId = captured.Id,
                        Count = -captured.HullsOrdered
                    });
                    _campaign.Commands.ApplyAll(_campaign);
                    RefreshRecruitment();
                    Refresh();
                }, 76, 30);
                fRow.AddChild(cancelBtn);
            }
        }
    }
}
