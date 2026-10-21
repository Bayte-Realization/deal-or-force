using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyCapitalLoss(DealOrForceGame game)
    {
        var c = game.VerificationWorld; c.IsPaused = true;
        var country = c.NationByTag("RUS"); var enemy = c.NationByTag("DEU");
        var legalProvince = country.CapitalProvinceId; var legalName = country.CapitalName;
        c.SetProvinceController(legalProvince, (ushort)enemy.MapIndex);
        CapitalCore.Tick(c);
        Check(country.GovernmentSeatProvinceId > 0 && country.CapitalProvinceId == legalProvince &&
            country.CapitalName == legalName, "native occupation moves government but preserves legal capital");
        var seat = CapitalCore.EffectiveProvinceId(c, country);
        foreach (var (language,width,height) in new[] { ("english",1920,1080), ("english",1280,720), ("russian",1920,1080), ("russian",1280,720) })
        {
            Loc.Language = language; GetTree().Root.Size = new(width,height); game.Call("RefreshLocalisedText");
            game.VerificationOccupationView(true, CapitalCore.EffectiveLatitude(c, country),
                CapitalCore.EffectiveLongitude(c, country), 5.0f);
            var fp = CampaignFingerprint.Of(c); var random = c.Random.State;
            game.VerificationInspectPick(new GlobePick(country,null,-1,0,0)); await Wait(.4);
            var card = game.FindChild("CountryCard",true,false) as Control;
            var labels = card.FindChildren("*","Label",true,false).OfType<Label>().Select(l => l.Text).ToArray();
            Check(card.IsVisibleInTree() && labels.Contains(Loc.Get("Temporary government seat")) &&
                labels.Contains(CountryNames.Capital(country,c)), "country card shows actual temporary seat: " + language + width);
            Check(game.VerificationSeatMarkerMatches(country), "actual capital star follows the government's effective seat");
            Check(CampaignFingerprint.Of(c) == fp && c.Random.State == random,
                "seat card, map and language are read-only");
            await Capture("capital-evacuated-" + language + "-" + width);
        }
        c.ProvinceMap.ProvinceToNation[legalProvince] = (ushort)enemy.MapIndex;
        c.MarkMapChanged(); CapitalCore.Tick(c); await Wait(.2);
        Check(country.CapitalProvinceId != legalProvince && country.CapitalName != legalName &&
            country.GovernmentSeatProvinceId == 0 && CapitalCore.HasSeat(c,country),
            "native permanent capital loss changes both legal name and map location");
        game.VerificationInspectPick(new GlobePick(country,null,-1,0,0)); await Wait(.2);
        Check(game.VerificationSeatMarkerMatches(country), "map star refreshes after permanent relocation");
        await Capture("capital-permanent-russian-1280");
        // A wholly occupied country must not pretend it still governs from an enemy city.
        for (var p=1; p<=c.ProvinceMap.ProvinceCount; p++)
            if (c.ProvinceMap.ProvinceToNation[p] == country.MapIndex)
                c.SetProvinceController(p,(ushort)enemy.MapIndex);
        CapitalCore.Tick(c); await Wait(.2);
        game.VerificationInspectPick(new GlobePick(country,null,-1,0,0)); await Wait(.2);
        Check(!CapitalCore.HasSeat(c,country) && game.VerificationSeatMarkerMatches(country) &&
            CountryNames.Capital(country,c) == Loc.Get("No government seat"),
            "total occupation shows exile and removes the old capital star");
        await Capture("capital-exile-russian-1280");
        game.Call("CloseMapSelection");
    }
}

public partial class DealOrForceGame
{
    internal bool VerificationSeatMarkerMatches(Nation nation) => _globe.VerificationSeatMarkerMatches(nation);
}

public sealed partial class GlobePresentation
{
    internal bool VerificationSeatMarkerMatches(Nation nation)
    {
        var marker = _capitalRoot.GetNodeOrNull<Node3D>("Capital_" + nation.Tag);
        if (!CapitalCore.HasSeat(_campaign,nation)) return marker is null;
        var latitude = CapitalCore.EffectiveLatitude(_campaign,nation);
        var longitude = CapitalCore.EffectiveLongitude(_campaign,nation);
        var expected = _flatMap ? FlatPoint(latitude,longitude,FlatMarkerZ) :
            SurfacePoint(latitude,longitude,SurfaceMarkerRadius);
        return marker is not null && marker.Position.DistanceTo(expected) < .001f;
    }
}
