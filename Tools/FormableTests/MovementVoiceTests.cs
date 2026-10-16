using System;
using System.Linq;
using DealOrForce.Simulation;

static class MovementVoiceTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("movement voice: " + label);
        Console.WriteLine("PASS movement voice: " + label);
    }

    public static void Run()
    {
        var c = new Campaign(20261015);
        var host = new Nation { Id = 0, MapIndex = 1, Tag = "FRA", Name = "France", Population = 1_000_000, IsHuman = true };
        var movement = new Nation
        {
            Id = 1, MapIndex = 2, Tag = "LGE", Name = "The League", Population = 10_000,
            Movement = new MovementState { Id = "league", Landless = true, Legitimacy = 0.05f, Militancy = 0.35f }
        };
        movement.Movement.Support[5] = 0.8f;
        var region = new ScenarioState
        {
            MapIndex = 5, NationMapIndex = 1, Id = "lyon", Name = "Lyon",
            Population = 200_000, Radicalism = 0.9f
        };
        c.Nations.Add(host);
        c.Nations.Add(movement);
        c.States.Add(region);
        var finger = CampaignFingerprint.Of(c);
        var militancy = movement.Movement.Militancy;
        Check(MovementMethods.LoudestPressure(c, movement) == "The people who follow it are already angry.",
            "angry followers are named ahead of a quiet standing");
        Check(movement.Movement.Militancy == militancy && CampaignFingerprint.Of(c) == finger,
            "naming the reason does not move the movement");

        movement.Movement.Legitimacy = 0.95f;
        region.Radicalism = 0.3f;
        Check(MovementMethods.LoudestPressure(c, movement) ==
              "The world is taking it seriously, and that is pulling it back from the gun.",
            "standing in the world is allowed to be the reason it softens");

        for (var i = 0; i < 4; i++)
            c.Chronicle.Record(c, "movement_crackdown", host.Id, movement.Id);
        movement.Movement.Legitimacy = 0.05f;
        Check(MovementMethods.LoudestPressure(c, movement) == "Crackdowns this year are pushing it toward the gun.",
            "a year of crackdowns outweighs a calm following");

        var seen = CampaignFingerprint.Of(c);
        var item = SituationReport.Read(c, host).FirstOrDefault(entry => entry.Id == "movement_methods");
        Check(item is not null && item.Route == SituationRoute.Crises && item.Facts.Length == 2
            && CampaignFingerprint.Of(c) == seen,
            "the host's situation room names the movement on its ground without changing the world");
    }
}
