using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using DealOrForce.Simulation;

/// <summary>A border cache must notice a vanished legal blocker, not invent a border change.</summary>
static class RegionReunionWakeTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("region reunion wake: " + note); Console.WriteLine("PASS region reunion wake: " + note); }

    static (Campaign World, DealOffer Paper) WithOffer()
    {
        var c = RegionReunionTests.Fixture();
        c.ConfigureSinglePlayer(c.Nations[1].Id);
        var paper = new DealOffer { Id = c.NextDealOfferId++, FromId = c.Nations[0].Id,
            ToId = c.Nations[1].Id, ExpiresDay = c.ElapsedDays + 5,
            From = new Ultimatum.Side { Regions = new[] { 4000 } } };
        c.DealOffers.Add(paper);
        Check(StatePartition.Reunify(c) == 1 && c.States.Any(s => s.MapIndex == 4000),
            "open offer keeps the home shard separate while foreign siblings reunite");
        return (c, paper);
    }

    static void RejoinsWithoutTransfer(Campaign c, string reason)
    {
        var owners = c.ProvinceMap.ProvinceToNation.ToArray();
        var controllers = c.ProvinceController.ToArray();
        var rng = c.Random.State;
        Check(StatePartition.Reunify(c) == 1 && !c.States.Any(s => s.MapIndex == 4000) &&
            c.ProvinceMap.ProvinceToState[1] == 3001, reason);
        Check(owners.SequenceEqual(c.ProvinceMap.ProvinceToNation) && controllers.SequenceEqual(c.ProvinceController) &&
            rng == c.Random.State && c.States.Sum(s => s.Population) == 1000,
            "rechecking the blocker preserves sovereign land, control, RNG and residents");
        var hash = CampaignFingerprint.Of(c);
        Check(StatePartition.Reunify(c) == 0 && hash == CampaignFingerprint.Of(c),
            "the healed world returns to an idempotent cached no-op");
        Check(c.ReunifyBlockedPairs.Count == 0, "healing releases cached references to the old shards");
    }

    public static void Run()
    {
        var (c, paper) = WithOffer();
        var blockedHash = CampaignFingerprint.Of(c);
        Check(StatePartition.Reunify(c) == 0 && CampaignFingerprint.Of(c) == blockedHash,
            "a second call cannot absorb a still-pending offer");
        // Warm the code path before measuring: this is managed allocation on this
        // calling thread, not resident RAM or a frame-time claim.
        for (var i = 0; i < 32; i++) StatePartition.Reunify(c);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) StatePartition.Reunify(c);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Check(allocated == 0, "1000 unchanged blocked checks allocate zero managed bytes after warm-up: " + allocated);
        var mapRevision = c.MapRevision;
        DealNegotiations.Respond(c, paper.ToId, paper.Id, DealReply.Decline);
        Check(paper.State == DealOfferState.Declined && c.MapRevision == mapRevision,
            "real refusal closes the paper without pretending a border moved");
        RejoinsWithoutTransfer(c, "declining the offer wakes reunion without a capture or reload");

        (c, paper) = WithOffer();
        mapRevision = c.MapRevision;
        paper.ExpiresDay = c.ElapsedDays;
        DealNegotiations.Tick(c);
        Check(paper.State == DealOfferState.Expired && c.MapRevision == mapRevision,
            "real inbox expiry does not redraw the political map");
        RejoinsWithoutTransfer(c, "expired territorial offer no longer freezes its shard");

        (c, paper) = WithOffer();
        var demand = new UltimatumPaper { Id = c.NextUltimatumId++, FromId = paper.FromId,
            ToId = paper.ToId, ExpiresDay = c.ElapsedDays + 5, Liberate = true, LiberateRegion = 4000 };
        c.UltimatumPapers.Add(demand);
        DealNegotiations.Respond(c, paper.FromId, paper.Id, DealReply.Withdraw);
        blockedHash = CampaignFingerprint.Of(c);
        Check(StatePartition.Reunify(c) == 0 && CampaignFingerprint.Of(c) == blockedHash,
            "withdrawing one offer does not erase another pending liberation demand");
        mapRevision = c.MapRevision;
        // Isolate the same state transition the inbox cleanup makes, without inventing
        // diplomatic terms for this tiny topology fixture.
        demand.State = UltimatumPaperState.Invalidated;
        Check(c.MapRevision == mapRevision, "closing the final paper is not a map edit");
        RejoinsWithoutTransfer(c, "only the final paper's closure releases the shard");

        c = RegionReunionTests.Fixture();
        c.States[0].AdministeredById = c.Nations[0].Id;
        c.States[1].AdministeredById = c.Nations[1].Id;
        Check(StatePartition.Reunify(c) == 1 && c.States.Any(s => s.MapIndex == 4000),
            "two competing offices remain separate on a warmed cache");
        mapRevision = c.MapRevision;
        ParallelAdministration.Close(c, c.States.Single(s => s.MapIndex == 4000), "withdrawn");
        Check(c.MapRevision == mapRevision, "closing an office changes no sovereign border");
        RejoinsWithoutTransfer(c, "a withdrawn competing office releases its former shard");

        c = RegionReunionTests.Fixture();
        var lease = new LandArrangement { Regions = new() { 4000 } };
        c.LandArrangements.Add(lease);
        Check(StatePartition.Reunify(c) == 1, "a lease protects its exact region footprint");
        c.LandArrangements.Remove(lease);
        RejoinsWithoutTransfer(c, "removed lease is rechecked even without an unrelated map event");

        c = RegionReunionTests.Fixture();
        c.States[1].HeldById = c.Nations[1].Id;
        Check(StatePartition.Reunify(c) == 1, "differing regional occupation is still protected");
        c.States.Single(s => s.MapIndex == 4000).HeldById = c.States[0].HeldById;
        RejoinsWithoutTransfer(c, "matching occupation releases the old partition");

        var (first, firstPaper) = WithOffer();
        var (second, secondPaper) = WithOffer();
        var secondHash = CampaignFingerprint.Of(second);
        DealNegotiations.Respond(first, firstPaper.ToId, firstPaper.Id, DealReply.Decline);
        RejoinsWithoutTransfer(first, "a neighbouring world's cache cannot suppress reunion");
        Check(secondPaper.Pending && StatePartition.Reunify(second) == 0 && CampaignFingerprint.Of(second) == secondHash,
            "another campaign retains its own protected shard and fingerprint");

        (c, paper) = WithOffer();
        // This tiny fixture deliberately starts without a production reference and
        // other opening defaults. Canonicalise it through the real loader while the
        // offer is STILL OPEN, before comparing warm vs cold cache behaviour. Do not
        // mistake one-time legacy/default adoption for a reunion discrepancy.
        var normalised = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "reunion-wake"), "reunion-wake");
        Check(normalised.Success, "snapshot fixture adopts its missing opening defaults before the comparison");
        c = normalised.Campaign;
        c.AttachProvinceTopology(FreshTopology());
        paper = c.DealOffers.Single();
        Check(StatePartition.Reunify(c) == 0 && c.States.Any(s => s.MapIndex == 4000),
            "canonical snapshot fixture warms its cache with the offer still pending");
        DealNegotiations.Respond(c, paper.ToId, paper.Id, DealReply.Decline);
        var restored = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "reunion-wake"), "reunion-wake");
        Check(restored.Success, "a just-closed paper uses real snapshot persistence: " + restored.Warning);
        restored.Campaign.AttachProvinceTopology(FreshTopology());
        RejoinsWithoutTransfer(c, "the warm source world heals just as the cold snapshot does");
        Check(restored.Campaign.States.Count == c.States.Count &&
            restored.Campaign.ProvinceMap.ProvinceToState.SequenceEqual(c.ProvinceMap.ProvinceToState),
            "cold snapshot and warm world agree on region identities after the blocker closes");
        var equal = CampaignFingerprint.Of(restored.Campaign) == CampaignFingerprint.Of(c);
        if (!equal) PrintDifferences(c, restored.Campaign);
        Check(equal,
            "the derived blocker cache cannot change the restored campaign fingerprint");
    }

    static ProvinceTopology FreshTopology() => ProvinceTopology.Build(Enumerable.Repeat((ushort)3001, 8192).ToArray(),
        Enumerable.Repeat((ushort)501, 8192).ToArray(), 128, 64, _ => false);

    static void PrintDifferences(Campaign a, Campaign b)
    {
        JsonNode Read(Campaign world)
        {
            using var bytes = new MemoryStream(SaveSystem.ExportNetworkSnapshot(world, "reunion-wake"));
            using var gzip = new GZipStream(bytes, CompressionMode.Decompress);
            return JsonNode.Parse(gzip);
        }
        var found = 0;
        void Walk(JsonNode left, JsonNode right, string path)
        {
            if (found >= 8 || JsonNode.DeepEquals(left, right)) return;
            if (left is JsonObject lo && right is JsonObject ro)
                foreach (var key in lo.Select(p => p.Key).Union(ro.Select(p => p.Key))) Walk(lo[key], ro[key], path + "." + key);
            else if (left is JsonArray la && right is JsonArray ra && la.Count == ra.Count)
                for (var i = 0; i < la.Count; i++) Walk(la[i], ra[i], path + "[" + i + "]");
            else
            {
                string Short(JsonNode value) { var text = value?.ToJsonString() ?? "null"; return text[..Math.Min(text.Length, 80)]; }
                Console.WriteLine("REUNION_SNAPSHOT_DIFF " + path + ": " + Short(left) + " vs " + Short(right));
                found++;
            }
        }
        Walk(Read(a), Read(b), "world");
    }
}
