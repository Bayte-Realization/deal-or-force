using System;
using System.Linq;
using DealOrForce.Simulation;

public static class ShardConsolidationTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("ShardConsolidation: " + label);
        Console.WriteLine("PASS shard consolidation: " + label);
    }

    public static void Run()
    {
        SplinterDetection();
        ShardWillingness();
        ShardIntegration();
    }

    private static void SplinterDetection()
    {
        var campaign = new Campaign(1, new Defines());
        var rebel = new Nation { Id = 0, Tag = "R_UKR", Population = 500_000, MapIndex = 1 };
        var large = new Nation { Id = 1, Tag = "RUS", Population = 140_000_000, MapIndex = 2 };
        var small = new Nation { Id = 2, Tag = "BLR", Population = 9_000_000, MapIndex = 3 };
        campaign.Nations.AddRange(new[] { rebel, large, small });

        Check(Consolidation.IsSplinterOrShard(campaign, rebel), "rebel tag is recognized as a splinter shard");
        Check(Consolidation.IsSplinterOrShard(campaign, small), "small sovereign state is recognized as a shard");
        Check(!Consolidation.IsSplinterOrShard(campaign, large), "great power population exceeds shard limit");

        campaign.Fallen.Add(small.Id);
        Check(!Consolidation.IsSplinterOrShard(campaign, small), "fallen nation is not an eligible shard");
    }

    private static void ShardWillingness()
    {
        var campaign = new Campaign(1, new Defines());
        var a = new Nation { Id = 0, Tag = "UKR", Population = 3_000_000, Ideology = "democracy", Stability = 0.6f };
        var b = new Nation { Id = 1, Tag = "R_UKR", Population = 500_000, Ideology = "democracy", Stability = 0.5f };
        var c = new Nation { Id = 2, Tag = "SOM", Population = 500_000, Ideology = "nationalism", Stability = 0.3f };
        campaign.Nations.AddRange(new[] { a, b, c });

        CampaignDiplomacy.Get(campaign, a.Id, b.Id).Opinion = 40f;
        CampaignDiplomacy.Get(campaign, a.Id, c.Id).Opinion = 0f;

        var kindredWillingness = Consolidation.ShardWillingness(campaign, a, b);
        var foreignWillingness = Consolidation.ShardWillingness(campaign, a, c);

        Check(kindredWillingness >= 40f, "kindred shards with positive opinion have substantial unification willingness");
        Check(kindredWillingness > foreignWillingness, "kindred shards have far higher willingness than culturally foreign states");
    }

    private static void ShardIntegration()
    {
        var campaign = new Campaign(1, new Defines());
        var founder = new Nation { Id = 0, Tag = "UKR", Population = 5_000_000, Gdp = 20_000_000_000f, MapIndex = 1 };
        var partner = new Nation { Id = 1, Tag = "R_ODS", Population = 1_000_000, Gdp = 4_000_000_000f, MapIndex = 2 };
        campaign.Nations.AddRange(new[] { founder, partner });

        var initialGdp = founder.Gdp;
        var initialPartnerGdp = partner.Gdp;

        var cmd = new ConsolidateSplintersCommand { IssuerId = founder.Id, PartnerId = partner.Id };
        cmd.Apply(campaign);

        Check(campaign.Fallen.Contains(partner.Id), "partner nation is recorded as fallen/absorbed");
        Check(founder.Gdp >= initialGdp + initialPartnerGdp * 0.99f, "partner GDP is transferred to the union founder");
        Check(campaign.Chronicle.Entries.Any(e => e.Kind == "shard_consolidated" && e.ActorId == founder.Id && e.TargetId == partner.Id),
            "chronicle records shard_consolidated event");
    }
}
