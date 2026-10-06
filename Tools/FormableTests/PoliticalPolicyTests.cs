using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using DealOrForce.Simulation;

static class PoliticalPolicyTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS policies: " + label); }
    public static void Run()
    {
        PoliticalPolicyCatalog.ResetCatalog();
        Check(PoliticalPolicyCatalog.AllLaws.Count() == 45 && PoliticalPolicyCatalog.AllLaws.GroupBy(p => p.Category).Count() == 15,
            "45 laws in 15 categories load");
        Check(PoliticalPolicyCatalog.AllAgendas.Count() == 6, "six agendas load");
        var errors = new List<string>();
        foreach (var doc in GameDataLoader.DataDocuments("common", "laws")) using (doc) PoliticalPolicyCatalog.MergeLaws(doc.RootElement, errors);
        Check(errors.Count == 0, "all vanilla laws pass scope/effect validation");
        var c = Campaign.CreateBootstrap(701); var n = c.NationByTag("ITA"); c.ConfigureSinglePlayer(n.Id);
        n.Treasury = 100;
        var law = PoliticalPolicyCatalog.Find("rights_association");
        Check(PoliticalPolicies.Adopt(c, n, law), "government can reform a national law");
        Check(NationalModifiers.Of(n, "movement_activity") >= .15f, "law modifiers reach the simulation");
        Check(!PoliticalPolicies.Adopt(c, n, law) && !PoliticalPolicies.Adopt(c, n, PoliticalPolicyCatalog.Find("rights_emergency")),
            "repetition and immediate contradictory reform are blocked");
        c.SetFeature("laws", false);
        Check(NationalModifiers.Of(n, "movement_activity") == 0 && !PoliticalPolicies.Adopt(c, n, law),
            "feature disable removes effects without deleting adopted laws");
        c.SetFeature("laws", true);
        Check(NationalModifiers.Of(n, "movement_activity") >= .15f, "reactivation restores declared policy");
        var mine = c.StatesOf(n).First(); var other = c.StatesOf(c.NationByTag("FRA")).First();
        var regional = PoliticalPolicyCatalog.Find("regional_autonomy");
        Check(!PoliticalPolicies.Adopt(c, n, regional, region: other.MapIndex), "regional reform rejects foreign territory");
        Check(PoliticalPolicies.Adopt(c, n, regional, region: mine.MapIndex), "regional measure can be enacted in own territory");
        mine.Radicalism = .5f;
        var radicalism = mine.Radicalism; var foreignRadicalism = other.Radicalism;
        PoliticalPolicies.Tick(c);
        Check(mine.Radicalism < radicalism && other.Radicalism == foreignRadicalism, "regional law affects only its selected region");
        var after = mine.Radicalism; PoliticalPolicies.Tick(c);
        Check(after == mine.Radicalism, "duplicate daily review does not double effects");
        var partner = c.NationByTag("ROU"); var external = PoliticalPolicyCatalog.Find("foreign_partnership");
        Check(PoliticalPolicies.Adopt(c, n, external, partner.Tag) && PoliticalPolicies.ForeignTrust(c, n, partner) == .06f,
            "external doctrine targets one specified partner");
        Check(PoliticalPolicies.ForeignTrust(c, n, c.NationByTag("FRA")) == 0, "external doctrine is not a global bonus");
        var agenda = PoliticalPolicyCatalog.Find("national_reconciliation", true);
        Check(PoliticalPolicies.Adopt(c, n, agenda, agenda: true), "political promise starts a timed mandate");
        Check(!PoliticalPolicies.Adopt(c, n, agenda, agenda: true), "active promises cannot be spammed");
        n.Stability = .8f;
        void Day(int d) => typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, (long)d * 24);
        for (var day = 1; day <= 90; day++) { Day(day); PoliticalPolicies.Tick(c); }
        c.SetFeature("political_agendas", false); var progress = n.Agenda.ElapsedDays;
        Day(160); PoliticalPolicies.Tick(c);
        Check(n.Agenda.ElapsedDays == progress, "disabled agenda pauses rather than completing in secret");
        c.SetFeature("political_agendas", true);
        for (var day = 161; day <= 260; day++) { Day(day); PoliticalPolicies.Tick(c); }
        Check(n.Agenda.Completed && n.Agenda.Succeeded, "sustained achievement completes a promise");
        var fingerprint = CampaignFingerprint.Of(c); n.Laws.Values.First().AdoptedDay++;
        Check(fingerprint != CampaignFingerprint.Of(c), "scoped laws are in network fingerprint");
        var json = JsonSerializer.Serialize(n, new JsonSerializerOptions { IncludeFields = true });
        var restored = JsonSerializer.Deserialize<Nation>(json, new JsonSerializerOptions { IncludeFields = true });
        Check(restored.Laws.Count == n.Laws.Count && restored.Agenda.Succeeded, "laws and agenda outcomes serialize");
        using var invalid = JsonDocument.Parse("{\"rights_association\":{\"scope\":\"region\",\"modifiers\":{\"output\":0.5}}}");
        PoliticalPolicyCatalog.MergeLaws(invalid.RootElement, errors);
        Check(errors.Count > 0 && PoliticalPolicyCatalog.Find("rights_association").Scope == "nation",
            "invalid scoped overlay is rejected atomically");
        Check(NetworkCommandCodec.TryEncode(new AdoptPoliticalPolicyCommand { IssuerId = n.Id, PolicyId = law.Id }, out var type, out var payload) &&
            NetworkCommandCodec.TryDecode(type, payload, out _), "policy command roundtrips through network whitelist");
        var roman = FormableNations.Find("roman_restoration");
        Check(FormableNations.Eligible(n, roman) && FormableNations.Eligible(partner, roman), "Italy and Romania can pursue Roman restoration");
    }
}
