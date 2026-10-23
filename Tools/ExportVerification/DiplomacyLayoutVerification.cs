using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyDiplomacyLayout(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        world.ConfigureSinglePlayer(world.NationByTag("KAZ").Id);
        world.IsPaused = true;
        foreach (var language in new[] { "english", "russian" })
        {
            Loc.Language = language; game.Call("RefreshLocalisedText");
            foreach (var size in new[] { new Vector2I(1920,1080), new Vector2I(1280,720) })
            {
                GetTree().Root.Size = size;
                game.VerificationOpenDiplomacy("JPN");
                await Wait(.25);
                var rows = game.FindChild("DiplomacyListRows", true, false).GetChildren().OfType<Button>().Where(b => b.Visible).ToArray();
                var fits = rows.Length > 0;
                var identitiesReadable = fits;
                foreach (var row in rows)
                {
                    var name = row.FindChild("DiplomacyCountryName", true, false) as Label;
                    var identity = row.FindChild("DiplomacyCountryIdentity", true, false) as Label;
                    if (name is null || identity is null) { fits = false; break; }
                    var bottom = row.GetGlobalRect().End.Y;
                    fits &= identity.GetGlobalRect().End.Y <= bottom - 1 &&
                        name.GetGlobalRect().End.Y <= identity.GetGlobalRect().Position.Y + .5f;
                    var needed = identity.GetLineHeight() * Math.Max(1, identity.GetLineCount()) +
                        identity.GetThemeConstant("line_spacing") * Math.Max(0, identity.GetLineCount() - 1);
                    identitiesReadable &= identity.AutowrapMode != TextServer.AutowrapMode.Off &&
                        identity.MaxLinesVisible == -1 && identity.Size.Y + 1 >= needed &&
                        identity.TooltipText.Length > 0;
                }
                Check(fits, "diplomacy country names AND identity lines fit their rows " + language + " " + size.X);
                Check(identitiesReadable, "diplomacy demographics wrap without hiding faith or culture " + language + " " + size.X);
                var materials = game.FindChildren("AtlasMaterial", "", true, false);
                Check(materials.Count > 0 && materials.All(n => n is Node2D && n is not Control),
                    "panel decoration never participates in content layout " + language + " " + size.X);
                var before = CampaignFingerprint.Of(world);
                game.Call("ForceDiplomacyRefresh");
                await Wait(.1);
                Check(CampaignFingerprint.Of(world) == before, "diplomacy layout inspection changes no world state");
                await Capture("diplomacy-layout-" + language + "-" + size.X);
                game.Call("CloseDiplomacyScreen");
            }
        }
        GetTree().Root.Size = new(1920,1080);
        Loc.Language = "english"; game.Call("RefreshLocalisedText");
    }
}

public partial class DealOrForceGame
{
    internal void VerificationOpenDiplomacy(string tag)
    {
        _selectedNation = _campaign.NationByTag(tag);
        _diplomacyPageKey = "";
        OpenDiplomacyScreen();
    }
}
