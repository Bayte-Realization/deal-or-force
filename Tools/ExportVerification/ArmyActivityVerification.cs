using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    // A presentation fixture in this verification process only. Never loads or writes a save.
    private async Task VerifyArmyActivity(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        world.ConfigureSinglePlayer(world.NationByTag("CHN").Id);
        var units = world.Armies.Where(a => a.NationId == world.Player.Id).Take(4).ToArray();
        Check(units.Length == 4, "army activity fixture has four actual formations");
        if (units.Length != 4) return;
        foreach (var unit in units)
        {
            unit.BattleId = -1; unit.EmbarkedFleetId = -1; unit.SupportOnly = false;
            unit.Route.Clear(); unit.OrderKind = ArmyOrder.AdvanceFront;
            unit.Organisation = .8f; unit.SupplyLevel = .8f; unit.PlanActive = true;
        }
        units[0].Route.Add(world.ProvinceMap.Neighbors[units[0].ProvinceId].First());
        units[1].SupportOnly = true;
        units[2].EmbarkedFleetId = 999;
        units[3].PlanActive = false; units[3].Planning = .42f;
        foreach (var language in new[] { "english", "russian" })
        {
            Loc.Language = language; game.Call("RefreshLocalisedText");
            foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
            {
                GetTree().Root.Size = size;
                var before = CampaignFingerprint.Of(world);
                game.VerificationShowArmyActivity(units);
                await Wait(.2);
                var label = game.FindChild("AssaultReadinessText", true, false) as Label;
                Check(label is not null && label.IsVisibleInTree() &&
                    label.Text.Contains(Loc.Format("Marching: {0:N0}", 1)) &&
                    label.Text.Contains(Loc.Format("Support fire only: {0:N0}", 1)) &&
                    label.Text.Contains(Loc.Format("Embarked: {0:N0}", 1)),
                    "actual army card distinguishes activity " + language + " " + size.X);
                Check(CampaignFingerprint.Of(world) == before,
                    "reading the army activity card changes no simulation state");
                await Capture("army-activity-" + language + "-" + size.X);
            }
        }
        GetTree().Root.Size = new(1920, 1080);
    }
}

public partial class DealOrForceGame
{
    internal void VerificationShowArmyActivity(Army[] units)
    {
        _selectedArmies.Clear();
        _selectedArmies.AddRange(units);
        SetArmyPanelVisible(true);
        _armyCommandTabs.CurrentTab = 0;
        RefreshArmyPanel();
    }
}
