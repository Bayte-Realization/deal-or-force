using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    // An isolated native-resource fixture; never changes or writes a campaign.
    private void VerifyCounterTextureLifetime(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        var before = CampaignFingerprint.Of(world);
        GlobePresentation.VerifyCounterTextureCacheLifetime(world.NationByTag("POL"), Check);
        Check(CampaignFingerprint.Of(world) == before, "counter texture lifetime fixture changes no campaign state");
    }
}

public sealed partial class GlobePresentation
{
    internal static void VerifyCounterTextureCacheLifetime(Nation sourceNation, Action<bool, string> check)
    {
        var fixture = new GlobePresentation();
        try
        {
            const int variants = 100;
            const int retained = 3;
            const string template = "line_infantry";
            var symbolBounds = new Rect2I(8, 5, 51, 70);
            fixture._fleetTexture = CreateFleetMarkerTexture();
            var compactFleet = fixture.CompactCounterTexture(fixture._fleetTexture, new Rect2I(34, 61, 96, 26));
            for (var index = 0; index < variants; index++)
            {
                // Distinct cache identities with the same authored artwork keep this
                // fixture bounded and exercise the real full/compact generation paths.
                var nation = new Nation { Id = index, MapIndex = 30000 + index,
                    Tag = sourceNation.Tag, Color = sourceNation.Color };
                var full = fixture.ArmyCounterTexture(nation, template);
                var compact = fixture.CompactCounterTexture(full, symbolBounds);
                var marker = new ArmyStackMarker { Node = new Node3D(), FullPlateTexture = full,
                    Plate = new Sprite3D { Texture = compact } };
                marker.Node.AddChild(marker.Plate);
                fixture.AddChild(marker.Node);
                fixture._armyStacks[(index, 0, 0, 0)] = marker;
            }

            check(fixture._armyCounterTextures.Count == variants && fixture._compactCounterTextures.Count == variants + 1,
                "counter fixture generates 100 full and compact native textures");
            var live = fixture._armyStacks[(variants - 1, 0, 0, 0)];
            var retired = fixture._armyStacks[(0, 0, 0, 0)];
            byte[] fullPixels;
            byte[] compactPixels;
            using (var image = live.FullPlateTexture.GetImage()) fullPixels = image.GetData();
            using (var image = live.Plate.Texture.GetImage()) compactPixels = image.GetData();

            for (var index = 0; index < variants - retained; index++)
                fixture._armyStacks.Remove((index, 0, 0, 0));
            fixture.PruneArmyCounterTextureCaches();
            check(fixture._armyCounterTextures.Count == retained && fixture._compactCounterTextures.Count == retained + 1,
                "full and compact counter caches retain only three current marker cards");
            check(fixture.CompactCounterTexture(fixture._fleetTexture, new Rect2I(34, 61, 96, 26)).GetInstanceId() == compactFleet.GetInstanceId(),
                "army cache pruning preserves a shared fleet compact texture");
            check(fixture.CompactCounterTexture(live.FullPlateTexture, symbolBounds).GetInstanceId() == live.Plate.Texture.GetInstanceId(),
                "retained compact counter reuses its native texture after pruning");
            using (var image = live.FullPlateTexture.GetImage())
                check(image.GetWidth() == 176 && image.GetHeight() == 104 && fullPixels.SequenceEqual(image.GetData()),
                    "full counter pixels survive disposal of temporary images and cache pruning");
            using (var image = live.Plate.Texture.GetImage())
                check(image.GetWidth() == 72 && image.GetHeight() == 46 && compactPixels.SequenceEqual(image.GetData()),
                    "compact counter pixels survive disposal of temporary images and cache pruning");
            using (var image = retired.Plate.Texture.GetImage())
                check(!image.IsEmpty(), "retired sprite keeps its texture while waiting for node deletion");

            fixture._armyStacks.Clear();
            fixture.PruneArmyCounterTextureCaches();
            check(fixture._armyCounterTextures.Count == 0 && fixture._compactCounterTextures.Count == 1,
                "counter caches release all army identities while preserving the fleet card");
            using (var image = live.Plate.Texture.GetImage())
                check(compactPixels.SequenceEqual(image.GetData()), "dropping cache references does not dispose a sprite texture");

            Image first = null;
            for (var index = 0; index < FlagChipImageLimit + 10; index++)
            {
                var image = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
                image.Fill(Colors.White);
                if (index == 0) first = image;
                RememberFlagChipImage("verify_flag_cache_" + index, image);
            }
            check(FlagChipImages.Count == FlagChipImageLimit && !GodotObject.IsInstanceValid(first),
                "decoded CPU flag copies have a 64-image bound and dispose evicted ownership");
            check(FlagChipImages.Values.All(image => image.GetPixel(0,0) == Colors.White),
                "retained CPU flag source pixels remain intact");
            ClearFlagChipImages();
            check(FlagChipImages.Count == 0 && FlagChipOrder.Count == 0,
                "decoded flag cache can release all owned CPU images");
        }
        finally
        {
            fixture.Free();
        }
    }
}
