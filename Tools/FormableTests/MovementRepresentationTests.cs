using System;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class MovementRepresentationTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("movement representation: " + label); Console.WriteLine("PASS movement representation: " + label); }

    static Campaign Legacy()
    {
        var c = Campaign.CreateBootstrap(20261017);
        Movements.Found(c);
        return c;
    }

    static Nation Administration(Campaign c)
    {
        var actor = c.AddNation(new Nation { Tag = "HTH", Iso = "HTH", Name = "Ansar Allah",
            FactionId = "ansar_allah", MapIndex = 60000, Treasury = .37f });
        var region = Movements.StateById(c, "ye_sn");
        region.NationId = actor.Tag;
        region.NationMapIndex = actor.MapIndex;
        c.InvalidateStateIndex();
        return actor;
    }

    static Campaign SnapshotWorld()
    {
        // The real snapshot codec needs a complete province-control layer. Keep this
        // fixture small and independent of the bookmark's deployments and economy.
        var c = new Campaign(20261017) { ScenarioId = "movement_representation_fixture" };
        c.AddNation(new Nation { Tag = "YEM", MapIndex = 501, Population = 2_000_000, Gdp = 10, Treasury = 1, OutputScale = 1 });
        c.AddNation(new Nation { Tag = "HTH", FactionId = "ansar_allah", MapIndex = 502,
            Population = 1_000_000, Gdp = 5, Treasury = .37f, OutputScale = 1 });
        c.AddNation(new Nation { Tag = "HOU", MapIndex = 503, Gdp = .01f, Treasury = .1f, OutputScale = 1,
            Movement = new MovementState { Id = "ansar_allah", HeadquartersStateMapIndex = 3001, Cadres = 35000 } });
        c.AddNation(new Nation { Tag = "HMS", MapIndex = 504, Gdp = .01f, Treasury = .6f, OutputScale = 1,
            Movement = new MovementState { Id = "hamas", HeadquartersStateMapIndex = 3002, Cadres = 30000 } });
        for (var i = 0; i < 3; i++)
            c.States.Add(new ScenarioState { Id = i == 0 ? "ye_sn" : i == 1 ? "ye_sd" : "qa_ra",
                MapIndex = 3001 + i, NationMapIndex = i == 0 ? 502 : 501,
                NationId = i == 0 ? "HTH" : "YEM", Population = 1_000_000 });
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, 7L * 24);
        var cells = Enumerable.Range(0, 60).Select(i => (ushort)(3001 + i % 15 / 5)).ToArray();
        var owners = Enumerable.Range(0, 60).Select(i => (ushort)(i % 15 < 5 ? 502 : 501)).ToArray();
        c.AttachProvinceTopology(ProvinceTopology.Build(cells, owners, 15, 4, _ => false));
        c.ConfigureSinglePlayer(0);
        SovereignFinance.NormalizeAccounts(c);
        return c;
    }

    public static void Run()
    {
        var definition = MovementCatalog.Of("ansar_allah");
        Check(definition?.Tag == "HOU" && definition.TerritorialFaction == "ansar_allah",
            "the legacy movement tag is retained and explicitly linked to its territorial faction");
        Check(Factions.All.Any(f => f.Id == definition.TerritorialFaction && f.Tag == "HTH"),
            "the link resolves to the existing HTH administration, not Yemen's recognised government");

        var fresh = Campaign.CreateBootstrap(20261017);
        var actor = Administration(fresh);
        var before = CampaignFingerprint.Of(fresh);
        Check(Movements.TerritorialActor(fresh, definition) == actor && before == CampaignFingerprint.Of(fresh),
            "looking up a territorial representative is read-only");
        Movements.Found(fresh);
        Check(fresh.NationByTag("HOU") is null, "a fresh world does not found a second Ansar Allah headquarters");
        Movements.Activate(fresh, definition);
        Check(fresh.NationByTag("HOU") is null, "explicit activation cannot bypass the territorial representation");
        Check(fresh.NationByTag("HMS")?.Movement is { Dissolved: false },
            "other movements still start normally");

        Check(Legacy().NationByTag("HOU")?.Movement is { Dissolved: false },
            "a scenario without the territorial faction can still found the movement");
        var c = SnapshotWorld();
        var duplicate = c.NationByTag("HOU");
        actor = c.NationByTag("HTH");
        var other = c.NationByTag("HMS");
        var originalActorMoney = actor.Treasury;
        var actorLand = c.StatesOf(actor).Select(s => s.MapIndex).ToArray();
        var nationCount = c.Nations.Count;
        var fingerprint = CampaignFingerprint.Of(c);
        var peer = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "representation"), "representation").Campaign;
        Check(peer is not null, "the legacy-world fixture uses the real snapshot codec");
        peer.AttachProvinceTopology(c.ProvinceMap.CloneForOverrides());
        Check(CampaignFingerprint.Of(peer) == fingerprint && !peer.NationByTag("HOU").Movement.Dissolved,
            "snapshot import preserves the old world exactly until simulation advances");
        Check(Movements.RetireDuplicateRepresentations(c) == 1, "the old unused landless duplicate is retired");
        Check(Movements.RetireDuplicateRepresentations(peer) == 1 && CampaignFingerprint.Of(peer) == CampaignFingerprint.Of(c),
            "both peers retire the duplicate identically without a load-time migration");
        Check(duplicate.Movement.Dissolved && duplicate.Movement.HeadquartersStateMapIndex == -1 &&
            !Movements.Active(c).Contains(duplicate), "the duplicate leaves the active map and movement AI");
        Check(c.Nations.Count == nationCount && c.NationById(duplicate.Id) == duplicate,
            "the historical nation id is not removed or reassigned");
        Check(actor.Treasury == originalActorMoney &&
            c.StatesOf(actor).Select(s => s.MapIndex).SequenceEqual(actorLand) &&
            !other.Movement.Dissolved, "territory, territorial treasury and other movements are unchanged");
        fingerprint = CampaignFingerprint.Of(c);
        Check(Movements.RetireDuplicateRepresentations(c) == 0 && CampaignFingerprint.Of(c) == fingerprint,
            "repeated reconciliation does not rewrite the campaign");
        Movements.Activate(c, definition);
        Check(duplicate.Movement.Dissolved, "a retired duplicate is not reactivated beside its administration");
        peer = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "representation"), "representation").Campaign;
        peer.AttachProvinceTopology(c.ProvinceMap.CloneForOverrides());
        Check(CampaignFingerprint.Of(peer) == CampaignFingerprint.Of(c) && peer.NationByTag("HOU").Movement.Dissolved,
            "retirement survives save-payload roundtrip");

        var guards = new (string Label, Action<Campaign, Nation> Apply)[]
        {
            ("a human player", (_, n) => n.IsHuman = true),
            ("a selected player", (_, n) => n.IsPlayer = true),
            ("a human seat", (w, n) => w.HumanSeats.Add(n.Id)),
            ("a government in exile", (_, n) => n.Movement.Exile = true),
            ("landed movement status", (_, n) => n.Movement.Landless = false),
            ("actual territory despite stale landless status", (w, n) => {
                var r = Movements.StateById(w, "ye_sd"); r.NationId = n.Tag; r.NationMapIndex = n.MapIndex; w.InvalidateStateIndex(); }),
            ("an army", (w, n) => w.Armies.Add(new Army { NationId = n.Id })),
            ("a fleet", (w, n) => w.Fleets.Add(new Fleet { NationId = n.Id })),
            ("a support formation", (w, n) => w.SupportFormations.Add(new SupportFormation { NationId = n.Id })),
            ("a recruitment commitment", (w, n) => w.Recruitment.Add(new RecruitmentOrder { NationId = n.Id })),
            ("a support order", (w, n) => w.SupportOrders.Add(new SupportOrder { NationId = n.Id })),
            ("its own ongoing war", (w, n) => w.Wars.Add(new StrategicWar { AttackerId = n.Id, DefenderId = w.NationByTag("YEM").Id }))
        };
        foreach (var (label, apply) in guards)
        {
            var world = Legacy(); Administration(world); var n = world.NationByTag("HOU");
            apply(world, n);
            fingerprint = CampaignFingerprint.Of(world);
            Check(Movements.RetireDuplicateRepresentations(world) == 0 &&
                !n.Movement.Dissolved && CampaignFingerprint.Of(world) == fingerprint, label + " is never silently erased");
        }

        var fallen = Legacy();
        var former = Administration(fallen);
        former.FactionId = "different_faction";
        Check(Movements.RetireDuplicateRepresentations(fallen) == 0,
            "a similar name is not an identity link");
        former.FactionId = "ansar_allah";
        var capital = Movements.StateById(fallen, "ye_sn");
        capital.NationId = "YEM"; capital.NationMapIndex = fallen.NationByTag("YEM").MapIndex;
        fallen.InvalidateStateIndex();
        Check(Movements.RetireDuplicateRepresentations(fallen) == 0,
            "a faction without territory does not suppress a landless movement");

        try
        {
            using var overlay = JsonDocument.Parse("{\"ansar_allah\":{\"territorial_faction\":\"\"}}");
            MovementCatalog.Merge(overlay.RootElement, null);
            Check(MovementCatalog.Of("ansar_allah").TerritorialFaction.Length == 0,
                "a mod can explicitly remove the identity link with an empty string");
            var world = Campaign.CreateBootstrap(20261017); Administration(world); Movements.Found(world);
            Check(world.NationByTag("HOU")?.Movement is { Dissolved: false },
                "the mod opt-out permits separate actors");
        }
        finally { MovementCatalog.ResetCatalog(); }

        var ticking = Legacy(); Administration(ticking);
        ticking.IsPaused = false; ticking.TickDay();
        Check(ticking.NationByTag("HOU").Movement.Dissolved,
            "an existing campaign retires the unused duplicate on its first advancing day");
    }
}
