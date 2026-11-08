using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifySurfaceBattlesAndNationalFlags(DealOrForceGame game)
    {
        var c = game.VerificationWorld; c.IsPaused = true;
        var russia = c.NationByTag("RUS"); var ukraine = c.NationByTag("UKR");
        var a = c.Armies.Where(a => a.NationId == russia.Id && a.ProvinceId > 0)
            .OrderBy(a => { var p = c.ProvinceMap.CenterOf(a.ProvinceId);
                return Math.Abs(p.Latitude - 49) + Math.Abs(p.Longitude - 40); }).First();
        var battle = new LandBattle { Id = c.Battles.Select(b => b.Id).DefaultIfEmpty(0).Max() + 1,
            AttackerId = russia.Id, DefenderId = ukraine.Id, ProvinceId = a.ProvinceId,
            WarId = c.Wars.First(w => !w.Ended && w.Involves(russia.Id) && w.Involves(ukraine.Id)).Id };
        battle.Attackers.Add(a.Id);
        battle.Rounds.Add(new LandBattle.Round { AttackerOrganisation = .8f, DefenderOrganisation = .6f,
            AttackerDamage = .05f, DefenderDamage = .07f }); c.Battles.Add(battle);
        var geo = c.ProvinceMap.CenterOf(a.ProvinceId); var before = CampaignFingerprint.Of(c);
        foreach (var flat in new[] { true, false })
        {
            game.VerificationSurfaceBattleView(flat, geo.Latitude, geo.Longitude, 5.8f); await Wait(2);
            var state = game.VerificationSurfaceBattleState(battle.Id);
            Check(state.Surface && state.Depth, "battle badge is a non-billboard depth-tested surface quad " + flat);
            Check(state.Visible && state.Hit, "surface battle remains visible and opens through its tilted hit plane " + flat);
            Check(game.VerificationSurfaceBattleInk(battle.Id), "battle badge has visible rendered ink inside the viewport " + flat);
            await Capture(flat ? "battle-attached-flat" : "battle-attached-globe");
        }
        game.VerificationSurfaceBattleView(false, -geo.Latitude, geo.Longitude + 180, 7.4f); await Wait(2);
        var hidden = game.VerificationSurfaceBattleState(battle.Id);
        Check(!hidden.Visible && !hidden.Hit, "backside battle is neither drawn through the globe nor clickable");
        await Capture("battle-hidden-behind-globe");
        Check(CampaignFingerprint.Of(c) == before, "surface battle render, camera rotation and hit queries mutate no campaign state");

        var old = russia.Ideology;
        russia.Ideology = "communism"; game.VerificationRefreshHud(); await Wait(.5);
        Check(game.VerificationArmyFlagsMatch("RUS"), "existing Russian counters refresh to the same Soviet flag as diplomacy without moving");
        russia.Ideology = old; game.VerificationRefreshHud(); await Wait(.5);
        Check(game.VerificationArmyFlagsMatch("RUS"), "the same stationary counters return to the national flag");
        var regional = new Nation { Id = c.Nations.Max(n => n.Id) + 1, MapIndex = 64000, Tag = "UFT",
            Name = "Flag verification region", FoundNameKind = "region", FoundNameId = "flag-proof",
            SecededFromId = russia.Id, Ideology = "nationalism", Government = "provisional_government",
            DeclaredCulture = "russian", Color = "#4f87ac", Gdp = 1, Population = 1000000 };
        c.Nations.Add(regional);
        Check(game.VerificationRegionalFlagChip(regional), "a seceded regional country's actual UI banner, not its generic fallback, is painted into map counters");
        c.Nations.Remove(regional);
    }
}

public partial class DealOrForceGame
{
    internal void VerificationSurfaceBattleView(bool flat, float lat, float lon, float distance)
    { CloseMapSelection(); _globe.SetFlatMap(flat); _globe.FocusOn(lat, lon, distance); }
    internal (bool Surface, bool Depth, bool Visible, bool Hit) VerificationSurfaceBattleState(int id) => _globe.VerificationSurfaceBattleState(id);
    internal bool VerificationArmyFlagsMatch(string tag) => _globe.VerificationArmyFlagsMatch(tag);
    internal bool VerificationRegionalFlagChip(Nation n) => _globe.VerificationRegionalFlagChip(n);
    internal bool VerificationSurfaceBattleInk(int id) => _globe.VerificationSurfaceBattleInk(id);
}

public sealed partial class GlobePresentation
{
    internal bool VerificationSurfaceBattleInk(int id)
    {
        var node = _battleMarkers[id].Node;
        var plate = node.GetNode<SurfaceBattleBadge>("SurfaceBadge").Plate;
        using var ink = plate.Texture.GetImage();
        var visible = 0;
        for (var y = 0; y < ink.GetHeight(); y++) for (var x = 0; x < ink.GetWidth(); x++)
            if (ink.GetPixel(x, y).A > .1f) visible++;
        var screen = _camera.UnprojectPosition(node.GlobalPosition);
        GD.Print($"BATTLE_PROOF ink={visible} screen={screen} width={node.Scale.X} position={node.GlobalPosition}");
        return visible > 100 && new Rect2(Vector2.Zero, Size).HasPoint(screen);
    }
    internal (bool Surface, bool Depth, bool Visible, bool Hit) VerificationSurfaceBattleState(int id)
    {
        if (!_battleMarkers.TryGetValue(id, out var marker) || marker.Node.GetNodeOrNull<SurfaceBattleBadge>("SurfaceBadge")?.Plate is not { } plate)
            return (false, false, false, false);
        var visible = plate.IsVisibleInTree();
        var hit = TryPickBattle(_camera.UnprojectPosition(marker.Node.GlobalPosition), out var picked) && picked.Id == id;
        return (plate.Billboard == BaseMaterial3D.BillboardModeEnum.Disabled &&
            (_flatMap || marker.Node.Basis.Z.Normalized().Dot(marker.Normal) > .99f), !plate.NoDepthTest, visible, hit);
    }
    internal bool VerificationArmyFlagsMatch(string tag)
    {
        SyncArmyMarkers(); var n = _campaign.NationByTag(tag);
        var matches = _armyStacks.Values.Where(m => m.NationId == n.Id).ToArray();
        return matches.Length > 0 && matches.All(m => m.FlagVisualId == NationalFlagVisualId(n));
    }
    internal bool VerificationRegionalFlagChip(Nation n)
    {
        ArmyCounterTexture(n, "line_infantry");
        var texture = DealOrForceGame.FlagTextureFor(n.Tag);
        if (texture is null || !FlagChipImages.TryGetValue(n.Tag + "#" + texture.GetInstanceId(), out var chip)) return false;
        using var expected = texture.GetImage();
        if (expected.IsCompressed()) expected.Decompress(); expected.Convert(Image.Format.Rgba8);
        return chip.GetWidth() == expected.GetWidth() && chip.GetHeight() == expected.GetHeight() && chip.GetData().SequenceEqual(expected.GetData());
    }
}
