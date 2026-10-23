using System.Linq;
using DealOrForce.Simulation;

static class HostPatienceTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new System.Exception("host patience: " + label);
        System.Console.WriteLine("PASS host patience: " + label);
    }

    public static void Run()
    {
        var c = new Campaign(20261015);
        var host = new Nation { Id = 0, MapIndex = 1, Tag = "FRA", Name = "France", Population = 1_000_000, IsHuman = true };
        var movement = new Nation
        {
            Id = 1, MapIndex = 2, Tag = "LGE", Name = "The League", Population = 10_000,
            Movement = new MovementState
            {
                Id = "league", Landless = true, HeadquartersStateMapIndex = 5,
                Heat = 0.2f, Militancy = 0.2f
            }
        };
        c.Nations.Add(host);
        c.Nations.Add(movement);
        c.States.Add(new ScenarioState { MapIndex = 5, NationMapIndex = 1, Id = "lyon", Name = "Lyon", Population = 100_000 });

        Check(MovementRules.Patience(c, movement).Length == 0, "a quiet headquarters is not a warning");
        movement.Movement.Heat = 0.5f;
        Check(MovementRules.Patience(c, movement) ==
              "The host is losing patience. A headquarters that works in the open spends it.",
            "an open headquarters that has spent patience says so before it is thrown out");
        movement.Movement.Heat = 0.7f;
        movement.Movement.Militancy = 0.6f;
        Check(MovementRules.Patience(c, movement) ==
              "The welcome is almost gone: what it will do on this ground has used it up.",
            "an armed headquarters close to the line is named for what it will do");
        var heat = movement.Movement.Heat;
        var finger = CampaignFingerprint.Of(c);
        var worn = MovementRules.WornWelcome(c, host);
        var item = SituationReport.Read(c, host).FirstOrDefault(entry => entry.Id == "host_temper");
        Check(worn == movement && item is not null && item.TargetId == movement.Id && item.Route == SituationRoute.Crises,
            "the host's situation room names the headquarters on its ground");
        Check(movement.Movement.Heat == heat && CampaignFingerprint.Of(c) == finger,
            "hearing the warning does not spend the patience or throw the headquarters out");
        Check(MovementRules.PatienceChip(c, movement) == "welcome almost gone",
            "the province card can say the welcome is almost gone without a paragraph");
        new MovementDealingCommand { IssuerId = host.Id, MovementId = movement.Id, Action = "expel" }.Apply(c);
        Check(movement.Movement.HeadquartersStateMapIndex < 0 && movement.Movement.Heat < heat,
            "expelling the headquarters is the decision the warning was about");
        Check(MovementRules.Patience(c, movement).Length == 0, "once it is out, the warning stops");
    }
}
