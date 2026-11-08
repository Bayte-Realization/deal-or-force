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
    private Button _recruitmentSubmit, _recruitmentOnMap;
    private VBoxContainer _recruitmentQueue;
    private VBoxContainer _recruitmentStockpileHost;
    private readonly Dictionary<string, (Label Caption, Label Value)> _recruitmentDepotLabels = new();
    private HFlowContainer _recruitmentTemplateCards;
    private HBoxContainer _recruitmentModeButtonsHost;
    private VBoxContainer _recruitmentNavalHost;
    private MobilisationMode _recruitmentMode = MobilisationMode.Standard;
    private readonly Dictionary<MobilisationMode, Button> _recruitmentModeButtons = new();
    private string[] _recruitmentTemplates = Array.Empty<string>();
    private int _recruitmentViewer = -1;
    private string _recruitmentQueueState = "";
    private bool _updatingRecruitmentCount;

    private void OpenRecruitment(int region = -1, bool preserveSelection = false)
    {
        if (_campaign?.Player is not { IsHuman: true }) return;
        var templateId = preserveSelection ? RecruitmentTemplateId() : "line_infantry";
        if (preserveSelection && _recruitmentRegion is not null) region = _recruitmentRegion.GetSelectedId();
        CancelRecruitmentPlacement(); CancelConstructionPlacement();
        if (_recruitmentLayer is null) BuildRecruitmentWorkbench();
        _recruitmentViewer = _campaign.Player.Id;
        _recruitmentTemplates = UnitTypeCatalog.All.OrderBy(t => t.RecruitmentCost).Select(t => t.Id).ToArray();
        _recruitmentTemplate.Clear();
        foreach (var id in _recruitmentTemplates) _recruitmentTemplate.AddItem(Tr(UnitTypeCatalog.Get(id).Name));
        _recruitmentTemplate.Select(Math.Max(0, Array.IndexOf(_recruitmentTemplates, templateId)));
        _recruitmentRegion.Clear();
        foreach (var state in _campaign.StatesOf(_campaign.Player).OrderByDescending(s => s.Buildings.GetValueOrDefault(BuildingKind.Barracks)).ThenBy(s => s.Name))
        {
            var b = state.Buildings.GetValueOrDefault(BuildingKind.Barracks);
            var label = b > 0 ? Loc.Format("{0} (★ {1} barracks · {2})", state.DisplayName, b, Fmt.People(state.Population)) : $"{state.DisplayName} ({Fmt.People(state.Population)})";
            _recruitmentRegion.AddItem(label, state.MapIndex);
            if (state.MapIndex == region) _recruitmentRegion.Select(_recruitmentRegion.ItemCount - 1);
        }
        ShowRecruitmentPage("army", refresh: false);
        _recruitmentTemplateSearch.Clear();
        RaiseWorkspaceLayer(_recruitmentLayer);
        _recruitmentQueueState = "";
        RefreshRecruitment();
    }

    private string RecruitmentTemplateId() => _recruitmentTemplates.Length == 0 ? "" :
        _recruitmentTemplates[Math.Clamp(_recruitmentTemplate?.Selected ?? 0, 0, _recruitmentTemplates.Length - 1)];

    private void RefreshRecruitment()
    {
        if (_recruitmentLayer is not { Visible: true } || _campaign?.Player is null) return;
        var player = _campaign.Player;
        if (player.Id != _recruitmentViewer) { _recruitmentLayer.Hide(); return; }

        // Update mode buttons highlight
        foreach (var (m, btn) in _recruitmentModeButtons)
            StyleButton(btn, m == _recruitmentMode);

        // Update stockpile dashboard
        if (_recruitmentDepotLabels.Count == 0) BuildRecruitmentDepotRows();
        FitRecruitmentDepotReading();
        foreach (var goodId in StrategicStockpiles.TrackedGoods)
        {
            var have = StrategicStockpiles.Peek(_campaign, player, goodId);
            var cap = StrategicStockpiles.Capacity(_campaign, player, goodId);
            var gain = player.DailyStockpileGain.GetValueOrDefault(goodId);
            var burn = player.DailyStockpileBurn.GetValueOrDefault(goodId);
            var replacements = player.DailyEquipmentReplacement.GetValueOrDefault(goodId);
            var net = gain - burn;
            var fill = cap <= 0f ? 0f : have / cap;
            var token = have <= 0.1f ? "state.negative" : fill < 0.25f || net < -0.05f ? "state.warning" : "state.positive";
            var valText = $"{have:0.0}/{cap:0} ({net:+0.00;-0.00;0.00}/d)";
            var tip = Simulation.Loc.Format(
                "{0} strategic depot: {1:0.0} of {2:0.0} capacity ({3:P0})\nDaily refill from industry & imports: +{4:0.00}\nDaily operations: -{5:0.00}\nDaily durable equipment replacements: -{6:0.00}\nPersonnel losses, durable equipment and ammunition are separate. Deliveries spend real depot materials.",
                RecruitmentDepotName(goodId), have, cap, fill, gain, Math.Max(0f, burn - replacements), replacements);
            var (caption, value) = _recruitmentDepotLabels[goodId];
            caption.Text = RecruitmentDepotName(goodId);
            value.Text = valText;
            value.AddThemeColorOverride("font_color", Ui.Colour(token));
            ((Control)caption.GetParent()).TooltipText = Ui.Tip(tip);
        }

        // A warmed catalogue survives quantity, posture, tab and daily refreshes.
        var liveTemplates = _recruitmentTemplates.ToHashSet(StringComparer.Ordinal);
        foreach (var stale in _recruitmentCardButtons.Keys.Where(id => !liveTemplates.Contains(id)).ToArray())
        {
            var old = _recruitmentCardButtons[stale];
            _recruitmentTemplateCards.RemoveChild(old); old.QueueFree(); _recruitmentCardButtons.Remove(stale);
        }
        for (var idx = 0; idx < _recruitmentTemplates.Length; idx++)
        {
            var tmpl = UnitTypeCatalog.Get(_recruitmentTemplates[idx]);
            var selected = _recruitmentTemplate.Selected == idx;
            var days = RecruitmentSystem.TrainingDaysOf(player, tmpl, _recruitmentMode);
            var unitCost = RecruitmentSystem.CostOf(player, tmpl, _recruitmentMode);
            var glyph = CounterGlyph(tmpl.CounterSymbol);
            if (!_recruitmentCardButtons.TryGetValue(tmpl.Id, out var cardBtn))
            {
                var id = tmpl.Id;
                cardBtn = ActionButton("", () =>
                {
                    _recruitmentTemplate.Select(Array.IndexOf(_recruitmentTemplates, id));
                    RefreshRecruitment();
                }, 210, 66);
                cardBtn.Name = "RecruitmentCard_" + id;
                cardBtn.ClipText = true; cardBtn.SetMeta("atlas_tab_button", true);
                _recruitmentCardButtons[id] = cardBtn; _recruitmentTemplateCards.AddChild(cardBtn);
            }
            cardBtn.Text = $"{glyph} {Tr(tmpl.Name)}\n" + Loc.Format("{0} · {1} training days", Fmt.Money(unitCost), days);
            cardBtn.TooltipText = Ui.Tip(Tr(tmpl.Name) + "\n" + Loc.Format(
                "Stats: Soft attack {0:0.00} · Hard attack {1:0.00} · Defense {2:0.00} · Breakthrough {3:0.00} · Armour {4:0.00} · Speed ×{5:0.00}",
                tmpl.SoftAttack, tmpl.HardAttack, tmpl.Defense, tmpl.Breakthrough, tmpl.Armour, tmpl.Speed));
            StyleButton(cardBtn, selected);
            _recruitmentTemplateCards.MoveChild(cardBtn, idx);
        }
        FilterRecruitmentTemplates();

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
            "Batch size: 1–{0} · training queue {1}/{2}",
            maxBatch, activeBatches, queueCap);

        var t = UnitTypeCatalog.Get(RecruitmentTemplateId());
        var count = (int)_recruitmentCount.Value;
        var reason = RecruitmentSystem.Validate(_campaign, player.Id, selectedRegionId, t.Id, count, allowDeficit: false, _recruitmentMode);
        var totalCost = RecruitmentSystem.TotalBatchCost(_campaign, player, t, count, _recruitmentMode, out var surcharge);
        var trainDays = RecruitmentSystem.TrainingDaysOf(player, t, _recruitmentMode);
        var bill = StrategicStockpiles.DivisionMaterialBill(t, count, _recruitmentMode);
        var billText = string.Join(" · ", bill.Select(kv =>
        {
            var have = StrategicStockpiles.Peek(_campaign, player, kv.Key);
            return Loc.Format("{0} {1:0.#} (available {2:0.#})", RecruitmentDepotName(kv.Key), kv.Value, have);
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
            Fmt.Money(player.Treasury), player.ManpowerReserve);

        _recruitmentOrderTitle.Text = Loc.Format("{0} × {1}", count, Tr(t.Name));
        _recruitmentOrderPrice.Text = Loc.Format("{0} · {1} days · {2:N0} recruits", Fmt.Money(totalCost), trainDays, count * t.RecruitmentPersonnel);
        _recruitmentOrderStatus.Text = reason.Length == 0 ? Tr("Ready to begin training.") : Tr(reason);
        _recruitmentOrderStatus.AddThemeColorOverride("font_color", Ui.Colour(reason.Length == 0 ? "state.positive" : "state.warning"));

        var submitCaption = _recruitmentMode switch
        {
            MobilisationMode.RapidCallUp => Simulation.Loc.Format("RAPID CALL-UP: {0} × {1} ({2}d)", count, Tr(t.Name), trainDays),
            MobilisationMode.TotalMobilisation => Simulation.Loc.Format("EMERGENCY MOBILISE: {0} × {1} ({2}d)", count, Tr(t.Name), trainDays),
            _ => Simulation.Loc.Format("Begin training: {0} × {1} ({2}d)", count, Tr(t.Name), trainDays)
        };
        _recruitmentSubmit.Text = Tr("Begin training");
        _recruitmentSubmit.TooltipText = Ui.Tip(submitCaption);
        _recruitmentSubmit.Disabled = reason.Length > 0;
        _recruitmentOnMap.Disabled = !RecruitmentSystem.Enabled(_campaign) || _recruitmentTemplates.Length == 0;

        if (_recruitmentPages["navy"].Visible) RefreshRecruitmentNavalSection(player);
        if (_recruitmentPages["contractors"].Visible) RefreshContractors();
        _recruitmentPageButtons["queue"].Text = Loc.Format("Training queue ({0}/{1})", activeBatches, queueCap);
        FitRecruitmentWorkbench();

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
            var modeBadge = order.Mode == MobilisationMode.TotalMobilisation ? " · " + Tr("Emergency mobilisation") :
                            order.Mode == MobilisationMode.RapidCallUp ? " · " + Tr("Rapid call-up") : "";
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

            var fRow = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            fRow.AddThemeConstantOverride("separation", 10);
            fCard.AddChild(fRow);

            var fInfo = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            var posture = Tr(fleet.HullOrderMode switch { MobilisationMode.RapidCallUp => "Rapid call-up",
                MobilisationMode.TotalMobilisation => "Emergency mobilisation", _ => "Standard training" });
            fInfo.AddChild(Title(Loc.Format("{0} · {1} hulls afloat · {2} on slipways ({3})",
                Loc.Get(fleet.Name), fleet.Hulls, fleet.HullsOrdered, posture), 13, Ui.Colour("text.bright")));
            fInfo.AddChild(Title(Simulation.Loc.Format("Strength {0:P0} · Supply {1:P0} · Readiness {2:P0}", fleet.Strength, fleet.Supply, fleet.Readiness), 11, Ui.Colour("text.caption")));
            fRow.AddChild(fInfo);

            var fleetActions = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            fleetActions.AddThemeConstantOverride("h_separation", 8);
            fleetActions.AddThemeConstantOverride("v_separation", 6);
            fRow.AddChild(fleetActions);

            foreach (var addCount in new[] { 5, 15, 30 })
            {
                var c = addCount;
                var addBtn = ActionButton(Loc.Format("Order {0} hulls", c), () =>
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
                fleetActions.AddChild(addBtn);
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
                fleetActions.AddChild(cancelBtn);
            }
        }
    }
}
