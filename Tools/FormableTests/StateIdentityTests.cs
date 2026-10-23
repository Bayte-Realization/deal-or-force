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
        var choices = StateIdentity.ReligionChoices(c, n);
        Check(choices.Count(choice => choice.Id == "irreligion") == 1 &&
            choices.Select(choice => choice.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == choices.Count,
            "irreligion appears once even when offered by both demography and secular policy");
        var secularChoices = choices.Where(choice => StateIdentity.IsSecular(choice.Id)).ToList();
        Check(secularChoices.Count == 1 && secularChoices[0].Id == StateIdentity.SecularChoice,
            "the faith menu offers exactly one non-religious answer, under one name");
        Check(StateIdentity.Name(StateIdentity.SecularChoice, true) == Loc.Get("Secular state"),
            "a secular declaration is named as a policy, not as a congregation");
        Check(c.ElapsedDays - n.IdentityChangedDay >= c.Defines.StateIdentityCooldownDays,
            "first sight declares what the country already is without starting the five-year cooldown");
        var headline = StateIdentity.Headline(c, n, false);
        Check(headline.Id == n.DeclaredCulture && headline.Share >= 0.5f,
            "the headline identity is the declared one, with its exact share");
        var brief = StateIdentity.Brief(c, n);
        Check(brief.Contains(StateIdentity.Name(n.DeclaredReligion, true), StringComparison.Ordinal) &&
              brief.Contains(StateIdentity.Name(n.DeclaredCulture, false), StringComparison.Ordinal),
            "the compact identity line names both the faith and the people");
        Check(CampaignFingerprint.Of(c) == plain, "building religion choices does not mutate the campaign");
        new DeclareStateIdentityCommand { IssuerId = n.Id, Culture = n.DeclaredCulture }.Apply(c);
        Check(n.IdentityChangedDay < 0 && CampaignFingerprint.Of(c) == plain,
            "re-declaring the same identity changes nothing and starts no cooldown");
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

        // Co-religionists and regional cohort alienation.
        var partner = c.NationByTag("BGR");
        n.DeclaredReligion = "eastern_orthodox";
        partner.DeclaredReligion = "eastern_orthodox";
        Check(StateIdentity.ShareDeclaredFaith(n, partner),
            "two states with the same non-secular declared faith recognize each other");
        partner.DeclaredReligion = "irreligion";
        Check(!StateIdentity.ShareDeclaredFaith(n, partner),
            "a secular declaration does not count as a shared church");

        var sampleState = c.StatesOf(n).First(s => s.Population > 0 && s.MapIndex != n.CapitalStateMapIndex);
        n.DeclaredCulture = pluralityCulture;
        n.DeclaredReligion = "";
        Check(StateIdentity.RegionAlienation(c, n, sampleState) == 0f,
            "a region whose majority matches the declared identity feels no alienation");
        n.DeclaredCulture = "no_majority_here";
        Check(StateIdentity.RegionAlienation(c, n, sampleState) >= 0.9f,
            "a region outside the declared identity registers high alienation");
        sampleState.PoliticalDivergence = 0.70f;
        sampleState.GovernmentApproval = 0.25f;
        sampleState.LawApproval = 0.30f;
        n.Stability = 0.35f;
        n.ShortageIndex = 0f;
        n.DebtCondition = DebtCondition.Current;
        sampleState.StandardOfLiving = n.StandardOfLiving;
        var (cause, chip) = PoliticalCohorts.CauseOf(c, n, sampleState);
        Check(chip == "declared identity" && cause.Contains("declared identity"),
            "cohort cause names the declared identity when it alienates the region");
        Check(SecessionCore.IsFederalBreak(c, n, sampleState),
            "severe state-identity alienation can supply the grievance stress on the federal road");

        // The opinion driver: the same church warms most, the same people less, a difference
        // nothing at all - and the whole driver switches off with its weight.
        n.DeclaredReligion = "eastern_orthodox";
        partner.DeclaredReligion = "eastern_orthodox";
        n.DeclaredCulture = "romanian";
        partner.DeclaredCulture = "bulgarian";
        var both = StateIdentity.Affinity(c, n.Id, partner.Id);
        partner.DeclaredCulture = "romanian";
        var sameBoth = StateIdentity.Affinity(c, n.Id, partner.Id);
        partner.DeclaredReligion = "sunni";
        partner.DeclaredCulture = "turkish";
        var neither = StateIdentity.Affinity(c, n.Id, partner.Id);
        c.Defines.IdentityOpinionWeight = 9f;
        Check(both > 0f && sameBoth > both && neither == 0f,
            $"the opinion driver warms a shared church most, a shared people less, a difference never ({both:F1}/{sameBoth:F1}/{neither:F1})");
        c.Defines.IdentityOpinionWeight = 0f;
        Check(StateIdentity.Affinity(c, n.Id, partner.Id) == 0f, "a zero weight switches the driver off");
        c.Defines.IdentityOpinionWeight = 9f;

        // The weekly strain: a declared faith short of half the country pays stability every
        // day, scaled by the gap; a majority faith and a secular answer pay nothing.
        n.Stability = 0.80f;
        var minorityFaith = StateIdentity.Candidates(c, n, true)
            .Where(choice => !StateIdentity.IsSecular(choice.Id) && choice.Share < 0.35f)
            .Select(choice => choice.Id).FirstOrDefault();
        if (minorityFaith is not null)
        {
            n.DeclaredReligion = minorityFaith;
            StateIdentity.EnsureDeclared(c);
            Check(n.Stability < 0.80f,
                $"a faith against the majority drains stability every day ({minorityFaith})");
            n.Stability = 0.80f;
            n.DeclaredReligion = "irreligion";
            StateIdentity.EnsureDeclared(c);
            Check(MathF.Abs(n.Stability - 0.80f) < 0.0001f,
                "a secular declaration pays no strain whatever its share");
        }
        var majorityFaith = StateIdentity.Candidates(c, n, true).First().Id;
        n.DeclaredReligion = majorityFaith;
        n.Stability = 0.80f;
        StateIdentity.EnsureDeclared(c);
        Check(MathF.Abs(n.Stability - 0.80f) < 0.0001f,
            "a majority faith pays no strain");
    }
}
