using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class OfficeholderModdingTests
{
    static void Check(bool ok, string text)
    { if (!ok) throw new Exception("officeholder: " + text); Console.WriteLine("PASS officeholder: " + text); }

    static ScriptedEffects.Effect Parse(string yaml, List<string> errors = null)
    {
        using var document = StructuredData.ParseDocument("events/people.yml", yaml);
        var result = ScriptedEffects.Parse(document.RootElement, "events/people.yml", errors ?? new());
        return result;
    }

    public static void Run()
    {
        var (c, n, other) = TerritorialRecoveryTests.World();
        c.ConfigureSinglePlayer(n.Id);
        // This tiny territorial fixture has no economy until explicitly initialised.
        // Otherwise loading legitimately runs the legacy zero-output migration.
        EconomySystem.CalibrateOutput(c); EconomySystem.Explain(c);
        n.HeadOfStateName = "Original"; n.HeadOfStateBirthYear = c.Date.Year - 50;
        var trait = Rulers.AllTraits.First().Id;
        var errors = new List<string>();
        var change = Parse("head_of_state:\n  name: Modded Person\n  age: 45\n  trait: " + trait + "\n  portrait: portrait_civilian\n", errors);
        Check(errors.Count == 0 && !change.IsEmpty && change.People.Count == 1, "YAML accepts a typed officeholder effect");
        var before = CampaignFingerprint.Of(c); var random = c.Random.State;
        var lines = ScriptedEffects.Lines(change);
        ScriptedEffects.PreviewForNation(c, n, change);
        Check(lines.Any(s => s.Contains("Modded Person")) && CampaignFingerprint.Of(c) == before && c.Random.State == random,
            "preview describes the person without appointing them or consuming RNG");
        change.Apply(c, n);
        var person = Rulers.HeadOfState(c, n);
        Check(person.Name == "Modded Person" && person.Age == 45 && person.TraitId == trait && person.PortraitId == "portrait_civilian",
            "the event really changes name, age, trait and portrait");
        Check(other.PersonPortraits.Count == 0 && c.Random.State == random, "explicit appointments are country-scoped and deterministic");
        Check(CampaignFingerprint.Of(c) != before, "appointment and appearance enter the campaign fingerprint");
        Parse("head_of_state: {portrait: portrait_uniform}\n").Apply(c, n);
        Check(Rulers.HeadOfState(c, n).PortraitId == "portrait_uniform", "a later event can change only the current portrait");
        var visualBefore = CampaignFingerprint.Of(c);
        Parse("head_of_state: {portrait: portrait_civilian}\n").Apply(c, n);
        Check(CampaignFingerprint.Of(c) != visualBefore, "a portrait-only change is not invisible to state comparison");

        using (var document = StructuredData.ParseDocument("events/condition.yml", "officeholders:\n  head_of_state:\n    name: Modded Person\n    portrait: portrait_civilian\n    min_age: 40\n    max_age: 50\n"))
        {
            var trigger = ScriptedTrigger.Parse(document.RootElement, "condition", errors);
            before = CampaignFingerprint.Of(c);
            Check(!trigger.IsEmpty && trigger.Matches(c, n) && trigger.Shortfall(c, n) == 0, "events can test officeholder name, appearance and age");
            Check(before == CampaignFingerprint.Of(c) && c.Random.State == random, "officeholder conditions are read-only");
            Parse("head_of_state: {name: Successor, age: 35}\n").Apply(c, n);
            Check(!trigger.Matches(c, n) && float.IsPositiveInfinity(trigger.Shortfall(c, n)), "the old-person condition no longer matches after succession");
            Check(Rulers.HeadOfState(c, n).PortraitId == "", "a successor does not inherit their predecessor's photograph");
        }

        Parse("head_of_government: {name: Modded PM, age: 40, portrait: portrait_pm}\n").Apply(c, n);
        Check(Rulers.HasDualExecutive(n) && Rulers.HeadOfGovernment(c, n).Name == "Modded PM" && Rulers.HeadOfGovernment(c, n).PortraitId == "portrait_pm",
            "an explicit PM appointment does not overwrite the president");
        other.Government = "absolute_monarchy";
        var otherHash = CampaignFingerprint.Of(c);
        Parse("head_of_government: {portrait: portrait_pm}\n").Apply(c, other);
        Check(!Rulers.HasDualExecutive(other) && otherHash == CampaignFingerprint.Of(c),
            "a portrait-only edit cannot invent a missing PM or change the monarch's own picture");

        var portfolio = Rulers.AllPortfolios.First(p => p.Options.Count > 0);
        Parse("ministers:\n  " + portfolio.Id + ":\n    name: Modded Minister\n    age: 48\n    option: " + portfolio.Options[0].Id + "\n    portrait: portrait_minister\n").Apply(c, n);
        var minister = OfficeholderEffect.MinisterProfile(c, n, portfolio.Id);
        Check(minister.Name == "Modded Minister" && minister.PortraitId == "portrait_minister" && Rulers.ActiveMinister(n, portfolio.Id) is not null,
            "a data event appoints a real minister with a policy and portrait");

        // Pending choices must retain the new typed action across the actual effect converter.
        var pending = Parse("head_of_state: {name: After Load, age: 42, portrait: portrait_saved}\n");
        var options = new JsonSerializerOptions { IncludeFields = true };
        var restored = JsonSerializer.Deserialize<ScriptedEffects.Effect>(JsonSerializer.Serialize(pending, options), options);
        Check(restored is { People.Count: 1 } && !restored.IsEmpty, "pending choice serialization retains non-numeric effects");
        var folder = Path.Combine(Path.GetTempPath(), "dof-officeholders-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var prepared = SaveSystem.Prepare(folder, "people", c, "officeholder_fixture");
            var fingerprint = CampaignFingerprint.Of(c);
            prepared.Commit();
            var result = SaveSystem.Load(folder, "people", "officeholder_fixture");
            Check(result.Success, "actual campaign save loads: " + result.Warning);
            var loaded = result.Campaign;
            Check(CampaignFingerprint.Of(loaded) == fingerprint, "actual campaign save/load retains officeholder appearances");
            Check(Rulers.HeadOfGovernment(loaded, loaded.NationById(n.Id)).PortraitId == "portrait_pm", "loaded PM still resolves the chosen graphic");
            restored.Apply(loaded, loaded.NationById(n.Id));
            Check(Rulers.HeadOfState(loaded, loaded.NationById(n.Id)).Name == "After Load" && Rulers.HeadOfState(loaded, loaded.NationById(n.Id)).PortraitId == "portrait_saved",
                "restored pending action really applies after loading");
        }
        finally { Directory.Delete(folder, true); }

        Parse("ministers: {" + portfolio.Id + ": {dismiss: true}}\n").Apply(c, n);
        Check(!n.Cabinet.ContainsKey(portfolio.Id) && !n.CabinetPeople.ContainsKey(portfolio.Id), "dismissal removes both policy and minister identity");
        var oldLeader = Rulers.HeadOfState(c, n).Name;
        Parse("head_of_state: {death: true}\n").Apply(c, n);
        Check(Rulers.HeadOfState(c, n).Name != oldLeader && c.Chronicle.Entries.Any(e => e.Kind == "ruler_died"), "scripted ruler death uses the real succession path and news");

        foreach (var invalid in new[] { "head_of_state: {trait: non_existent_trait}", "head_of_state: {portrait: '../outside.png'}",
            "head_of_state: {age: old}", "head_of_state: {age: 40, birth_year: 1980}", "head_of_state: {death: true, name: Wrong}",
            "head_of_state: {unknown: 5}", "ministers: {not_a_portfolio: {name: Wrong}}" })
        {
            errors.Clear(); var bad = Parse(invalid, errors); before = CampaignFingerprint.Of(c); bad.Apply(c, n);
            Check(errors.Count > 0 && bad.IsEmpty && CampaignFingerprint.Of(c) == before, "bad person is diagnosed and rejected atomically: " + invalid);
        }
        Parse("head_of_state: {portrait: ''}\n").Apply(c, n);
        Check(Rulers.HeadOfState(c, n).PortraitId == "", "an empty portrait restores automatic resolution");
        foreach (var invalid in new[] { "officeholders: {head_of_state: {unknown: 1}}", "officeholders: [bad]" })
        {
            using var doc = StructuredData.ParseDocument("bad-condition.yml", invalid);
            errors.Clear(); var condition = ScriptedTrigger.Parse(doc.RootElement, "bad-condition.yml", errors);
            Check(errors.Count > 0 && !condition.IsEmpty && !condition.Matches(c, n) && float.IsPositiveInfinity(condition.Shortfall(c, n)),
                "an invalid person condition fails closed rather than firing an unconditional event");
        }
        var pictureRoot = Path.Combine(Path.GetTempPath(), "dof-person-editor-" + Guid.NewGuid().ToString("N"));
        try
        {
            var authoredNation = new Nation { Tag = "PIC" };
            using (var doc = JsonDocument.Parse("{\"PIC\":{\"head_of_state\":\"Authored\",\"head_of_government\":\"Prime\",\"state_age\":61,\"ministers\":{\"" + portfolio.Id + "\":{\"name\":\"Existing Minister\",\"age\":56,\"option\":\"" + portfolio.Options[0].Id + "\"}}}}"))
                Rulers.MergeHistory(doc.RootElement);
            using (var doc = JsonDocument.Parse("{\"PIC\":{\"state_portrait\":\"authored_photo\"}}")) Rulers.MergeHistory(doc.RootElement);
            Check(Rulers.HeadOfState(c, authoredNation) is { Name: "Authored", Age: 61, PortraitId: "authored_photo" } &&
                Rulers.HeadOfGovernment(c, authoredNation).Name == "Prime" && Rulers.AuthoredMinister("PIC", portfolio.Id).Name == "Existing Minister",
                "a portrait-only history overlay inherits names, ages and other ministers");
            using (var doc = JsonDocument.Parse("{\"PIC\":{\"ministers\":{\"" + portfolio.Id + "\":{\"portrait\":\"minister_photo\"}}}}")) Rulers.MergeHistory(doc.RootElement);
            Check(Rulers.AuthoredMinister("PIC", portfolio.Id) is { Name: "Existing Minister", Age: 56, Portrait: "minister_photo" },
                "a minister's one-photo overlay inherits their own identity and policy");
            var editor = new CountryEditorDocument { Tag = "PIC", Colour = "#123456", HasPortraitSettings = true,
                HeadOfState = "Authored", HeadOfGovernment = "Prime", HeadOfStatePortrait = "portrait_editor_a", HeadOfGovernmentPortrait = "portrait_editor_b" };
            editor.Names["english"] = ("Picture Test", ""); editor.Write(pictureRoot);
            var read = CountryEditorDocument.Read(pictureRoot, "PIC");
            Check(read is { HasPortraitSettings: true, HeadOfStatePortrait: "portrait_editor_a", HeadOfGovernmentPortrait: "portrait_editor_b" },
                "the no-code country editor writes and reopens both portrait choices");
            using (var doc = StructuredData.ReadDocument(Path.Combine(pictureRoot, "history", "rulers", editor.FileStem + ".json"))) Rulers.MergeHistory(doc.RootElement);
            Check(Rulers.HeadOfState(c, authoredNation).PortraitId == "portrait_editor_a" && Rulers.HeadOfGovernment(c, authoredNation).PortraitId == "portrait_editor_b",
                "the real runtime loader consumes the editor's output");
            editor.HeadOfStatePortrait = ""; editor.Write(pictureRoot);
            using (var doc = StructuredData.ReadDocument(Path.Combine(pictureRoot, "history", "rulers", editor.FileStem + ".json"))) Rulers.MergeHistory(doc.RootElement);
            Check(CountryEditorDocument.Read(pictureRoot, "PIC").HeadOfStatePortrait == "" && Rulers.HeadOfState(c, authoredNation).PortraitId == "",
                "choosing Automatic persists an explicit clear instead of reviving the previous portrait");
        }
        finally { Rulers.ResetCatalog(); if (Directory.Exists(pictureRoot)) Directory.Delete(pictureRoot, true); }
        before = CampaignFingerprint.Of(c);
        Check(ContentWorkbench.Validate("events/person.yml", "person_event:\n  effects:\n    head_of_state: {portrait: example_photo}\n").StartsWith("Event scripts valid"),
            "the ordinary content editor validates structured character effects");
        var diagnosed = false;
        try { ContentWorkbench.Validate("events/person.yml", "person_event:\n  options:\n    - effects:\n        head_of_state: {unknown: 1}\n"); }
        catch (InvalidDataException e) { diagnosed = e.Message.Contains("events/person.yml/person_event/options/0") && e.Message.Contains("unknown"); }
        Check(diagnosed && before == CampaignFingerprint.Of(c), "bad character effects are caught in the editor before saving, without mutating the world");
        using (var malformed = StructuredData.ParseDocument("trigger.yml", "min_stability: not_a_number"))
        {
            errors.Clear(); ScriptedTrigger.Parse(malformed.RootElement, "trigger.yml", errors);
            Check(errors.Count > 0 && errors[0].Contains("must be a number"), "a text-valued numeric condition reports a useful diagnostic, not a JSON API exception");
        }
        var spirit = ModifierCatalog.All.First();
        var structured = new ScriptedEffects.Effect(); structured.Grant(spirit.Id, 30);
        Check(!string.IsNullOrWhiteSpace(structured.Describe()) && structured.Describe() != "No effect.",
            "a non-numeric modifier-only choice still has a real consequence description");
    }
}
