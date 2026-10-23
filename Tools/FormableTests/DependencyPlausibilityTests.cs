using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Dependencies that do not go to war (2026-10-21). The five-year world pulse had the United States
/// fighting Guantanamo Bay, Russia fighting Baikonur and Australia fighting Norfolk Island, all
/// raised by a rival's agitation money.
/// </summary>
static class DependencyPlausibilityTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("dependencies: " + label);
        Console.WriteLine("PASS dependencies: " + label);
    }

    public static void Run()
    {
        SubjectCatalog.ResetCatalog();
        var c = new Campaign(20261021) { ScenarioId = "dependency_plausibility_fixture" };
        var sovereign = new Nation { Id = 1, MapIndex = 1, Tag = "SOV", Name = "Sovereign", Population = 300_000_000, Gdp = 2000f, Treasury = 50f, Stability = .8f };
        var rival = new Nation { Id = 2, MapIndex = 2, Tag = "RIV", Name = "Rival", Population = 100_000_000, Gdp = 800f, Treasury = 50f, Stability = .8f };
        var lease = new Nation { Id = 3, MapIndex = 3, Tag = "LSE", Name = "Leased base", Population = 6_000, Gdp = .1f, Treasury = .1f };
        var islet = new Nation { Id = 4, MapIndex = 4, Tag = "ISL", Name = "Islet", Population = 2_000, Gdp = .05f, Treasury = .1f };
        var colony = new Nation { Id = 5, MapIndex = 5, Tag = "COL", Name = "Overseas territory", Population = 280_000, Gdp = 10f, Treasury = 1f };
        var puppet = new Nation { Id = 6, MapIndex = 6, Tag = "PUP", Name = "Puppet", Population = 5_000_000, Gdp = 20f, Treasury = 2f };
        c.Nations.AddRange(new[] { sovereign, rival, lease, islet, colony, puppet });
        Check(SubjectSystem.Subjugate(c, sovereign, lease, "leased_territory", out _) &&
              SubjectSystem.Subjugate(c, sovereign, islet, "dependent_territory", out _) &&
              SubjectSystem.Subjugate(c, sovereign, colony, "dependent_territory", out _) &&
              SubjectSystem.Subjugate(c, sovereign, puppet, "puppet", out _), "fixture dependencies are bound");

        Check(SubjectCatalog.Of("leased_territory") is { Agitable: false, ArmedRebellion: false } &&
              SubjectCatalog.Of("dependent_territory") is { Agitable: true, ArmedRebellion: false } &&
              SubjectCatalog.Of("associated_state") is { ArmedRebellion: false } &&
              SubjectCatalog.Of("puppet") is { Agitable: true, ArmedRebellion: true },
            "the data says which kinds can be agitated and which leave by war");
        Check(!SubjectInteractions.CanInteract(c, rival, lease, SubjectInteractionKind.AgitateIndependence, out var leaseWhy) && leaseWhy.Length > 0,
            "nobody can raise an independence movement on rented land");
        Check(!SubjectInteractions.CanInteract(c, rival, islet, SubjectInteractionKind.AgitateIndependence, out _),
            "an islet of two thousand people is too small for one");
        Check(SubjectInteractions.CanInteract(c, rival, colony, SubjectInteractionKind.AgitateIndependence, out _) &&
              SubjectInteractions.CanInteract(c, rival, puppet, SubjectInteractionKind.AgitateIndependence, out _),
            "a populous territory and a puppet can still be stirred up");

        colony.LibertyDesire = 1f;
        var wars = c.Wars.Count;
        Check(SubjectInteractions.TriggerSubjectRebellion(c, sovereign, colony) is null && !colony.IsSubject && c.Wars.Count == wars &&
              c.Chronicle.Entries.Any(e => e.Kind == "subject_broke_free" && e.ActorId == colony.Id),
            "an overseas territory that wants out leaves peacefully: no war, a recorded departure");
        puppet.LibertyDesire = 1f;
        Check(SubjectInteractions.TriggerSubjectRebellion(c, sovereign, puppet) is not null && c.Wars.Count == wars + 1 &&
              lease.IsSubject && islet.IsSubject,
            "a puppet still rises in arms, and does not drag the lease or the islet into its war");
    }
}
