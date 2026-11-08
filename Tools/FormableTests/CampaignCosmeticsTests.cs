using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using DealOrForce.Simulation;

internal static class CampaignCosmeticsTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("campaign cosmetics: " + label);
        Console.WriteLine("PASS campaign cosmetics: " + label);
    }

    private static FlagDesign Design(string colour = "#ce1126") => new()
    {
        Id = "flag_editor_draft", Ratio = 1.5f, Pattern = "horizontal",
        Colours = new List<string> { colour, "#ffffff", "#0038a8" },
        Emblems = new List<FlagEmblem> { new() { Shape = "star", Colour = "#fcd116", Size = .3f } }
    };

    private static Campaign World()
    {
        var c = new Campaign(20261108, new Defines());
        for (var i = 0; i < 3; i++)
        {
            c.AddNation(new Nation { Tag = "COS" + i, Name = "Country " + i, MapIndex = 501 + i,
                Population = 1_000_000, Gdp = 100, Treasury = 20, Color = i == 0 ? "#123456" : "#654321",
                CapitalStateMapIndex = 1001 + i, Stability = .7f, WarSupport = .7f });
            c.States.Add(new ScenarioState { Id = "cos_" + i, Name = "Region " + i, MapIndex = 1001 + i,
                NationMapIndex = 501 + i, NationId = "COS" + i, Population = 1_000_000 });
        }
        c.InvalidateStateIndex();
        c.AttachProvinceTopology(ProvinceTopology.Build(new ushort[] { 1001, 1002, 1003, 1001, 1002, 1003 },
            new ushort[] { 501, 502, 503, 501, 502, 503 }, 3, 2, _ => false));
        c.ConfigureSinglePlayer(c.Nations[0].Id);
        return c;
    }

    public static void Run()
    {
        var c = World();
        var n = c.Player;
        var initial = CampaignFingerprint.Of(c);
        n.CustomFlagDesignJson = null; n.MapColorBeforeOverride = null;
        Check(CampaignFingerprint.Of(c) == initial, "missing optional fields retain empty-field fingerprint semantics");
        n.CustomFlagDesignJson = ""; n.MapColorBeforeOverride = "";
        Check(CampaignFingerprint.Of(c) == initial, "explicit defaults emit no optional cosmetic state");

        var designs = FlagDesignCatalog.All.ToDictionary(d => d.Id, d => d.ToJson(), StringComparer.Ordinal);
        var notifications = 0;
        void Changed() => notifications++;
        FlagDesignCatalog.Changed += Changed;
        try
        {
            var isolated = World();
            var isolatedHash = CampaignFingerprint.Of(isolated);
            var order = new SetNationalCosmeticsCommand { IssuerId = n.Id, CustomName = "Моя республика 🌟",
                CustomFlag = "flag_modded_banner", CustomFlagDesignJson = Design().ToDocument(), MapColor = "#A3c" };
            var rng = c.Random.State;
            order.Apply(c);
            Check(order.Refusal == "" && n.CustomName == "Моя республика 🌟" && n.CustomFlag == "flag_modded_banner",
                "own country accepts a printable name and a safe mod sprite ID");
            Check(n.Color == "#aa33cc" && n.MapColorBeforeOverride == "#123456",
                "map colour normalises and preserves the first original colour");
            Check(n.CustomFlagDesignJson.StartsWith("{") && !n.CustomFlagDesignJson.Contains("//") &&
                CampaignCosmetics.ReadFlagDesign(n)?.Id == "flag_campaign_cosmetic_" + n.Id,
                "commented editor document is stored as one canonical campaign-local design");
            Check(CampaignFingerprint.Of(c) != initial && c.Random.State == rng,
                "applying cosmetics affects the fingerprint without spending campaign RNG");
            Check(CampaignFingerprint.Of(isolated) == isolatedHash && isolated.Player.CustomFlagDesignJson == "" &&
                isolated.Player.Color == "#123456", "another campaign is not changed by editing this one");

            var hash = CampaignFingerprint.Of(c);
            var revision = c.MapRevision;
            order.Apply(c);
            Check(CampaignFingerprint.Of(c) == hash && c.MapRevision == revision,
                "repeating the same canonical edit does not dirty the map again");
            var read = CampaignCosmetics.ReadFlagDesign(n);
            read.Colours[0] = "#000000";
            Check(CampaignCosmetics.ReadFlagDesign(n).Colours[0] == "#ce1126" &&
                CampaignFingerprint.Of(c) == hash && c.Random.State == rng,
                "reading or editing a returned draft never mutates saved state");

            new SetNationalCosmeticsCommand { IssuerId = n.Id, CustomName = n.CustomName, CustomFlag = n.CustomFlag,
                MapColor = "#abcdef" }.Apply(c);
            Check(n.Color == "#abcdef" && n.MapColorBeforeOverride == "#123456",
                "subsequent colours retain the first original backup");
            var savedDocument = n.CustomFlagDesignJson;
            new SetNationalCosmeticsCommand { IssuerId = n.Id }.Apply(c);
            Check(n.CustomName == "" && n.CustomFlag == "" && n.CustomFlagDesignJson == savedDocument && n.Color == "#abcdef",
                "legacy name/sprite reset preserves new fields omitted from the command");
            new SetNationalCosmeticsCommand { IssuerId = n.Id, CustomFlagDesignJson = "", MapColor = "" }.Apply(c);
            Check(n.Color == "#123456" && n.MapColorBeforeOverride == "" && n.CustomFlagDesignJson == "" &&
                CampaignFingerprint.Of(c) == initial, "explicit full reset restores the original identity and fingerprint");
            Check(notifications == 0 && FlagDesignCatalog.All.Count == designs.Count &&
                FlagDesignCatalog.All.All(d => designs.TryGetValue(d.Id, out var original) && original == d.ToJson()),
                "neither commands nor previews register or replace global flag designs");
        }
        finally { FlagDesignCatalog.Changed -= Changed; }

        Permissions();
        InvalidPackages();
        FingerprintFields();
        WireAndSnapshot();
    }

    private static void Permissions()
    {
        var c = World(); var issuer = c.Player; var subject = c.Nations[1]; var foreign = c.Nations[2];
        subject.OverlordId = issuer.Id; subject.SubjectType = "puppet";
        var command = new SetNationalCosmeticsCommand { IssuerId = issuer.Id, TargetNationId = subject.Id, CustomName = "Direct subject" };
        command.Apply(c);
        Check(command.Refusal == "" && subject.CustomName == "Direct subject", "a direct subject can be customised");
        foreign.OverlordId = subject.Id; foreign.SubjectType = "puppet";
        var before = CampaignFingerprint.Of(c);
        command = new SetNationalCosmeticsCommand { IssuerId = issuer.Id, TargetNationId = foreign.Id, CustomName = "Indirect subject" };
        command.Apply(c);
        Check(command.Refusal.Length > 0 && CampaignFingerprint.Of(c) == before, "an indirect subject is not silently owned");
        foreign.SubjectType = ""; foreign.Movement = new MovementState { HeadquartersStateMapIndex = c.States[0].MapIndex };
        foreign.Movement.SponsoredBy[issuer.Id] = 5;
        before = CampaignFingerprint.Of(c);
        command = new SetNationalCosmeticsCommand { IssuerId = issuer.Id, TargetNationId = foreign.Id, CustomName = "Foreign movement" };
        command.Apply(c);
        Check(command.Refusal.Length > 0 && CampaignFingerprint.Of(c) == before,
            "hosting or sponsoring a foreign movement does not grant identity-edit authority");
        issuer.Movement = new MovementState();
        command = new SetNationalCosmeticsCommand { IssuerId = issuer.Id, CustomName = "Our movement" };
        command.Apply(c);
        Check(command.Refusal == "" && issuer.CustomName == "Our movement", "a movement can still edit its own identity");
        Check(!CampaignCosmetics.CanEdit(c, issuer, new Nation { Id = foreign.Id }),
            "an object outside the campaign is not authorised by an ID collision");
    }

    private static void InvalidPackages()
    {
        void Refuses(string label, string name = "Should not change", string sprite = "flag_safe",
            string document = null, string colour = "#abc", Action<Nation> prepare = null)
        {
            var c = World(); var n = c.Player;
            prepare?.Invoke(n);
            var before = CampaignFingerprint.Of(c); var revision = c.MapRevision; var rng = c.Random.State;
            var original = (n.CustomName, n.CustomFlag, n.CustomFlagDesignJson, n.Color, n.MapColorBeforeOverride);
            var order = new SetNationalCosmeticsCommand { IssuerId = n.Id, CustomName = name, CustomFlag = sprite,
                CustomFlagDesignJson = document, MapColor = colour };
            order.Apply(c);
            Check(order.Refusal.Length > 0 && CampaignFingerprint.Of(c) == before && c.MapRevision == revision &&
                c.Random.State == rng && original == (n.CustomName, n.CustomFlag, n.CustomFlagDesignJson, n.Color, n.MapColorBeforeOverride),
                "invalid package is atomic: " + label);
        }
        Refuses("overlong name", name: new string('a', 97));
        Refuses("control in name", name: "Country\nOther");
        Refuses("bidi-format control", name: "Country\u202eOther");
        Refuses("invalid surrogate", name: "Country\ud800");
        Refuses("sprite file traversal", sprite: "../flag_private");
        Refuses("sprite URI", sprite: "res://game/gfx/flags/flag_pol.png");
        Refuses("sprite ID too long", sprite: new string('x', 97));
        Refuses("invalid map colour after valid name/design", document: Design().ToJson(), colour: "red");
        Refuses("map alpha is not RGB", colour: "#123456ff");
        Refuses("invalid original colour", prepare: n => n.Color = "broken");
        Refuses("invalid restoration backup", colour: "", prepare: n => n.MapColorBeforeOverride = "broken");
        Refuses("malformed JSON", document: "{");
        Refuses("missing flag envelope", document: "{}");
        Refuses("two designs", document: "{\"flag_designs\":{\"flag_a\":{},\"flag_b\":{}}}");
        Refuses("no background", document: "{\"flag_designs\":{\"flag_a\":{}}}");
        Refuses("duplicate fields", document: "{\"flag_designs\":{},\"flag_designs\":{}}");
        Refuses("invalid ratio", document: Design().ToJson().Replace("1.5", "99"));
        Refuses("unknown pattern", document: Design().ToJson().Replace("horizontal", "unknown_pattern"));
        Refuses("invalid colour", document: Design().ToJson().Replace("#ce1126", "wrong"));
        Refuses("unknown emblem", document: Design().ToJson().Replace("\"star\"", "\"unknown_shape\""));
        var spriteDesign = Design(); spriteDesign.Emblems[0].Sprite = "../private";
        Refuses("emblem sprite path", document: spriteDesign.ToJson());
        var crowded = Design();
        crowded.Emblems = Enumerable.Range(0, CampaignCosmetics.MaximumEmblems + 1).Select(_ => new FlagEmblem()).ToList();
        Refuses("emblem limit", document: crowded.ToJson());
        Refuses("UTF8 byte limit", document: "/*" + new string('Я', 32768) + "*/" + Design().ToJson());
        Refuses("text size limit", document: new string(' ', CampaignCosmetics.MaximumFlagDesignBytes + 1));
        var tinyWeights = Design(); tinyWeights.Weights = new List<float> { .000001f, 1, 1 };
        var tinyJson = tinyWeights.ToJson().Replace("0,", "0.000001,", StringComparison.Ordinal);
        Refuses("weights that canonicalise to zero", document: tinyJson);

        Check(CampaignCosmetics.IsPrintableName(string.Concat(Enumerable.Repeat("🌟", 96))) &&
            !CampaignCosmetics.IsPrintableName(string.Concat(Enumerable.Repeat("🌟", 97))),
            "name limits count printable Unicode scalars rather than splitting surrogate pairs");
        var full = Design(); full.Emblems = Enumerable.Range(0, CampaignCosmetics.MaximumEmblems).Select(_ => new FlagEmblem()).ToList();
        Check(CampaignCosmetics.TryNormaliseFlagDesign(full.ToJson(), 0, out var canonical, out _) &&
            CampaignCosmetics.TryNormaliseFlagDesign(canonical, 0, out var again, out _) && canonical == again,
            "the maximum supported emblem count normalises idempotently");
    }

    private static void FingerprintFields()
    {
        var c = World(); var n = c.Player; var before = CampaignFingerprint.Of(c);
        n.CustomFlagDesignJson = Design().ToJson();
        Check(CampaignFingerprint.Of(c) != before, "the custom flag document participates in the fingerprint");
        var first = CampaignFingerprint.Of(c); n.CustomFlagDesignJson = Design("#000000").ToJson();
        Check(CampaignFingerprint.Of(c) != first, "different document content changes the fingerprint");
        n.CustomFlagDesignJson = ""; n.MapColorBeforeOverride = n.Color;
        first = CampaignFingerprint.Of(c); n.Color = "#abcdef";
        Check(CampaignFingerprint.Of(c) != first, "the current active override colour participates in the fingerprint");
        first = CampaignFingerprint.Of(c); n.MapColorBeforeOverride = "#111111";
        Check(CampaignFingerprint.Of(c) != first, "the colour restoration backup participates in the fingerprint");
        var old = JsonSerializer.Deserialize<Nation>("{\"Id\":0,\"Tag\":\"OLD\"}", new JsonSerializerOptions { IncludeFields = true });
        Check(old.CustomFlagDesignJson == "" && old.MapColorBeforeOverride == "", "old nation data defaults both additive fields to empty");
        var visual = FlagCatalog.VisualStateHash(c);
        n.CustomFlagDesignJson = Design().ToJson();
        Check(FlagCatalog.VisualStateHash(c) != visual, "custom flag design invalidates retained military/map flag presentation");
    }

    private static void WireAndSnapshot()
    {
        var command = new SetNationalCosmeticsCommand { IssuerId = 4, TargetNationId = 5,
            CustomName = "Wire name", CustomFlag = "flag_wire", CustomFlagDesignJson = Design().ToJson(), MapColor = "#abc" };
        Check(NetworkCommandCodec.TryEncode(command, out var type, out var json) &&
            NetworkCommandCodec.TryDecode(type, json, out var decoded) && decoded is SetNationalCosmeticsCommand wire &&
            wire.CustomName == command.CustomName && wire.CustomFlag == command.CustomFlag &&
            wire.CustomFlagDesignJson == command.CustomFlagDesignJson && wire.MapColor == "#abc",
            "the existing command codec preserves the complete cosmetic edit");
        Check(NetworkCommandCodec.TryDecode(nameof(SetNationalCosmeticsCommand),
            "{\"IssuerId\":0,\"CustomName\":\"Old command\",\"CustomFlag\":\"\"}", out var legacy) &&
            legacy is SetNationalCosmeticsCommand old && old.CustomFlagDesignJson is null && old.MapColor is null,
            "old recorded commands preserve newly added cosmetic fields");
        command = new SetNationalCosmeticsCommand { IssuerId = 0, CustomFlagDesignJson = "", MapColor = "" };
        Check(NetworkCommandCodec.TryEncode(command, out type, out json) && NetworkCommandCodec.TryDecode(type, json, out decoded) &&
            decoded is SetNationalCosmeticsCommand reset && reset.CustomFlagDesignJson == "" && reset.MapColor == "",
            "the codec distinguishes explicit resets from omitted new fields");

        var c = Campaign.CreateBootstrap(20261108);
        MapRasterCatalog.ResetCatalog();
        Check(MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
            WorldMapSource.Width, WorldMapSource.Height, out var topology, out var message), "authored topology loads: " + message);
        c.AttachProvinceTopology(topology);
        var n = c.NationByTag("POL"); c.ConfigureSinglePlayer(n.Id);
        SovereignFinance.NormalizeAccounts(c); EconomySystem.Explain(c);
        new SetNationalCosmeticsCommand { IssuerId = n.Id, CustomName = "Saved republic", CustomFlagDesignJson = Design().ToDocument(),
            MapColor = "#abcdef" }.Apply(c);
        var before = CampaignFingerprint.Of(c);
        var saved = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "campaign-cosmetics"), "campaign-cosmetics");
        Check(saved.Success, "campaign-local identity survives real-map snapshot: " + saved.Warning);
        saved.Campaign.AttachProvinceTopology(c.ProvinceMap.CloneForOverrides());
        var restored = saved.Campaign.NationById(n.Id);
        Check(restored.CustomName == n.CustomName && restored.CustomFlagDesignJson == n.CustomFlagDesignJson &&
            restored.Color == n.Color && restored.MapColorBeforeOverride == n.MapColorBeforeOverride &&
            CampaignFingerprint.Of(saved.Campaign) == before, "snapshot restores exact identity fields and authoritative fingerprint");
        var originalColour = restored.MapColorBeforeOverride;
        new SetNationalCosmeticsCommand { IssuerId = restored.Id, CustomFlagDesignJson = "", MapColor = "" }.Apply(saved.Campaign);
        Check(restored.Color == originalColour && restored.MapColorBeforeOverride == "" && restored.CustomFlagDesignJson == "",
            "colour and flag reset still work after snapshot restore");
    }
}
