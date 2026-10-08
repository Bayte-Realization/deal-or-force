using System;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class MovementMethodsTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS movement methods: " + label); }

    public static void Run()
    {
        var c = new Campaign(919);
        var civicDefinition = MovementCatalog.All.First(d => d.Has("civic_independence"));
        var armedDefinition = MovementCatalog.All.First(d => d.Has("armed_wing"));
        var civic = c.AddNation(new Nation { Tag = civicDefinition.Tag, Movement = new MovementState { Id = civicDefinition.Id } });
        var armed = c.AddNation(new Nation { Tag = armedDefinition.Tag, Movement = new MovementState { Id = armedDefinition.Id } });
        var before = CampaignFingerprint.Of(c);
        Check(MovementMethods.StageOf(c, civic) == MovementStage.Civic && MovementMethods.Peaceful(c, civic),
            "legacy civic movements retain peaceful initial methods");
        Check(MovementMethods.StageOf(c, armed) == MovementStage.Armed &&
            MovementMethods.WillRise(c, armed, armedDefinition), "legacy armed movements retain their initial capability");
        Check(civic.Movement.Militancy == -1 && armed.Movement.Militancy == -1 && before == CampaignFingerprint.Of(c),
            "reading methods and uprising eligibility initializes no saved state");
        civic.Movement.Militancy = .61f;
        Check(!MovementMethods.Peaceful(c, civic) && MovementMethods.WillRise(c, civic, civicDefinition),
            "a formerly civic movement can acquire armed methods through sustained radicalization");
        armed.Movement.Militancy = .1f;
        Check(MovementMethods.Peaceful(c, armed) && !MovementMethods.WillRise(c, armed, armedDefinition),
            "a formerly armed movement can choose peaceful methods");
        c.Defines.MovementArmedAt = .25f; armed.Movement.Militancy = .3f;
        Check(MovementActions.For(armed, c).Any(k => k.Id == "rise"),
            "action discovery uses the campaign's authored thresholds, not compiled defaults");
        before = CampaignFingerprint.Of(c); armed.Movement.Militancy += .01f;
        Check(before != CampaignFingerprint.Of(c), "methods participate in the network fingerprint");
        before = CampaignFingerprint.Of(c); armed.LivingOffset += .1f;
        Check(before != CampaignFingerprint.Of(c), "economic living anchor participates in the network fingerprint");
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, 5L * 24);
        c.SetFeature("movement_lifecycle", false);
        var prior = civic.Movement.Militancy;
        MovementMethods.Tick(c);
        Check(civic.Movement.Militancy == prior, "disabled lifecycle prevents automatic method changes");
        c.SetFeature("movement_lifecycle", true); c.SetFeature("movements", false);
        MovementMethods.Tick(c);
        Check(civic.Movement.Militancy == prior, "disabled movements also prevent method changes");
        c.SetFeature("movements", true);
        MovementMethods.Tick(c);
        Check(civic.Movement.Militancy < prior && Math.Abs(civic.Movement.Militancy - prior) < .01f,
            "scheduled drift is gradual rather than an instant identity switch");
        var options = new JsonSerializerOptions { IncludeFields = true };
        var saved = JsonSerializer.Deserialize<MovementState>(JsonSerializer.Serialize(civic.Movement, options), options);
        Check(saved.Militancy == civic.Movement.Militancy, "method progress survives serialization");
    }
}
