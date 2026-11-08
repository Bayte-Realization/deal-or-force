using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyFlagCloth(DealOrForceGame game, string language, int width)
    {
        var badge = (game.FindChild("HudCountryRank", true, false) as Control)?.GetParent()?.GetChildOrNull<Control>(0);
        var flag = badge?.GetChildOrNull<TextureRect>(0);
        Check(flag?.Material is ShaderMaterial, "real HUD flag uses the shared cloth shader: " + language + width);
        if (flag?.Material is not ShaderMaterial material) return;
        Check(game.FindChildren("*", "TextureRect", true, false).OfType<TextureRect>()
            .Where(rect => rect.Material == material).Count() > 1, "flag badges share one material instead of per-flag processors");
        Check(SettingsCatalog.Of("animated_flags") is { Category: "video", Campaign: false } &&
            SettingsCatalog.Of("flag_fabric") is { Category: "video", Campaign: false }, "both flag appearance options are player video settings");
        var c = game.VerificationWorld; var fingerprint = CampaignFingerprint.Of(c); var random = c.Random.State;
        var oldAnimation = SettingsCatalog.Flag("animated_flags"); var oldFabric = SettingsCatalog.Flag("flag_fabric");
        try
        {
            SettingsCatalog.Store("animated_flags", true); SettingsCatalog.Store("flag_fabric", true);
            await Wait(.4);
            using var movingBefore = GetViewport().GetTexture().GetImage();
            await Wait(.8);
            using var movingAfter = GetViewport().GetTexture().GetImage();
            var rectangle = flag.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, flag.Size);
            var scale = new Vector2(movingBefore.GetWidth(), movingBefore.GetHeight()) / GetViewport().GetVisibleRect().Size;
            var pixels = new Rect2I((Vector2I)(rectangle.Position * scale), (Vector2I)(rectangle.Size * scale));
            int Changed(Image a, Image b)
            {
                var changed = 0;
                for (var y = Math.Max(0, pixels.Position.Y + 2); y < Math.Min(a.GetHeight(), pixels.End.Y - 2); y++)
                for (var x = Math.Max(0, pixels.Position.X + 2); x < Math.Min(a.GetWidth(), pixels.End.X - 2); x++)
                {
                    var delta = a.GetPixel(x, y) - b.GetPixel(x, y);
                    if (MathF.Abs(delta.R) + MathF.Abs(delta.G) + MathF.Abs(delta.B) > .015f) changed++;
                }
                return changed;
            }
            Check(Changed(movingBefore, movingAfter) > 10, "flag really animates in rendered pixels while the campaign is paused: " + language + width);
            await Capture("flag-cloth-animated-" + language + "-" + width);
            SettingsCatalog.Store("animated_flags", false); await Wait(.4);
            using var stillBefore = GetViewport().GetTexture().GetImage();
            await Wait(.6);
            using var stillAfter = GetViewport().GetTexture().GetImage();
            Check(!material.GetShaderParameter("animate_flags").AsBool() &&
                material.GetShaderParameter("fabric_enabled").AsBool() && Changed(stillBefore, stillAfter) == 0,
                "disabling animation freezes flag pixels but keeps fabric lighting: " + language + width);
            await Capture("flag-cloth-still-" + language + "-" + width);
            SettingsCatalog.Store("flag_fabric", false); await Wait(.4);
            using var plain = GetViewport().GetTexture().GetImage();
            Check(!material.GetShaderParameter("fabric_enabled").AsBool() && Changed(stillAfter, plain) > 10,
                "fabric can be removed independently, visibly restoring the plain design: " + language + width);
            await Capture("flag-cloth-plain-" + language + "-" + width);
            Check(CampaignFingerprint.Of(c) == fingerprint && c.Random.State == random,
                "all flag settings and rendering leave campaign state and randomness unchanged");
        }
        finally
        {
            SettingsCatalog.Store("animated_flags", oldAnimation); SettingsCatalog.Store("flag_fabric", oldFabric);
            FlagClothPresentation.Synchronize();
        }
    }
}

