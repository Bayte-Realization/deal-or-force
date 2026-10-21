using System;
using System.Linq;
using DealOrForce.Simulation;

static class CatalogIdentityTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("catalogue identity: " + note); Console.WriteLine("PASS catalogue identity: " + note); }

    public static void Run()
    {
        var visibleBefore = FormableNations.All.ToArray();
        try
        {
            using var overlay = System.Text.Json.JsonDocument.Parse(
                "{\"indochinese_federation\":{\"alias_of\":\"visegrad_union\"}}");
            FormableNations.Merge(overlay.RootElement, null);
            Check(!FormableNations.All.Any(f => f.Id == "indochinese_federation") &&
                visibleBefore.Any(f => f.Id == "indochinese_federation"),
                "a mod overlay invalidates the read-only visible catalogue cache");
        }
        finally { FormableNations.ResetCatalog(); }
        Check(FormableNations.All.Any(f => f.Id == "indochinese_federation"),
            "resetting the catalogue restores shipped identities after an overlay");
        foreach (var pair in new[] {("indochina_federation","indochinese_federation"),
                                    ("visegrad_commonwealth","visegrad_union")})
        {
            var legacy = FormableNations.Find(pair.Item1); var canonical = FormableNations.Find(pair.Item2);
            Check(legacy?.AliasOf == pair.Item2 && canonical is { AliasOf.Length: 0 },
                pair.Item1 + " remains readable as a legacy identity with a live canonical target");
            Check(!FormableNations.All.Any(f=>f.Id==legacy.Id) && FormableNations.All.Any(f=>f.Id==canonical.Id),
                "new catalogues contain only one version of " + canonical.Name);
            var n = new Nation { Tag = "VNM", FormedNationId = "" };
            Check(!FormableNations.CompatibleProclamation(n,legacy),
                "a legacy duplicate cannot award a second proclamation bonus");
        }
        Check(FormableNations.Find("intermarium").RequiredTags.OrderBy(x=>x)
                .SequenceEqual(new[]{"POL","UKR","LTU","BLR","LVA","EST"}.OrderBy(x=>x)),
            "Intermarium requires its Baltic-to-Black-Sea territory rather than duplicating the Commonwealth");
        Check(!FormableNations.Find("intermarium").RequiredTags.OrderBy(x=>x)
                .SequenceEqual(FormableNations.Find("polish_lithuanian").RequiredTags.OrderBy(x=>x)),
            "the distinct Commonwealth restoration remains available");

        var c = Campaign.CreateBootstrap(20261018);
        var originalLanguage = Loc.Language;
        try
        {
            foreach (var tag in new[] { "ABZ", "CCC", "KSF", "PFS" })
            foreach (var language in new[] { "english", "russian" })
            {
                Loc.Language = language;
                // Landless movements are founded after bootstrap, not necessarily here.
                var nation = c.NationByTag(tag) ?? new Nation { Tag = tag, Name = tag };
                var key = "country." + tag + ".name";
                var translated = Loc.Get(key);
                var name = nation.Name;
                try
                {
                    nation.Name = "A mod renamed the English source";
                    Check(translated != key && CountryNames.Resolve(nation, false) == translated,
                        tag + " keeps its " + language + " name after the source name is edited");
                }
                finally { nation.Name = name; }
            }
        }
        finally { Loc.Language = originalLanguage; }
        foreach (var id in new[]{"arakan_army","southern_transition_council"})
        {
            var d = MovementCatalog.Of(id);
            var f = Factions.All.Single(f=>f.Id==d.TerritorialFaction);
            var actor = c.AddNation(new Nation { Tag=f.Tag, FactionId=f.Id, MapIndex=60000+c.Nations.Count });
            var region = c.States.Single(s=>s.Id==d.Headquarters);
            region.NationMapIndex=actor.MapIndex; region.NationId=actor.Tag;
        }
        c.InvalidateStateIndex(); Movements.Found(c);
        Check(c.NationByTag(MovementCatalog.Of("arakan_army").Tag)?.Movement is null &&
            c.NationByTag(MovementCatalog.Of("southern_transition_council").Tag)?.Movement is null,
            "actual administrations do not also found duplicate movement headquarters");
        Check(MovementCatalog.Of("arakan_army") is not null &&
            MovementCatalog.Of("southern_transition_council") is not null,
            "legacy movement definitions survive for landless scenarios and save migration");
        foreach (var id in new[]{"railway_timetable_recovery","emergency_maintenance_reserve"})
        {
            var e=EventCatalog.All.Single(e=>e.Id==id);
            Check(e.Options.Length==3 && e.Options.Select(o=>o.Title).Distinct().Count()==3,
                id+" replaces repetitive catalogue entries with three different recovery choices");
            var lang=Loc.Language;
            try { Loc.Language="russian"; Check(Loc.Get(e.Title)!=e.Title &&
                e.Options.All(o=>Loc.Get(o.Title)!=o.Title && Loc.Get(o.Consequence)!=o.Consequence),
                id+" has Russian titles, choices and consequences"); }
            finally { Loc.Language=lang; }
        }
    }
}

static class ObserverAuthorityTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("observer authority: " + note); Console.WriteLine("PASS observer authority: " + note); }

    public static void Run()
    {
        var c=Campaign.CreateBootstrap(20261018);
        var s=c.StatesOf(c.Player).First();
        c.AttachProvinceTopology(ProvinceTopology.Build(Enumerable.Repeat((ushort)s.MapIndex,512).ToArray(),
            Enumerable.Repeat((ushort)c.Player.MapIndex,512).ToArray(),32,16,_=>false));
        c.Armies.Clear();
        c.HumanSeats.Clear(); foreach(var n in c.Nations) n.IsHuman=false;
        var hash=CampaignFingerprint.Of(c);
        var saved=SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c,"observer"),"observer");
        Check(saved.Success,"an all-AI observer snapshot loads");
        var restored=saved.Campaign;
        restored.AttachProvinceTopology(c.ProvinceMap.CloneForOverrides());
        Check(restored.HumanSeats.Count==0 && restored.Nations.All(n=>!n.IsHuman),
            "loading never silently takes the observed country away from its AI");
        Check(restored.Player?.Tag==c.Player.Tag,"the observer's focus country remains selected without becoming a human seat");
        Check(CampaignFingerprint.Of(restored)==hash,"observer authority survives the snapshot with identical authoritative state");
        restored.ConfigureSinglePlayer(restored.Player.Id);
        var player=SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(restored,"observer"),"observer");
        Check(player.Success && player.Campaign.HumanSeats.Count==1 && player.Campaign.Player.IsHuman,
            "returning to a country still saves and restores a normal human seat");
    }
}

