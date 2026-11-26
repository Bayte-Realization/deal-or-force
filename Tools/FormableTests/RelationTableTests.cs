using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The relation tables are keyed by a pair of country ids packed into a long, and hashed by
/// <see cref="PairKeyComparer"/> so that a lookup finds its own entry instead of walking a
/// chain of every pair with the same `a ^ b`. Changing the hash must change nothing a walk
/// of the table can see: the order of a walk is the order of the additions, and a relation
/// is the same object whichever of the two tables it is found in.
/// </summary>
static class RelationTableTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("relation table: " + label);
        Console.WriteLine("PASS relation table: " + label);
    }

    public static void Run()
    {
        // Five hundred countries: every pair gets a code of its own, or near enough.
        var codes = new HashSet<int>();
        var folded = new HashSet<int>();
        var pairs = 0;
        for (var low = 0; low < 500; low++)
            for (var high = low + 1; high < 500; high++)
            {
                var key = ((long)low << 32) | (uint)high;
                codes.Add(PairKeyComparer.Instance.GetHashCode(key));
                folded.Add(key.GetHashCode());
                pairs++;
            }
        Console.WriteLine($"relation table: {pairs} pairs, {codes.Count} codes mixed, {folded.Count} codes folded");
        Check(codes.Count >= pairs * 99L / 100, "the pair hash tells " + pairs + " pairs apart");

        // The same additions, removals and re-additions give the same walk under either hash.
        var plain = new Dictionary<long, int>();
        var mixed = new Dictionary<long, int>(PairKeyComparer.Instance);
        var state = 2463534242u;
        uint Next() { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return state; }
        for (var step = 0; step < 40_000; step++)
        {
            var a = (int)(Next() % 180);
            var b = (int)(Next() % 180);
            if (a == b) continue;
            var key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
            if (Next() % 5 == 0) { plain.Remove(key); mixed.Remove(key); }
            else { plain[key] = step; mixed[key] = step; }
        }
        var same = plain.Count == mixed.Count;
        using (var left = plain.GetEnumerator())
        using (var right = mixed.GetEnumerator())
            while (same && left.MoveNext())
                same = right.MoveNext() && left.Current.Key == right.Current.Key && left.Current.Value == right.Current.Value;
        Check(same && plain.Count > 1000, "a walk of the table is in the order of the additions, whatever the hash");

        var campaign = World(out var asker, out var patron, out var rival, out var stranger);
        Check(ReferenceEquals(campaign.Relations.Comparer, PairKeyComparer.Instance) &&
              ReferenceEquals(campaign.DormantRelations.Comparer, PairKeyComparer.Instance),
            "a campaign's two relation tables use the pair hash");

        var bond = CampaignDiplomacy.Get(campaign, asker.Id, patron.Id);
        bond.Stance = DiplomaticStance.Allied;
        var feud = CampaignDiplomacy.Get(campaign, rival.Id, asker.Id);
        Check(ReferenceEquals(CampaignDiplomacy.Peek(campaign, patron.Id, asker.Id, out var awake), bond) && awake,
            "a relation that is awake is the one Get returns");
        Check(CampaignDiplomacy.Peek(campaign, asker.Id, stranger.Id, out awake) is null && !awake,
            "a pair with no relation reads as none, and reading opens none");

        // Asleep: out of the daily table, still read, woken by the first writer.
        var key01 = ((long)Math.Min(feud.A, feud.B) << 32) | (uint)Math.Max(feud.A, feud.B);
        campaign.Relations.Remove(key01);
        feud.Dormant = true;
        campaign.DormantRelations[key01] = feud;
        Check(ReferenceEquals(CampaignDiplomacy.Peek(campaign, asker.Id, rival.Id, out awake), feud) && !awake,
            "a sleeping relation is read without being woken");

        feud.Reasons.Add(new OpinionReason { Id = "fear_of_strength", Scale = 0.75f });
        var all = SaveSystem.CampaignSave.Capture(campaign, "", includeRelations: true);
        var deferred = SaveSystem.CampaignSave.Capture(campaign, "", includeRelations: false);
        all.Isolate(campaign);
        deferred.Isolate(campaign);
        var order = campaign.Relations.Keys.Concat(campaign.DormantRelations.Keys).ToArray();
        Check(ReferenceEquals(all.Relations.Comparer, PairKeyComparer.Instance) &&
              ReferenceEquals(deferred.Relations.Comparer, PairKeyComparer.Instance) &&
              all.Relations.Keys.SequenceEqual(order) && deferred.Relations.Keys.SequenceEqual(order),
            "both save capture paths use the pair hash and keep the active and dormant insertion order");

        Check(ReferenceEquals(CampaignDiplomacy.Get(campaign, asker.Id, rival.Id), feud) && !feud.Dormant &&
              ReferenceEquals(CampaignDiplomacy.Peek(campaign, asker.Id, rival.Id, out awake), feud) && awake &&
              campaign.DormantRelations.Count == 0 && campaign.Relations.Count == 2,
            "the first writer wakes it: the same relation, back in the daily table, neither lost nor doubled");
        feud.Reasons[0].Scale = 0.5f;
        Check(all.Relations[key01].Dormant && all.Relations[key01].Reasons[0].Scale == 0.75f &&
              deferred.Relations[key01].Dormant && deferred.Relations[key01].Reasons[0].Scale == 0.75f,
            "waking and editing an old relation leaves both detached saves unchanged");
    }

    static Campaign World(out Nation asker, out Nation patron, out Nation rival, out Nation stranger)
    {
        var campaign = new Campaign(61114);
        asker = campaign.AddNation(new Nation
        {
            Tag = "ASK", Name = "Asker", MapIndex = 11, Population = 2_000_000, Gdp = 40,
            Stability = 0.7f, Ideology = "liberalism", AssessedDay = 0, AssessedStrength = 4f
        });
        patron = campaign.AddNation(new Nation
        {
            Tag = "PAT", Name = "Patron", MapIndex = 12, Population = 90_000_000, Gdp = 40_000,
            Stability = 0.7f, Ideology = "liberalism", AssessedDay = 0, AssessedStrength = 4f
        });
        rival = campaign.AddNation(new Nation
        {
            Tag = "RIV", Name = "Rival", MapIndex = 13, Population = 20_000_000, Gdp = 400,
            Stability = 0.7f, Ideology = "liberalism", AssessedDay = 0, AssessedStrength = 4f
        });
        stranger = campaign.AddNation(new Nation
        {
            Tag = "STR", Name = "Stranger", MapIndex = 14, Population = 5_000_000, Gdp = 100,
            Stability = 0.7f, Ideology = "liberalism", AssessedDay = 0, AssessedStrength = 4f
        });
        return campaign;
    }
}
