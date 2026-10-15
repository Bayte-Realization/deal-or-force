using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DealOrForce.Simulation;

/// <summary>New living-world state must be visible to peer divergence checks before it
/// changes cash, membership, borders or military strength.</summary>
public static class ClaudeStateFingerprintTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("living-world fingerprint: " + label);
        Console.WriteLine("PASS living-world fingerprint: " + label);
    }

    public static void Run()
    {
        // Captured before adding the new fingerprint blocks, not from the repaired code.
        Check(CampaignFingerprint.Of(Fixture()) == 0xE7C481E45B745B52UL,
            "new fields at their legacy defaults retain the pre-fix golden fingerprint");
        NewStateChangesFingerprint();
        ExistingStateValuesAreNotJustPresenceFlags();
        DictionaryOrderIsNotAuthoritative();
        CompactPersistenceRetainsState();
        HashingDoesNotConsumeRandomness();
    }

    private static void ExistingStateValuesAreNotJustPresenceFlags()
    {
        var changes = new (string Label, Action<Campaign> Start, Action<Campaign> Change)[]
        {
            ("consolidation project identity", c => c.Nations[0].ConsolidationProject = "union_state", c => c.Nations[0].ConsolidationProject = "yugoslavia"),
            ("consolidation partner identity", c => c.Nations[0].ConsolidationFocusId = 1, c => c.Nations[0].ConsolidationFocusId = 2),
            ("existing consolidation ambition", c => c.Nations[0].ConsolidationAmbition = .55f, c => c.Nations[0].ConsolidationAmbition = .56f),
            ("existing address cooldown date", c => c.Nations[0].LastAddressDay = 0, c => c.Nations[0].LastAddressDay = 1),
            ("existing address weariness", c => c.Nations[0].AddressWeariness = 1f, c => c.Nations[0].AddressWeariness = 2f),
            ("existing promise identity", c => c.Nations[0].AddressPromise = "prosperity", c => c.Nations[0].AddressPromise = "order"),
            ("existing promise due date", c => c.Nations[0].AddressPromiseDueDay = 180, c => c.Nations[0].AddressPromiseDueDay = 181),
            ("existing promise comparison mark", c => c.Nations[0].AddressPromiseMark = 100f, c => c.Nations[0].AddressPromiseMark = 101f),
            ("existing contractor expiration", c => c.Armies[0].ContractUntilDay = 182, c => c.Armies[0].ContractUntilDay = 183),
            ("existing world-war recognition date", c => c.Wars[0].WorldWarDay = 12, c => c.Wars[0].WorldWarDay = 13),
            ("existing covert operation due date", c => c.Nations[0].CovertOperations.Add(new CovertOperation { Kind = "sabotage", TargetId = 1, DueDay = 30 }),
                c => c.Nations[0].CovertOperations[0].DueDay = 31),
            ("existing covert operation region", c => c.Nations[0].CovertOperations.Add(new CovertOperation { Kind = "unrest", TargetId = 1, RegionMapIndex = 4 }),
                c => c.Nations[0].CovertOperations[0].RegionMapIndex = 5),
            ("existing lying-low date", c => c.Nations[0].CovertQuietUntil[1] = 90, c => c.Nations[0].CovertQuietUntil[1] = 91)
        };
        foreach (var (label, start, change) in changes)
        {
            var c = Fixture(); start(c);
            var before = CampaignFingerprint.Of(c);
            change(c);
            Check(CampaignFingerprint.Of(c) != before, label + " changes the hash even when the state already exists");
        }
    }

    private static void NewStateChangesFingerprint()
    {
        var changes = new (string Label, Action<Campaign> Change)[]
        {
            ("intelligence network strength", c => c.Nations[0].IntelNetworks[1] = .35f),
            ("initial organization belonging", c => c.Nations[0].OrganizationBelongingAtStart[7] = .30f),
            ("consolidation project", c => c.Nations[0].ConsolidationProject = "union_state"),
            ("consolidation partner zero is not the missing-partner sentinel", c => c.Nations[1].ConsolidationFocusId = 0),
            ("consolidation ambition", c => c.Nations[0].ConsolidationAmbition = .55f),
            ("address cooldown starting on day zero", c => c.Nations[0].LastAddressDay = 0),
            ("address weariness", c => c.Nations[0].AddressWeariness = 1f),
            ("outstanding address promise", c => c.Nations[0].AddressPromise = "prosperity"),
            ("address promise due on day zero", c => c.Nations[0].AddressPromiseDueDay = 0),
            ("address promise comparison mark", c => c.Nations[0].AddressPromiseMark = 100f),
            ("contract expiration on day zero", c => c.Armies[0].ContractUntilDay = 0),
            ("world-war recognition on day zero", c => c.Wars[0].WorldWarDay = 0),
            ("covert operation under way", c => c.Nations[0].CovertOperations.Add(new CovertOperation { Kind = "theft", TargetId = 0 })),
            ("network lying low until day zero", c => c.Nations[0].CovertQuietUntil[0] = 0)
        };
        var failures = new List<string>();
        foreach (var (label, change) in changes)
        {
            var c = Fixture();
            var before = CampaignFingerprint.Of(c);
            change(c);
            if (CampaignFingerprint.Of(c) == before)
            {
                Console.WriteLine("FAIL living-world fingerprint: " + label);
                failures.Add(label);
            }
            else Console.WriteLine("PASS living-world fingerprint: " + label);
        }
        if (failures.Count > 0)
            throw new Exception("living-world fingerprint omitted " + failures.Count + " authoritative fields: " + string.Join(", ", failures));

        var network = Fixture(); network.Nations[0].IntelNetworks[1] = .35f;
        var baseline = CampaignFingerprint.Of(network);
        network.Nations[0].IntelNetworks[1] = .36f;
        Check(CampaignFingerprint.Of(network) != baseline, "an existing network's changed strength is not just a count change");
        network.Nations[0].IntelNetworks = new Dictionary<int, float> { [2] = .35f };
        Check(CampaignFingerprint.Of(network) != baseline, "a network's target identity is fingerprinted");
        network.Nations[0].IntelNetworks = new Dictionary<int, float> { [1] = 0f };
        var zero = CampaignFingerprint.Of(network);
        network.Nations[0].IntelNetworks.Clear();
        Check(CampaignFingerprint.Of(network) != zero, "a zero-strength network still occupies a service slot and is not an absent network");
    }

    private static void DictionaryOrderIsNotAuthoritative()
    {
        var first = Fixture(); var second = Fixture();
        first.Nations[0].IntelNetworks = new Dictionary<int, float> { [2] = .5f, [1] = .25f };
        second.Nations[0].IntelNetworks = new Dictionary<int, float> { [1] = .25f, [2] = .5f };
        first.Nations[0].OrganizationBelongingAtStart = new Dictionary<int, float> { [12] = .6f, [7] = .4f };
        second.Nations[0].OrganizationBelongingAtStart = new Dictionary<int, float> { [7] = .4f, [12] = .6f };
        Check(CampaignFingerprint.Of(first) == CampaignFingerprint.Of(second), "numeric dictionary insertion order does not desynchronise peers");
        var before = CampaignFingerprint.Of(second);
        second.Nations[0].OrganizationBelongingAtStart[7] = .41f;
        Check(CampaignFingerprint.Of(second) != before, "an existing organization's initial belonging value is fingerprinted");
    }

    private static void CompactPersistenceRetainsState()
    {
        var c = Fixture();
        var n = c.Nations[0];
        n.IntelNetworks[2] = .7f; n.OrganizationBelongingAtStart[7] = .2f;
        n.ConsolidationProject = "union_state"; n.ConsolidationFocusId = 0; n.ConsolidationAmbition = .8f;
        n.LastAddressDay = 0; n.AddressWeariness = 2.5f; n.AddressPromise = "order";
        n.AddressPromiseDueDay = 180; n.AddressPromiseMark = .65f;
        c.Armies[0].ContractUntilDay = 182; c.Wars[0].WorldWarDay = 0;
        n.CovertOperations.Add(new CovertOperation { Kind = "opposition", TargetId = 0, RegionMapIndex = -1, StartedDay = 0, DueDay = 42, Spent = .3f });
        n.CovertQuietUntil[1] = 0;
        var options = new JsonSerializerOptions
        {
            IncludeFields = true,
            PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { SaveDefaults.Modify } }
        };
        var clone = Fixture();
        clone.Nations.Clear(); clone.Armies.Clear(); clone.Wars.Clear();
        clone.Nations.AddRange(c.Nations.Select(x => JsonSerializer.Deserialize<Nation>(JsonSerializer.Serialize(x, options), options)));
        clone.Armies.AddRange(c.Armies.Select(x => JsonSerializer.Deserialize<Army>(JsonSerializer.Serialize(x, options), options)));
        clone.Wars.AddRange(c.Wars.Select(x => JsonSerializer.Deserialize<StrategicWar>(JsonSerializer.Serialize(x, options), options)));
        Check(CampaignFingerprint.Of(clone) == CampaignFingerprint.Of(c), "all new authoritative fields retain their fingerprint through the real compact entity serializer");
        Check(clone.Nations[0].ConsolidationFocusId == 0 && clone.Wars[0].WorldWarDay == 0,
            "compact serialization does not turn real actor/day zero into negative defaults");
    }

    private static void HashingDoesNotConsumeRandomness()
    {
        var c = Fixture();
        c.Nations[0].IntelNetworks[1] = .3f;
        var rng = c.Random.State;
        var before = CampaignFingerprint.Of(c);
        Check(CampaignFingerprint.Of(c) == before && c.Random.State == rng,
            "hashing is repeatable and consumes no campaign randomness");
    }

    private static Campaign Fixture()
    {
        var c = new Campaign(112017) { ScenarioId = "living_world_fingerprint_fixture" };
        for (var id = 0; id < 3; id++)
            c.Nations.Add(new Nation { Id = id, MapIndex = 501 + id, Tag = "FP" + id,
                Name = "Fingerprint country " + id, Population = 1_000_000, Gdp = 100, Treasury = 40 });
        c.Armies.Add(new Army { Id = 7, NationId = 0, ProvinceId = 3, Divisions = 1,
            Strength = 1, Organisation = 1 });
        c.Wars.Add(new StrategicWar { Id = 12, AttackerId = 0, DefenderId = 1 });
        return c;
    }
}
