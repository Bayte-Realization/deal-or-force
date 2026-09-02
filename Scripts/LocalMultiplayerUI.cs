using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _handoverLayer = null!;
    private PanelContainer _hotseatHud = null!;
    private Label _hotseatStatus = null!;
    private Label _handoverTitle = null!;
    private Label _handoverHint = null!;

    private void BuildHotseatInterface()
    {
        _hotseatHud = TexturedPanel(_hudLayer);
        _hotseatHud.AnchorLeft = 0;
        _hotseatHud.AnchorRight = 0;
        _hotseatHud.AnchorTop = 0;
        _hotseatHud.AnchorBottom = 0;
        _hotseatHud.OffsetLeft = 14;
        _hotseatHud.OffsetRight = 294;
        _hotseatHud.OffsetTop = 146;
        _hotseatHud.OffsetBottom = 224;
        _hotseatHud.Visible = false;
        var hudRows = PanelRows(_hotseatHud, 12, 8, 3);
        _hotseatStatus = Title("PLAYER 1 / 2", 12, Ui.Colour("text.accent"));
        hudRows.AddChild(_hotseatStatus);
        hudRows.AddChild(ActionButton("PASS TO NEXT PLAYER", PassHotseat, 240, 31, true));

        _handoverLayer = FullLayer("HotseatHandover");
        _handoverLayer.MouseFilter = MouseFilterEnum.Stop;
        _handoverLayer.Visible = false;
        AddChild(_handoverLayer);
        var veil = new ColorRect
        {
            Color = new Color(0.012f, 0.018f, 0.028f, 0.985f),
            MouseFilter = MouseFilterEnum.Stop
        };
        veil.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _handoverLayer.AddChild(veil);
        var panel = TexturedPanel(_handoverLayer);
        panel.AnchorLeft = 0.5f;
        panel.AnchorRight = 0.5f;
        panel.AnchorTop = 0.5f;
        panel.AnchorBottom = 0.5f;
        panel.OffsetLeft = -250;
        panel.OffsetRight = 250;
        panel.OffsetTop = -160;
        panel.OffsetBottom = 160;
        var rows = PanelRows(panel, 30, 26, 14);
        rows.AddChild(Title("PRIVATE HANDOVER", 15, Ui.Colour("text.caption")));
        _handoverTitle = Title("PLAYER 1", 34, Ui.Colour("text.accent"));
        rows.AddChild(_handoverTitle);
        _handoverHint = Title("Pass the device. The map and national information are hidden.", 16, Ui.Colour("text.body_soft"));
        _handoverHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_handoverHint);
        rows.AddChild(ActionButton("I AM READY", AcceptHotseat, 330, 48, true));
    }

    private void PassHotseat()
    {
        if (!_campaign.IsHotseat || !_campaign.NextSeat()) return;
        _selectedArmy = null;
        _selectedNation = _campaign.Player;
        _globe.SelectArmy(-1);
        _sidePanel.Visible = false;
        _openScreen = "";
        if (!string.IsNullOrWhiteSpace(_campaign.Player.CapitalName))
            _globe.FocusOn(_campaign.Player.CapitalLatitude, _campaign.Player.CapitalLongitude, 8.4f);
        ShowSeatHandover();
    }

    private void ShowSeatHandover()
    {
        _campaign.IsPaused = true;
        _pause.Text = "▶";
        UpdateHotseatHud();
        _handoverTitle.Text = $"PLAYER {_campaign.ActiveSeat + 1}: {_campaign.Player.Name}";
        _handoverHint.Text = $"Pass the device to the {_campaign.Player.Tag} player. " +
                             "The previous country's panels and orders are hidden until they are ready.";
        _handoverLayer.Visible = true;
    }

    private void AcceptHotseat()
    {
        _handoverLayer.Visible = false;
        UpdateHotseatHud();
        Refresh();
    }

    private void UpdateHotseatHud()
    {
        if (_hotseatHud is null || _campaign is null) return;
        _hotseatHud.Visible = _hudLayer.Visible && _campaign.IsHotseat && !_networkConnected;
        if (!_campaign.IsHotseat || _networkConnected) return;
        _hotseatStatus.Text = $"PLAYER {_campaign.ActiveSeat + 1} / {_campaign.HumanSeats.Count} · {_campaign.Player.Tag}";
    }
}
