using System;
using System.Linq;
using DealOrForce.Simulation;

static class StateIdentityTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS state identity: " + label); }

    public static void Run()
    {
        var c = Campaign.CreateBootstrap(805);
        var n = c.NationByTag("ROU");
        c.Defines.StateIdentityShare = 0.05f;

        StateIdentity.EnsureDeclared(c);
        Check(n.DeclaredCulture.Length > 0 && n.DeclaredReligion.Length > 0,
            "first sight declares the country's own plurality");
        var pluralityCulture = StateIdentity.Candidates(c, n, false).First().Id;
        Check(n.DeclaredCulture == pluralityCulture,
            "the declared culture is the largest people of the country");
        var share = StateIdentity.CultureShare(c, n, pluralityCulture);
        Check(share >= 0.5f, $"the plurality really is a majority (got {share:P0})");

        var plain = CampaignFingerprint.Of(c);
        new DeclareStateIdentityCommand { IssuerId = n.Id, Culture = n.DeclaredCulture }.Apply(c);
        Check(n.IdentityChangedDay >= 0 && CampaignFingerprint.Of(c) == plain,
            "re-declaring the same identity changes nothing");
        // The first-sight declaration started the cooldown; step past it for the refusal tests.
        n.IdentityChangedDay = -9999;

        // A group below the share line is refused with its reason; a fabricated one too.
        var tiny = StateIdentity.Candidates(c, n, false).Last().Id;
        if (StateIdentity.CultureShare(c, n, tiny) < 0.05f)
        {
            var refusal = StateIdentity.BlockReason(c, n, "", tiny);
            Check(refusal.Length > 0 && refusal.Contains("under"),
                "a too-small people is refused with its share");
            new DeclareStateIdentityCommand { IssuerId = n.Id, Culture = tiny }.Apply(c);
            Check(n.DeclaredCulture != tiny, "the refused declaration did not land");
        }
        Check(StateIdentity.BlockReason(c, n, "", "no_such_people").Length > 0,
            $"an unknown people is refused (candidates: {string.Join(",", StateIdentity.Candidates(c, n, false).Select(entry => $"{entry.Id}:{entry.Item2:P1}"))})");

        // A declaration against the grain: costs stability, is public news, starts the clock.
        // The threshold drops for the fixture, because Romania carries no second people above
        // the shipped line - the rule under test is the declaration's, not the threshold's.
        c.Defines.StateIdentityShare = 0.001f;
        var other = StateIdentity.Candidates(c, n, false)
            .Skip(1).FirstOrDefault(entry => entry.Item2 >= 0.001f);
        Check(other.Id is not null, $"a second people exists for the against-grain case (got {other.Id ?? "none"}, declared {n.DeclaredCulture})");
        if (other.Id is not null && !other.Id.Equals(n.DeclaredCulture, StringComparison.Ordinal))
        {
            var stability = n.Stability;
            new DeclareStateIdentityCommand { IssuerId = n.Id, Culture = other.Id }.Apply(c);
            Check(n.DeclaredCulture == other.Id, "a qualifying declaration lands");
            Check(n.Stability < stability, "a declaration against the majority costs stability");
            Check(c.Chronicle.Entries.Any(e => e.Kind == "state_identity_declared" && e.ActorId == n.Id),
                "the declaration is public news");
            Check(CampaignFingerprint.Of(c) != plain, "the fingerprint covers the declared identity");
            Check(StateIdentity.BlockReason(c, n, "", other.Id).Length > 0,
                "the cooldown refuses anything further today");
        }

        // The secular answer needs no congregation: even below the share line it lands.
        n.IdentityChangedDay = -9999;
        var faithShare = StateIdentity.ReligionShare(c, n, "irreligion");
        new DeclareStateIdentityCommand { IssuerId = n.Id, Religion = "irreligion" }.Apply(c);
        Check(n.DeclaredReligion == "irreligion",
            $"the secular declaration lands with no congregation (share was {faithShare:P0})");

        // Saved state round-trips with the nation.
        var options = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
        var saved = System.Text.Json.JsonSerializer.Deserialize<Nation>(
            System.Text.Json.JsonSerializer.Serialize(n, options), options);
        Check(saved!.DeclaredReligion == n.DeclaredReligion && saved.DeclaredCulture == n.DeclaredCulture &&
              saved.IdentityChangedDay == n.IdentityChangedDay,
            "the declaration survives a save roundtrip");

        // Disabled: the mechanic stands down and refuses with its sentence.
        c.SetFeature("state_identity", false);
        Check(StateIdentity.BlockReason(c, n, "irreligion", "").Length > 0,
            "a disabled mechanic refuses with its sentence");
        c.SetFeature("state_identity", true);
    }
}
