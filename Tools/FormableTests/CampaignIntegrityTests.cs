using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DealOrForce.Simulation;

/// <summary>Century-campaign failures must be caught before overwriting a useful save.</summary>
static class CampaignIntegrityTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL integrity: " + label);
        Console.WriteLine("PASS integrity: " + label);
    }

    public static void Run()
    {
        ProductionReference();
        CompactDefaults();
        StrictRows();
        SaveProtection();
        CivilWarRules();
    }

    static void ProductionReference()
    {
        var nation = new Nation { Tag = "CAP", Population = 1_000_000, Gdp = 100f };
        Check(ProductionCapacity.Of(nation) == 100f, "productive baseline starts at the scenario scale");
        nation.Gdp = 1e30f;
        Check(ProductionCapacity.Of(nation) == 100f, "nominal GDP cannot feed back into physical arms output");
        nation.Population = 2_000_000;
        Check(ProductionCapacity.Of(nation) == 200f, "resident growth changes physical capacity");
        nation.Population = 0;
        Check(ProductionCapacity.Of(nation) == 0f, "an entity without residents has no residual industry");
        var antarctica = new Nation { Tag = "ATA", Population = 0, Gdp = 6.3e35f };
        Check(ProductionCapacity.Of(antarctica) == 0f && antarctica.ProductionReferenceGdp == 0f,
            "a legacy empty geography record cannot manufacture arms");
        var c = Campaign.CreateBootstrap(61008);
        var player = c.Player;
        player.Stability = float.NaN;
        Check(CivilWarOutbreak.Chance(c, player) == 0f, "NaN is not an automatic civil-war lottery win");
    }

    static void CompactDefaults()
    {
        var options = new JsonSerializerOptions
        {
            IncludeFields = true,
            PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { SaveDefaults.Modify } }
        };
        var n = new Nation { Tag = "SAV", Name = "Save sentinel fixture", WarPlanTargetId = 0 };
        // Zero is an actual country, while most foreign-country targets start at -1.
        var fields = typeof(Nation).GetFields().Where(f => f.FieldType == typeof(int) && (int)f.GetValue(n) == -1).ToArray();
        Check(fields.Length > 0, "fixture contains non-CLR integer sentinels");
        fields[0].SetValue(n, 0);
        n.DomesticCrisis.ResponseId = "public_dialogue";
        n.DomesticCrisis.ResponseUntilDay = 99;
        var json = JsonSerializer.Serialize(n, options);
        var restored = JsonSerializer.Deserialize<Nation>(json, options);
        Check((int)fields[0].GetValue(restored) == 0, "zero does not turn back into a missing target");
        Check(fields.Skip(1).All(f => (int)f.GetValue(restored) == -1), "omitted minus-one sentinels restore exactly");
        Check(restored.DomesticCrisis.ResponseId == "public_dialogue" && restored.DomesticCrisis.ResponseUntilDay == 99,
            "nested non-default institutional state survives elision");
        var fullOptions = new JsonSerializerOptions(options) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        Check(JsonNode.DeepEquals(JsonNode.Parse(JsonSerializer.Serialize(n, fullOptions)),
                JsonNode.Parse(JsonSerializer.Serialize(restored, fullOptions))),
            "all persisted nation fields survive a compact roundtrip");
        var defaults = new DiplomaticRelation();
        var compact = JsonSerializer.Serialize(defaults, options);
        Check(compact.Length < JsonSerializer.Serialize(defaults, fullOptions).Length,
            "an untouched bilateral relation becomes smaller without deleting live values");
        Check(JsonNode.DeepEquals(JsonNode.Parse(JsonSerializer.Serialize(defaults, fullOptions)),
                JsonNode.Parse(JsonSerializer.Serialize(JsonSerializer.Deserialize<DiplomaticRelation>(compact, options), fullOptions))),
            "every persisted relation field, not just its fingerprint, survives compact roundtrip");
    }

    static void StrictRows()
    {
        var sample = new NationHistorySample(7, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22);
        Check(JsonSerializer.Deserialize<NationHistorySample>(JsonSerializer.Serialize(sample)) == sample,
            "a complete history row remains lossless");
        foreach (var malformed in new[] { "[]", "[7,2,3]", "[7,2,3" })
        {
            try { JsonSerializer.Deserialize<NationHistorySample>(malformed); throw new Exception("accepted short history row"); }
            catch (JsonException) { Check(true, "truncated history cannot read uninitialised stack memory"); }
        }
        var legacy = JsonSerializer.Deserialize<NationHistorySample>("{\"Day\":7,\"Gdp\":2}");
        Check(legacy.Day == 7 && legacy.Gdp == 2 && legacy.Treasury == 0 && legacy.ArmsPriceIndex == 0,
            "omitted legacy object fields have deterministic zeros");
        try { JsonSerializer.Deserialize<PopulationGroup>("[0,0,\"culture\"]"); throw new Exception("accepted short population row"); }
        catch (JsonException) { Check(true, "a truncated people row is refused rather than fabricated"); }
        var broken = sample with { Treasury = float.PositiveInfinity };
        Check(NumericIntegrity.Inspect(new[] { broken }).Single().Contains("Treasury"),
            "numeric validation sees record properties in history");
        try { JsonSerializer.Serialize(broken); throw new Exception("ordinary JSON accepted infinity"); }
        catch (ArgumentException) { Check(true, "ordinary saves still reject named non-finite values"); }
        var diagnosticOptions = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        Check(JsonSerializer.Serialize(broken, diagnosticOptions).Contains("\"Infinity\""),
            "only an explicitly diagnostic row may represent the original infinity");
    }

    static void SaveProtection()
    {
        var c = Campaign.CreateBootstrap(61009);
        var s = c.States.First(r => r.NationMapIndex == c.Player.MapIndex);
        c.AttachProvinceTopology(ProvinceTopology.Build(Enumerable.Repeat((ushort)s.MapIndex, 512).ToArray(),
            Enumerable.Repeat((ushort)c.Player.MapIndex, 512).ToArray(), 32, 16, _ => false));
        c.Armies.Clear();
        var folder = Path.Combine(Path.GetTempPath(), "dof-integrity-" + Guid.NewGuid().ToString("N"));
        c.Defines.SaveTextFormat = true;
        SaveSystem.Save(folder, SaveSystem.AutoSlot, c, "tests");
        var path = Path.Combine(folder, SaveSystem.AutoSlot + ".bdfsave");
        var good = File.ReadAllBytes(path);
        var scenario = JsonNode.Parse(SaveSystem.ExportScenarioJson(c, "complete_schema"));
        Check(scenario["Nations"].AsArray().All(n => n["MapIndex"] is not null && n["Color"] is not null) &&
            scenario["States"].AsArray().All(r => r["NationMapIndex"] is not null),
            "scenario inspector receives a complete authoring schema even when saves are compact");
        c.Player.Gdp = float.NaN;
        string diagnostic = null;
        try { SaveSystem.Save(folder, SaveSystem.AutoSlot, c, "tests", autosave: true); throw new Exception("saved NaN"); }
        catch (CampaignIntegrityException error) { diagnostic = error.DiagnosticPath; }
        Check(c.IsPaused && File.ReadAllBytes(path).SequenceEqual(good), "invalid autosave pauses and leaves the last good file byte-for-byte unchanged");
        Check(File.Exists(diagnostic) && File.ReadAllText(diagnostic).Contains("NaN"), "original broken value is retained in a separate diagnostic");
        Check(SaveSystem.List(folder).Count == 1, "diagnostics never appear as playable campaign slots");
        var second = SaveSystem.WriteDiagnosticSnapshot(folder, c, "repeat test");
        Check(second != diagnostic && File.Exists(second) && File.Exists(diagnostic), "separate failures cannot overwrite each other's evidence");
        c.Player.Gdp = 1;
        c.History.Restore(new() { [c.Player.Id] = new() { new NationHistorySample(7, float.NaN, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0) } });
        try { SaveSystem.Save(folder, SaveSystem.AutoSlot, c, "tests"); throw new Exception("saved broken history"); }
        catch (CampaignIntegrityException error)
        {
            Check(error.Issue.Contains("History") && File.Exists(error.DiagnosticPath),
                "save protection includes historical record properties and preserves diagnostic rows");
        }
        Check(File.ReadAllBytes(path).SequenceEqual(good), "broken history also cannot replace the last good save");
        c.BlockSimulation("partially executed failed day");
        var before = c.ElapsedDays;
        c.IsPaused = false;
        c.TickDay();
        Check(c.ElapsedDays == before, "unpausing cannot resume a partially failed simulation step");
        Console.WriteLine("Integrity fixtures retained: " + folder);
    }

    static void CivilWarRules()
    {
        Check(CivilWarOutbreak.Chance(null, null) == 0f && CivilWarOutbreak.Break(null, null) is null,
            "an absent civil-war context is a safe refusal");
        var c = new Campaign(61010) { ScenarioId = "civil_integrity_fixture" };
        var parent = new Nation { Id = 0, MapIndex = 501, Tag = "CWA", Name = "Parent", Population = 7_000_000,
            Gdp = 70, Treasury = 70, ManpowerReserve = 700_000, PublicDebt = 35, DebtArrears = 7,
            PaymentArrears = 3.5f, DebtCondition = DebtCondition.Default, DebtMissedWeeks = 8,
            ProductionReferenceInitialized = true, ProductionReferenceGdp = 35, ProductionReferencePopulation = 7_000_000,
            Stability = .1f, Government = "presidential_republic", Ideology = "liberalism" };
        var observer = new Nation { Id = 1, MapIndex = 502, Tag = "CWB", Name = "Observer", Population = 1_000_000 };
        c.Nations.AddRange(new[] { parent, observer });
        var regions = new ushort[320]; var owners = new ushort[320];
        for (var i = 0; i < 8; i++)
        {
            var n = i < 7 ? parent : observer;
            var s = new ScenarioState { MapIndex = 3001 + i, Id = "civil_" + i, Name = "Region " + i,
                NationMapIndex = n.MapIndex, NationId = n.Tag, Population = 1_000_000,
                Stability = i == 1 ? .05f : .15f, Radicalism = .5f };
            s.PopulationGroups.Add(new PopulationGroup { Culture = "english", Size = s.Population });
            c.States.Add(s);
        }
        for (var y = 0; y < 4; y++)
        for (var x = 1; x < 79; x++)
        {
            var i = Math.Min(7, x / 10);
            regions[y * 80 + x] = (ushort)(3001 + i);
            owners[y * 80 + x] = (ushort)(i < 7 ? parent.MapIndex : observer.MapIndex);
        }
        c.AttachProvinceTopology(ProvinceTopology.Build(regions, owners, 80, 4, state => state == 0));
        c.ConfigureSinglePlayer(observer.Id);
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, 200L * 24);
        parent.CapitalStateMapIndex = 3001;
        Check(CivilWarOutbreak.EligibilityReason(c, parent) == "" && CivilWarOutbreak.Chance(c, parent) > 0,
            "the displayed civil-war warning uses the same eligibility gates as the outbreak");
        var fingerprint = CampaignFingerprint.Of(c);
        parent.Stability = float.NaN;
        Check(CivilWarOutbreak.Break(c, parent) is null && c.Nations.Count == 2,
            "non-finite stability cannot create a territorial opposition");
        parent.Stability = .1f;
        c.Defines.CivilWarRebelShareMax = .05f; c.Defines.CivilWarRebelShareMin = .01f;
        Check(CivilWarOutbreak.Break(c, parent) is null && c.Nations.Count == 2,
            "a single whole region cannot bypass the rebel population ceiling");
        c.Defines.CivilWarRebelShareMax = .4f; c.Defines.CivilWarRebelShareMin = .15f;
        Check(CampaignFingerprint.Of(c) == fingerprint, "refused outbreaks leave no hidden partial founding");
        var report = CivilWarOutbreak.Break(c, parent);
        var born = report?.Seceded;
        Check(born is not null && c.Wars.Any(w => !w.Ended && w.CivilConflict && w.Involves(parent.Id) && w.Involves(born.Id)),
            "a genuine civil outbreak opens a real war against its parent government");
        var ground = c.StatesOf(born).Select(s => s.MapIndex).ToArray();
        Check(ground.Length >= 2 && ground.Max() - ground.Min() + 1 == ground.Length &&
            c.States.First(s => s.MapIndex == 3001).NationMapIndex == parent.MapIndex,
            "armed opposition receives connected ground while the capital remains with the government");
        Check(born.Population <= 7_000_000 * .4 && parent.Population + born.Population == 7_000_000,
            $"the outbreak obeys its population ceiling without duplicating residents (parent={parent.Population}, rebels={born.Population})");
        Check(Math.Abs(parent.Treasury + born.Treasury - 70) < .001 &&
            Math.Abs(parent.Gdp + born.Gdp - 70) < .001 &&
            Math.Abs(parent.ManpowerReserve + born.ManpowerReserve - 700_000) < 1,
            "armed secession divides cash, GDP and reservists instead of copying the parent's accounts");
        Check(Math.Abs(parent.PublicDebt + born.PublicDebt - 35) < .001 &&
            Math.Abs(parent.DebtArrears + born.DebtArrears - 7) < .001 &&
            Math.Abs(parent.PaymentArrears + born.PaymentArrears - 3.5) < .001 &&
            born.DebtCondition == DebtCondition.Default && born.DebtMissedWeeks == 8,
            "a civil war neither creates debt nor erases inherited default and arrears");
        Check(Math.Abs(ProductionCapacity.Of(parent) + ProductionCapacity.Of(born) - 35) < .001,
            "splitting a country conserves physical capacity rather than rebasing it from nominal GDP");
        Check(CivilWarOutbreak.EligibilityReason(c, parent).Length > 0 && CivilWarOutbreak.Break(c, parent) is null,
            "recovery and an existing civil war prevent repeated fragmentation");
    }
}
