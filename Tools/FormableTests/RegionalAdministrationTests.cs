using System;
using System.Linq;
using DealOrForce.Simulation;

static class RegionalAdministrationTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("regional administration: " + note); Console.WriteLine("PASS regional administration: " + note); }

    public static void Run()
    {
        var c = new Campaign(20261019) { ScenarioId = "regional_administration_fixture" };
        var owner = new Nation { Id = 0, MapIndex = 1, Tag = "DEU", Name = "Germany", Population = 56_000_000,
            Gdp = 100, Treasury = 100, PoliticalPoints = 100, CapitalStateMapIndex = 1 };
        var subject = new Nation { Id = 1, MapIndex = 2, Tag = "V01", Name = "Kurdistan", Population = 2_000_000,
            Gdp = 20, Treasury = 20, OverlordId = 0, SubjectType = "satellite", DeclaredCulture = "kurdish", CapitalStateMapIndex = 4 };
        c.Nations.AddRange(new[] { owner, subject });
        const int width = 12; var states = new ushort[width * 3]; var owners = new ushort[states.Length];
        for (var id = 1; id <= 8; id++)
        {
            states[width + id] = (ushort)id; owners[width + id] = (ushort)(id == 4 ? 2 : 1);
            var s = new ScenarioState { Id = "test_" + id, MapIndex = id, Name = "Region " + id,
                NationMapIndex = id == 4 ? 2 : 1, Population = id == 1 ? 50_000_000 : id == 4 ? 2_000_000 : 1_000_000 };
            s.PopulationGroups.Add(new PopulationGroup { Culture = id is 1 or 6 or 8 ? "german" : "kurdish", Size = s.Population });
            c.States.Add(s);
        }
        c.AttachProvinceTopology(ProvinceTopology.Build(states, owners, width, 3, _ => false));
        owner.CapitalStateMapIndex = 1; subject.CapitalStateMapIndex = 4;
        var fingerprint = CampaignFingerprint.Of(c);
        var pack = RegionalAdministration.Package(c, owner, c.States[1]);
        Check(pack.SequenceEqual(new[] { 2, 3 }), "AI groups connected related regions and does not jump a subject or unrelated core");
        Check(RegionalAdministration.MatchingSubject(c, owner, c.States[1]) == subject,
            "a subject touching the complete package is reused even when it does not touch its seed");
        subject.DeclaredCulture = "han";
        Check(RegionalAdministration.MatchingSubject(c, owner, c.States[1]) is null,
            "foreign unrest alone does not assign land to an unrelated neighbouring puppet");
        subject.DeclaredCulture = "kurdish";
        Check(RegionalAdministration.MayGrant(c, owner, subject, pack, out var price, out _) && price == 6,
            "connected package has the same deterministic price for UI and command");
        Check(CampaignFingerprint.Of(c) == fingerprint, "package and transfer previews are read-only");
        Check(!RegionalAdministration.MayGrant(c, owner, subject, new[] { 2 }, out _, out _), "unconnected gift is refused");
        Check(!RegionalAdministration.MayGrant(c, owner, subject, new[] { 1, 2, 3 }, out _, out _), "capital cannot be gifted");
        Check(!RegionalAdministration.MayGrant(c, owner, subject, new[] { 2, 3, 999 }, out _, out _), "stale package is rejected atomically");
        owner.PoliticalPoints = 0;
        new GrantSubjectRegionsCommand { IssuerId = owner.Id, SubjectId = subject.Id, StateMapIndices = pack }.Apply(c);
        Check(c.States[1].NationMapIndex == owner.MapIndex, "insufficient political points transfer no land");
        owner.PoliticalPoints = 100;
        var gdp = owner.Gdp + subject.Gdp; var cash = owner.Treasury + subject.Treasury;
        var population = c.States.Sum(s => s.Population);
        new GrantSubjectRegionsCommand { IssuerId = owner.Id, SubjectId = subject.Id, StateMapIndices = new[] { 3, 2, 2 } }.Apply(c);
        Check(c.StatesOf(subject).Count(s => s.Population > 0) == 3, "one command grows the existing subject by two regions");
        Check(owner.PoliticalPoints == 94 && c.Nations.Count == 2, "deduplicated price is paid once, no new microstate is created");
        Check(Math.Abs(owner.Gdp + subject.Gdp - gdp) < .001f && owner.Treasury + subject.Treasury == cash,
            "GDP is conserved and bank balances are not overwritten");
        Check(owner.Population + subject.Population == population, "residents are recounted without duplication");
        Check(!RegionalAdministration.MayGrant(c, owner, subject, new[] { 5 }, out _, out _), "administration cooldown prevents instant repeat grants");
        Check(c.Chronicle.Entries.Count(e => e.Kind == "subject_land_grant") == 1, "only a successful grant emits a chronicle fact");
        c.SetFeature("subjects", false); subject.LastSubjectInteractionDay = -9999;
        var beforeDisabled = CampaignFingerprint.Of(c);
        new GrantSubjectRegionsCommand { IssuerId = owner.Id, SubjectId = subject.Id, StateMapIndices = new[] { 5 } }.Apply(c);
        Check(CampaignFingerprint.Of(c) == beforeDisabled, "feature switch blocks the command, not just the button");
        c.SetFeature("subjects", true); owner.ImperialOverstretch = .8f; owner.Stability = .3f;
        c.States[4].Radicalism = .9f; c.States[4].SecessionPressure = .8f;
        var aiGranted = false;
        for (var i=0; i<36 && !aiGranted; i++)
        {
            c.RestoreRuntimeState(new GameDate(2025,1,1), 83+i*30, true, c.Random.State, owner.Id,
                Array.Empty<int>(), 0, 0, c.MapRevision, true, null,null,null,null, noHumanAuthority:true);
            CountryRelease.TickAi(c);
            aiGranted = c.States[4].NationMapIndex == subject.MapIndex;
        }
        Check(aiGranted && c.Nations.Count == 2, "real crisis AI tick enlarges the suitable subject rather than spawning a competing microstate");
        var wire = new GrantSubjectRegionsCommand { IssuerId=owner.Id, SubjectId=subject.Id, StateMapIndices=new[]{6} };
        Check(NetworkCommandCodec.TryEncode(wire,out var type,out var json) && NetworkCommandCodec.TryDecode(type,json,out var decoded) &&
            decoded is GrantSubjectRegionsCommand restored && restored.StateMapIndices.SequenceEqual(wire.StateMapIndices),
            "administrative package crosses the existing command codec intact");
    }
}
