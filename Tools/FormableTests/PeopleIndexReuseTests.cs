using System;
using DealOrForce.Simulation;

/// <summary>
/// The movement people index lives on its campaign. The same owners and the same
/// population do not build it again. An ownership change does. A second campaign
/// keeps its own index.
/// </summary>
static class PeopleIndexReuseTests
{
    static int _passed;

    static void Check(bool ok, string note)
    {
        if (!ok) throw new Exception("people index reuse: " + note);
        _passed++;
        Console.WriteLine("PASS people index reuse: " + note);
    }

    public static void Run()
    {
        var first = World(12, "kurd", 4_000);
        var second = World(13, "arab", 9_000);
        Check(first.NationById(0) == first.Nations[0] && first.NationById(1) == first.Nations[1] &&
              second.NationById(0) == second.Nations[0],
            "nation id is the list index");

        var definition = Definition();
        var firstPrint = CampaignFingerprint.Of(first);
        var secondPrint = CampaignFingerprint.Of(second);
        Drain(first);
        var firstPeople = MovementRules.PeopleOf(first, first.States[0], definition, out _);
        Drain(second);
        var secondPeople = MovementRules.PeopleOf(second, second.States[0], definition, out _);
        var firstList = first.MovementPeople.CultureBucketsById[0];
        var secondList = second.MovementPeople.CultureBucketsById[0];
        Check(MovementRules.PeopleIndexBuilds(first) == 1 && MovementRules.PeopleIndexBuilds(second) == 1,
            "each campaign builds its own index once");
        Check(firstPeople == 4_000 && secondPeople == 9_000 && !ReferenceEquals(firstList, secondList),
            "the two indexes count different people and are different lists");
        Check(CampaignFingerprint.Of(first) == firstPrint && CampaignFingerprint.Of(second) == secondPrint,
            "building the index does not change either fingerprint");

        Advance(first);
        Drain(first);
        Check(MovementRules.PeopleIndexBuilds(first) == 1 &&
              MovementRules.PeopleOf(first, first.States[0], definition, out _) == firstPeople &&
              ReferenceEquals(firstList, first.MovementPeople.CultureBucketsById[0]),
            "the same owners and the same population do not rebuild");

        var owned = CampaignFingerprint.Of(first);
        first.InvalidateStateIndex();
        Drain(first);
        Check(MovementRules.PeopleIndexBuilds(first) == 1 &&
              first.MovementPeople.OwnershipRevision == first.OwnershipRevision &&
              MovementRules.PeopleOf(first, first.States[0], definition, out _) == firstPeople &&
              CampaignFingerprint.Of(first) == owned,
            "the same regions and the same population do not rebuild when the ownership revision moves");
        var replaced = new ScenarioState
        {
            Id = "home", Name = "Home", MapIndex = 100, NationId = "AAA", NationMapIndex = 10, Population = 4_000
        };
        replaced.PopulationGroups.Add(new PopulationGroup
        {
            Culture = "kurd", Religion = "catholic", Size = 4_000, Profession = "labourers", Class = SocialClass.Lower
        });
        first.States[0] = replaced;
        first.InvalidateStateIndex();
        Drain(first);
        Check(MovementRules.PeopleIndexBuilds(first) == 2 &&
              MovementRules.PeopleOf(first, replaced, definition, out _) == firstPeople,
            "an ownership change that replaces a region rebuilds the index");
        Check(MovementRules.PeopleIndexBuilds(second) == 1 &&
              ReferenceEquals(secondList, second.MovementPeople.CultureBucketsById[0]) &&
              MovementRules.PeopleOf(second, second.States[0], definition, out _) == secondPeople,
            "the first campaign's rebuild does not touch the second campaign's index");

        first.States[0].PopulationGroups[0].Size += 80;
        first.States[0].Population += 80;
        Advance(first);
        Drain(first);
        Check(MovementRules.PeopleIndexBuilds(first) == 3 &&
              MovementRules.PeopleOf(first, first.States[0], definition, out _) == firstPeople + 80,
            "a population change rebuilds the index");
        Check(MovementRules.PeopleIndexBuilds(second) == 1 &&
              MovementRules.PeopleOf(second, second.States[0], definition, out _) == secondPeople &&
              ReferenceEquals(secondList, second.MovementPeople.CultureBucketsById[0]),
            "a population change in one campaign leaves the other index in place");

        RegionPreviewDuringIndexBuild();
        CountsReuseStorageAcrossDefinitions();
        Console.WriteLine($"PEOPLE_INDEX_REUSE {_passed} PASS");
    }

    static void CountsReuseStorageAcrossDefinitions()
    {
        var campaign = World(15, "kurd", 4_000);
        var region = campaign.States[0];
        var former = Definition();
        var oldCounts = MovementRules.PeopleIn(campaign, former);
        Check(oldCounts[region].Own == 4_000, "the first definition stores its regional count");

        region.Population += 10;
        region.PopulationGroups[0].Size += 10;
        Advance(campaign);
        var next = new MovementDefinition { Id = "new_faith_movement" };
        next.Faiths.Add("catholic");
        var counts = MovementRules.PeopleIn(campaign, next);
        Check(ReferenceEquals(oldCounts, counts), "a new definition can reuse the old census storage");
        Check(counts[region] == (0L, 4_010L), "reused storage carries only the new movement's following");
    }

    static void RegionPreviewDuringIndexBuild()
    {
        var campaign = World(14, "kurd", 4_000);
        var region = campaign.States[0];
        var movement = campaign.Nations[1];
        movement.Movement = new MovementState { Exile = true, Landless = true, HeadquartersStateMapIndex = region.MapIndex };
        movement.Movement.Peoples.Add("kurd");
        movement.Movement.Support[region.MapIndex] = .5f;
        for (var index = 1; index <= 128; index++)
        {
            var extra = new ScenarioState { MapIndex = region.MapIndex + index, Id = $"region_{index}", Population = 250 };
            extra.PopulationGroups.Add(new PopulationGroup { Culture = "kurd", Religion = "catholic", Size = 250 });
            campaign.States.Add(extra);
        }

        var priorBudget = Campaign.StageSliceMilliseconds;
        Campaign.StageSliceMilliseconds = 0;
        try
        {
            using var stages = MovementRules.EnsurePeopleIndexedStages(campaign, false).GetEnumerator();
            Check(stages.MoveNext() && !campaign.MovementPeople.Indexed,
                "the index build yields before it has published all regions");
            Check(MovementReach.ResidentSupport(campaign, movement, region) == .5f &&
                  !campaign.MovementPeople.Indexed && MovementRules.PeopleIndexBuilds(campaign) == 1,
                "a regional preview during the read window leaves the suspended build alone");
            while (stages.MoveNext()) { }
            var counted = MovementRules.PeopleIn(campaign, Movements.DefinitionOf(movement));
            Check(campaign.MovementPeople.Indexed && counted.Count == 129 &&
                  counted[region].Own == 4_000 && counted[campaign.States[^1]].Own == 250,
                "the resumed index publishes every region once");
        }
        finally { Campaign.StageSliceMilliseconds = priorBudget; }
    }

    static Campaign World(int seed, string culture, long size)
    {
        var campaign = new Campaign(seed);
        campaign.Nations.Add(new Nation { Id = 0, Tag = "AAA", Name = "One", MapIndex = 10, Population = size });
        campaign.Nations.Add(new Nation { Id = 1, Tag = "BBB", Name = "Two", MapIndex = 11, Population = 1 });
        var state = new ScenarioState
        {
            Id = "home", Name = "Home", MapIndex = 100, NationId = "AAA", NationMapIndex = 10, Population = size
        };
        state.PopulationGroups.Add(new PopulationGroup
        {
            Culture = culture, Religion = "catholic", Size = size, Profession = "labourers", Class = SocialClass.Lower
        });
        campaign.States.Add(state);
        return campaign;
    }

    static MovementDefinition Definition()
    {
        var definition = new MovementDefinition { Id = "people_index_reuse" };
        definition.Peoples.Add("kurd");
        definition.Peoples.Add("arab");
        definition.Faiths.Add("catholic");
        return definition;
    }

    static void Drain(Campaign campaign)
    {
        foreach (var _ in MovementRules.EnsurePeopleIndexedStages(campaign, false)) { }
    }

    static void Advance(Campaign campaign)
    {
        var hours = campaign.ElapsedHours + 24;
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(campaign, hours);
    }
}
