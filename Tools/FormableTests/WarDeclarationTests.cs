using System;
using System.Linq;
using DealOrForce;
using DealOrForce.Simulation;

internal static class WarDeclarationTests
{
    private static void Check(bool ok, string text)
    { if (!ok) throw new Exception("war declaration: " + text); Console.WriteLine("PASS war declaration: " + text); }

    private static Campaign Rack(bool human = true)
    {
        var c = new Campaign(616);
        c.FeatureOverrides["diplomacy"] = true;
        for (var i = 0; i < 6; i++)
        {
            c.Nations.Add(new Nation { Id = i, MapIndex = i + 100, Tag = "WD" + i, Name = "Cabinet " + i,
                Population = 1_000_000, Gdp = 100, Treasury = 20, WarSupport = .9f, Stability = .9f, IsHuman = i == 0 && human });
            c.States.Add(new ScenarioState { Id = "wd_" + i, MapIndex = 1000 + i, NationMapIndex = 100 + i,
                NationId = "WD" + i, Population = 1_000_000 });
            c.Fleets.Add(new Fleet { Id = i + 1, NationId = i, Hulls = 6 });
            c.Armies.Add(new Army { Id = i + 1, NationId = i, Divisions = 4, Personnel = 40_000, MaxPersonnel = 40_000,
                Strength = 1, Organisation = 1, Equipment = 1, SupplyLevel = 1 });
        }
        c.SetPlayer(0); c.Nations[0].IsHuman = human;
        void Ally(int a, int b, float opinion)
        { var r = CampaignDiplomacy.Get(c, a, b); r.Stance = DiplomaticStance.Allied; r.Opinion = opinion; }
        Ally(0, 2, 100); Ally(1, 3, 100); Ally(0, 4, -100);
        c.Nations[4].WarSupport = .1f; c.Nations[4].Stability = .1f;
        return c;
    }

    internal static void Run()
    {
        var c = Rack(); var rng = c.Random.State; var relations = c.Relations.Count;
        var preview = WarDeclarationPlanning.Preview(c, 0, 1);
        Check(preview.Allowed && preview.Support.Any(p => p.NationId == 2 && !p.Defending && p.CanInvite && p.ExpectedJoin), "willing offensive ally is selectable");
        Check(preview.Support.Any(p => p.NationId == 3 && p.Defending && p.Automatic && p.ExpectedJoin), "defensive ally is forecast on the opposing side");
        Check(preview.Support.Any(p => p.NationId == 4 && !p.Defending && !p.ExpectedJoin), "reluctant ally is not presented as certain support");
        Check(c.Wars.Count == 0 && c.Relations.Count == relations && c.Random.State == rng && c.InterventionRequests.Count == 0,
            "preview changes no wars, relations, random state or requests");
        var command = new DeclareWarCampaignCommand { IssuerId = 0, TargetId = 1, InvitedNationIds = new[] { 2, 2, -999, 999, 0 } };
        Check(NetworkCommandCodec.TryEncode(command, out var type, out var json) &&
            NetworkCommandCodec.TryDecode(type, json, out var decoded) && decoded is DeclareWarCampaignCommand d && d.InvitedNationIds.Length == 5,
            "existing command codec preserves the optional invitation list");
        command.Apply(c);
        Check(c.Wars.Count == 1 && c.Wars[0].AttackerAllies.Contains(2) && c.Wars[0].DefenderAllies.Contains(3), "confirmation calls selected allies into the same war on the correct sides");
        Check(!c.Wars[0].Involves(4) && !c.Wars[0].Involves(5), "unchecked and unrelated countries are not silently called");
        command.Apply(c);
        Check(c.Wars.Count == 1 && c.Wars[0].AttackerAllies.Count(id => id == 2) == 1, "duplicate submission creates neither another war nor duplicate participation");

        c = Rack(); CampaignDiplomacy.Get(c, 2, 1).TruceUntil = 50;
        preview = WarDeclarationPlanning.Preview(c, 0, 1);
        Check(preview.Support.Any(p => p.NationId == 2 && !p.Defending && !p.CanInvite && !p.ExpectedJoin), "a truce blocks an otherwise willing ally in the forecast");
        new DeclareWarCampaignCommand { IssuerId = 0, TargetId = 1, InvitedNationIds = new[] { 2 } }.Apply(c);
        Check(!c.Wars[0].Involves(2), "confirmation revalidates a treaty-blocked invitation");

        c = Rack(); c.Nations[2].IsHuman = true;
        new DeclareWarCampaignCommand { IssuerId = 0, TargetId = 1, InvitedNationIds = new[] { 2 } }.Apply(c);
        Check(!c.Wars[0].Involves(2) && c.InterventionRequests.Any(r => r.DonorId == 2 && r.HumanConsent && r.State == WarInterventionState.Pending),
            "a human ally receives an inbox request and is never forced into war");

        c = Rack(human: false);
        var war = CampaignDiplomacy.DeclareWar(c, 0, 1);
        Check(war is not null && war.AttackerAllies.Contains(2) && war.DefenderAllies.Contains(3), "AI declaration immediately calls willing offensive allies too");
        Check(!war.Involves(4), "AI does not spam an ally forecast to refuse");

        foreach (var fps in new[] { 30, 60, 144 })
        {
            var speed = 100f; var travel = 0f;
            for (var frame = 0; frame < fps; frame++) { travel += CameraInertia.Travel(speed, 1f / fps); speed = CameraInertia.Remaining(speed, 1f / fps); }
            Check(Math.Abs(travel - CameraInertia.Travel(100, .1f) / (1 - MathF.Exp(-.8f)) * (1 - MathF.Exp(-8))) < .001f && speed < .04f,
                "camera coast has the same bounded travel at " + fps + " FPS");
        }
    }
}
