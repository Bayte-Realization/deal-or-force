using System;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private readonly byte[] _visionPixels = new byte[256 * 256];
    private readonly byte[] _uploadedVisionBytes = new byte[256 * 256];
    private bool _hasUploadedVisionBytes;
    private ulong _lastFogCheckMsec;
    private int _lastFogMapRevision = int.MinValue;
    private Campaign _uploadedFogCampaign;
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
        var nowMsec = Time.GetTicksMsec();
        if (_hasUploadedVisionBytes && ReferenceEquals(_uploadedFogCampaign, _campaign) &&
            _uploadedVisionViewer == viewer && _lastFogMapRevision == _campaign.MapRevision &&
            !_campaign.IsPaused && _campaign.Speed >= 7 && nowMsec - _lastFogCheckMsec < 120)
            return;
        var vision = CampaignIntel.Vision(_campaign, viewer);
        if (ReferenceEquals(_uploadedVision, vision) && _uploadedVisionRevision == vision.Revision &&
            _uploadedVisionViewer == viewer)
        {
            _lastFogCheckMsec = nowMsec;
            _lastFogMapRevision = _campaign.MapRevision;
            return;
        }
        Array.Clear(_visionPixels);
        for (var p = 1; p <= _campaign.ProvinceMap.ProvinceCount && p < _visionPixels.Length; p++)
            if (vision.Sees(p)) _visionPixels[p] = 255;
        if (_hasUploadedVisionBytes && _uploadedVisionViewer == viewer && _visionPixels.AsSpan().SequenceEqual(_uploadedVisionBytes))
        {
            _uploadedFogCampaign = _campaign;
            _uploadedVision = vision;
            _uploadedVisionRevision = vision.Revision;
            _lastFogCheckMsec = nowMsec;
            _lastFogMapRevision = _campaign.MapRevision;
            return;
        }
        using var timer = SimProfiler.Measure("f:visibility upload");
        Buffer.BlockCopy(_visionPixels, 0, _uploadedVisionBytes, 0, _uploadedVisionBytes.Length);
        _hasUploadedVisionBytes = true;
        _uploadedFogCampaign = _campaign;
        _uploadedVision = vision;
        _uploadedVisionRevision = vision.Revision;
        _uploadedVisionViewer = viewer;
        _lastFogMapRevision = _campaign.MapRevision;
        if (_visionImage is null) _visionImage = Image.CreateFromData(256, 256, false, Image.Format.R8, _visionPixels);
        else _visionImage.SetData(256, 256, false, Image.Format.R8, _visionPixels);
        UpdateMapImage("fog_visibility", _visionImage, ref _visionTexture);
        _lastFogCheckMsec = Time.GetTicksMsec();
        _battleMarkerDay = -1;
    }
}
