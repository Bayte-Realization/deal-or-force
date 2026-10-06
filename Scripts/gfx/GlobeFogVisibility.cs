using System;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private readonly byte[] _visionPixels = new byte[256 * 256];
    private ImageTexture _visionTexture;
    private Image _visionImage;
    private ProvinceVision _uploadedVision;
    private int _uploadedVisionRevision = -1;
    private int _uploadedVisionViewer = -1;

    private bool CanShowArmy(Army army) => !FogOfWarActive || CampaignIntel.PlayerCanSeeArmy(_campaign, army);
    private bool CanShowBattle(LandBattle battle) => !FogOfWarActive || CampaignIntel.PlayerCanSeeBattle(_campaign, battle);

    private void UpdateFogVisibility()
    {
        if (!FogOfWarActive || _campaign?.Player is not { IsHuman: true } || _campaign.ProvinceMap is null) return;
        var viewer = _campaign.Player.Id;
        var vision = CampaignIntel.Vision(_campaign, viewer);
        if (ReferenceEquals(_uploadedVision, vision) && _uploadedVisionRevision == vision.Revision &&
            _uploadedVisionViewer == viewer) return;
        using var timer = SimProfiler.Measure("f:visibility upload");
        _uploadedVision = vision;
        _uploadedVisionRevision = vision.Revision;
        _uploadedVisionViewer = viewer;
        Array.Clear(_visionPixels);
        for (var p = 1; p <= _campaign.ProvinceMap.ProvinceCount && p < _visionPixels.Length; p++)
            if (vision.Sees(p)) _visionPixels[p] = 255;
        if (_visionImage is null) _visionImage = Image.CreateFromData(256, 256, false, Image.Format.R8, _visionPixels);
        else _visionImage.SetData(256, 256, false, Image.Format.R8, _visionPixels);
        if (_visionTexture is null)
        {
            _visionTexture = ImageTexture.CreateFromImage(_visionImage);
            _globeMaterial.SetShaderParameter("fog_visibility", _visionTexture);
        }
        else _visionTexture.Update(_visionImage);
        _battleMarkerDay = -1;
    }
}
