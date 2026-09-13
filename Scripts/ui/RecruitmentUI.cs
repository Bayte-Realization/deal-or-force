using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _recruitmentLayer;
    private OptionButton _recruitmentTemplate, _recruitmentRegion;
    private SpinBox _recruitmentCount;
    private Label _recruitmentDetails;
    private Button _recruitmentSubmit;
    private VBoxContainer _recruitmentQueue;
    private string[] _recruitmentTemplates = Array.Empty<string>();
    private int _recruitmentViewer = -1;
    private string _recruitmentQueueState = "";

    private void OpenRecruitment(int region = -1)
    {
        if (_campaign?.Player is not { IsHuman: true }) return;
        if (_recruitmentLayer is null)
        {
            _recruitmentLayer = FullLayer("Recruitment");
            _hudLayer.AddChild(_recruitmentLayer);
            var shade = new ColorRect { Color = new Color(.015f,.026f,.041f,.8f), MouseFilter = MouseFilterEnum.Stop };
            _recruitmentLayer.AddChild(shade);
            shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            var panel = TexturedPanel(_recruitmentLayer);
            Ui.Place(panel, "modal.ledger");
            var rows = PanelRows(panel, 32, 28, 12);
            var header = new HBoxContainer();
            rows.AddChild(header);
            var title = Title("RECRUIT & DEPLOY", 25, Ui.Colour("text.accent"));
            title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            header.AddChild(title);
            header.AddChild(ActionButton("×", () => _recruitmentLayer.Hide(), 34, 32));
            rows.AddChild(Title("Choose a formation. Personnel and procurement are reserved immediately; trained divisions deploy in the selected region.", 14, Ui.Colour("text.body_soft")));
            var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            rows.AddChild(scroll);
            var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            body.AddThemeConstantOverride("separation", 12);
            scroll.AddChild(body);
            body.AddChild(Title("FORMATION TEMPLATE", 12, Ui.Colour("text.caption")));
            _recruitmentTemplate = new OptionButton { CustomMinimumSize = new Vector2(0, 40) };
            body.AddChild(_recruitmentTemplate);
            _recruitmentTemplate.ItemSelected += _ => RefreshRecruitment();
            body.AddChild(Title("TRAINING REGION", 12, Ui.Colour("text.caption")));
            _recruitmentRegion = new OptionButton { CustomMinimumSize = new Vector2(0,40) };
            body.AddChild(_recruitmentRegion);
            _recruitmentRegion.ItemSelected += _ => RefreshRecruitment();
            body.AddChild(Title("INDEPENDENT DIVISIONS IN THIS BATCH · 1–6", 12, Ui.Colour("text.caption")));
            _recruitmentCount = new SpinBox { MinValue = 1, MaxValue = 6, Step = 1, Value = 1 };
            body.AddChild(_recruitmentCount);
            _recruitmentCount.ValueChanged += _ => RefreshRecruitment();
            _recruitmentDetails = Title("", 16, Ui.Colour("text.body_soft"));
            body.AddChild(_recruitmentDetails);
            _recruitmentSubmit = ActionButton("Begin training", () =>
            {
                _campaign.Commands.Enqueue(new RaiseArmyCommand { IssuerId = _campaign.Player.Id,
                    StateMapIndex = _recruitmentRegion.GetSelectedId(), TemplateId = RecruitmentTemplateId(),
                    Divisions = (int)_recruitmentCount.Value });
                _campaign.Commands.ApplyAll(_campaign);
                RefreshRecruitment();
            }, 180, 42, true);
            body.AddChild(_recruitmentSubmit);
            body.AddChild(Title("TRAINING QUEUE", 18, Ui.Colour("text.accent")));
            body.AddChild(Title("Occupation pauses training. Cancellation returns all recruits and up to 75% of the unspent procurement budget.", 13, Ui.Colour("text.caption")));
            _recruitmentQueue = new VBoxContainer();
            _recruitmentQueue.AddThemeConstantOverride("separation", 8);
            body.AddChild(_recruitmentQueue);
        }
        _recruitmentViewer = _campaign.Player.Id;
        _recruitmentTemplates = UnitTypeCatalog.All.OrderBy(t => t.RecruitmentCost).Select(t => t.Id).ToArray();
        _recruitmentTemplate.Clear();
        foreach (var id in _recruitmentTemplates) _recruitmentTemplate.AddItem(UnitTypeCatalog.Get(id).Name);
        _recruitmentTemplate.Select(Math.Max(0, Array.IndexOf(_recruitmentTemplates, "line_infantry")));
        _recruitmentRegion.Clear();
        foreach (var state in _campaign.StatesOf(_campaign.Player).OrderBy(s => s.Name))
        {
            _recruitmentRegion.AddItem(state.DisplayName, state.MapIndex);
            if (state.MapIndex == region) _recruitmentRegion.Select(_recruitmentRegion.ItemCount - 1);
        }
        _recruitmentLayer.Show();
        _recruitmentQueueState = "";
        RefreshRecruitment();
    }

    private string RecruitmentTemplateId() => _recruitmentTemplates[Math.Max(0, _recruitmentTemplate.Selected)];

    private void RefreshRecruitment()
    {
        if (_recruitmentLayer is not { Visible: true }) return;
        if (_campaign.Player.Id != _recruitmentViewer) { _recruitmentLayer.Hide(); return; }
        var t = UnitTypeCatalog.Get(RecruitmentTemplateId());
        var count = (int)_recruitmentCount.Value;
        var reason = RecruitmentSystem.Validate(_campaign, _campaign.Player.Id, _recruitmentRegion.GetSelectedId(), t.Id, count);
        _recruitmentDetails.Text = Simulation.Loc.Format(
            "{0} × {1} · {2:N0} personnel\n" +
            "Procurement & training: ${3:0.00}B · {4} days\n" +
            "Soft attack {5:0.00} · Hard attack {6:0.00} · Armour {7:0.00}\n" +
            "Speed ×{8:0.00} · Combat width {9:0} · Supply ×{10:0.00}\n" +
            "Available: ${11:0.00}B · {12:N0} personnel\n",
            count, t.Name, count * t.RecruitmentPersonnel, count * t.RecruitmentCost, t.TrainingDays,
            t.SoftAttack, t.HardAttack, t.Armour, t.Speed, t.CombatWidth, t.SupplyUse,
            _campaign.Player.Treasury, _campaign.Player.ManpowerReserve) +
            (reason.Length == 0 ? Tr("Ready to fund training.") : reason);
        _recruitmentSubmit.Disabled = reason.Length > 0;
        var queueState = _recruitmentViewer + ":" + string.Join(";", _campaign.Recruitment.Where(o => o.NationId == _recruitmentViewer)
            .Select(o => $"{o.Id}:{o.TrainedDays}:{RecruitmentSystem.DeploymentProvince(_campaign, o.NationId, o.StateMapIndex)}"));
        // Do not replace a focused/pressed Cancel button on every HUD refresh.
        if (queueState == _recruitmentQueueState) return;
        _recruitmentQueueState = queueState;
        foreach (var child in _recruitmentQueue.GetChildren()) { _recruitmentQueue.RemoveChild(child); child.QueueFree(); }
        foreach (var order in _campaign.Recruitment.Where(o => o.NationId == _campaign.Player.Id))
        {
            var block = new VBoxContainer();
            _recruitmentQueue.AddChild(block);
            var state = _campaign.States.Find(s => s.MapIndex == order.StateMapIndex);
            var paused = RecruitmentSystem.DeploymentProvince(_campaign, order.NationId, order.StateMapIndex) <= 0;
            block.AddChild(Title($"{order.Count} × {UnitTypeCatalog.Get(order.TemplateId).Name} — {state?.Name}\n" +
                $"{order.TrainedDays}/{order.DurationDays} days" + (paused ? " · PAUSED — region unavailable" : " · Training"), 14, Ui.Colour("text.body_soft")));
            block.AddChild(new ProgressBar { MaxValue = order.DurationDays, Value = order.TrainedDays, CustomMinimumSize = new Vector2(0,18) });
            var id = order.Id;
            block.AddChild(ActionButton("Cancel training", () =>
            {
                _campaign.Commands.Enqueue(new CancelRecruitmentCommand { IssuerId = _campaign.Player.Id, OrderId = id });
                _campaign.Commands.ApplyAll(_campaign);
                RefreshRecruitment();
            }, 140, 30));
        }
        if (_recruitmentQueue.GetChildCount() == 0) _recruitmentQueue.AddChild(Title("No divisions in training.", 14, Ui.Colour("text.caption")));
    }
}
