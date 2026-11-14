using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Who may go into exile, and how an exile ends (MovementFall). A player's world ten years in
/// held 40 exiles among 86 movements: militias, pieces a war had carved out of a region, the
/// same exile twice, raw "rising:..." ids, and exiles at legitimacy 0.00-0.07 still standing
/// years later. Each of those is pinned here against the real fall and the real weekly review.
/// </summary>
static class ExileOriginTests
{
    static void Check(bool condition, string label)
    { if (!condition) throw new Exception("exile origin: " + label); Console.WriteLine("PASS exile origin: " + label); }

    // A country with no ground left (its remembered homeland is region 3001, held now by the
    // holder), the holder, and a third country that would take its government in.
    static (Campaign c, Nation fallen, Nation holder, Nation host) Fixture()
    {
        var c = new Campaign(4242) { ScenarioId = "exile_origin_fixture" };
        var fallen = new Nation { Id = 0, Tag = "QXA", Name = "Oldland", MapIndex = 501, Population = 0, Gdp = .05f,
            Treasury = 1f, Stability = .6f, Government = "parliamentary_republic", Ideology = "liberalism", Color = "#123456" };
        var holder = new Nation { Id = 1, Tag = "QXB", Name = "Holder", MapIndex = 502, Population = 1_000_000,
            Gdp = 1000f, Treasury = 100f, Stability = .8f, Government = "presidential_republic", Ideology = "nationalism" };
        var host = new Nation { Id = 2, Tag = "QXC", Name = "Host", MapIndex = 503, Population = 500_000,
            Gdp = 100f, Treasury = 10f, Stability = .8f, Government = "parliamentary_republic", Ideology = "liberalism" };
        c.Nations.Add(fallen); c.Nations.Add(holder); c.Nations.Add(host);
        foreach (var (index, owner, id) in new[] { (3001, 502, "qx_home"), (3002, 502, "qx_far"), (3003, 503, "qx_refuge") })
        {
            // Not settled under its new rulers: the generation-long forgetting is another rule.
            var region = new ScenarioState { MapIndex = index, NationMapIndex = owner, Id = id, Name = id, Population = 500_000, BorderHabit = 0f };
            region.PopulationGroups.Add(new PopulationGroup { Culture = "russian", Religion = "orthodox", Size = 500_000 });
            c.States.Add(region);
        }
        const int w = 192, h = 64;
        var states = new ushort[w * h]; var owners = new ushort[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                states[y * w + x] = (ushort)(x < 64 ? 3001 : x < 128 ? 3002 : 3003);
                owners[y * w + x] = (ushort)(x < 128 ? 502 : 503);
            }
        c.AttachProvinceTopology(ProvinceTopology.Build(states, owners, w, h, _ => false));
        c.Homelands[fallen.Id] = new List<int> { 3001 };
        c.ConfigureSinglePlayer(holder.Id);
        return (c, fallen, holder, host);
    }

    /// <summary>A movement already living abroad, with its office in the host's region.</summary>
    static MovementState Abroad(Nation nation, string id, float legitimacy, params string[] homeland)
    {
        nation.Movement = new MovementState
        {
            Id = id, Exile = true, Landless = true, Dissolved = false,
            HeadquartersStateMapIndex = 3003, Legitimacy = legitimacy, Cadres = 4000f
        };
        nation.Movement.Homeland.AddRange(homeland);
        nation.Movement.Peoples.Add("russian");
        return nation.Movement;
    }

    static void SetDay(Campaign c, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c, day * 24L);

    static bool RawId(string name) =>
        string.IsNullOrEmpty(name) || name.StartsWith("rising:", StringComparison.Ordinal) || name.Contains('@') ||
        NationNaming.LooksLikeKey(name);

    public static void Run()
    {
        DemographyCatalog.ResetCatalog(); Loc.ResetCatalog(); OpinionCatalog.ResetCatalog(); MovementCatalog.ResetCatalog();
        WhoHadAGovernment();
        ArmedGroupsAndFragmentsDoNotGoAbroad();
        AFadingExileEnds();
        NeverTwoForOneState();
        AnUndergroundDoesNotEraseTheGovernment();
        ARestoredExileKeepsItsRecord();
        FoundingsAreStamped();
        NoRawIdsForNames();
        OldSavesSettle();
    }

    static void WhoHadAGovernment()
    {
        // The holder (id 1) stands on ground of its own; a state with none is gone.
        var (c, _, holder, _) = Fixture();
        var gone = c.AddNation(new Nation { Tag = "QXG", Name = "Gone", MapIndex = 507 });
        bool Had(Nation n) => MovementFall.HadGovernment(c, n);
        Check(Had(new Nation { Tag = "AAA" }), "a country the world began with had a government");
        Check(Had(new Nation { Tag = "BDI", SecededFromId = holder.Id, RevoltParentId = holder.Id }),
            "so did one that walked out of an overlord: it was a state before it was a subject");
        Check(Had(new Nation { Tag = "Y01", FoundedInRevolt = "discontent", SecededFromId = -1,
                RevoltParentId = 5, ReleasedFromId = 5, FoundNameKind = "tag", FoundNameId = "YEM" }),
            "and a side that took the state's own name and seat was that state's government");
        Check(!Had(new Nation { Tag = "U09", FoundedInRevolt = "national", SecededFromId = holder.Id,
                RevoltParentId = holder.Id, ReleasedFromId = holder.Id, FoundNameKind = "tag", FoundNameId = "UKR" }),
            "but a breakaway that only named itself after a fallen country is still a breakaway");
        Check(!Had(new Nation { Tag = "ADF", FactionId = "allied_democratic_forces", SecededFromId = holder.Id }),
            "a faction of the bookmark never had one");
        Check(!Had(new Nation { Tag = "B01", Name = "rising:revolutionary_front:BGD:Bangladesh" }),
            "nor a front of a civil war");
        Check(!Had(new Nation { Tag = "TTP", FoundedInRevolt = "discontent", RevoltParentId = 107 }),
            "nor a country born in a rising");
        Check(!Had(new Nation { Tag = "U03", Name = "Western Dnipropetrovsk", SecededFromId = holder.Id,
                RevoltParentId = holder.Id, ReleasedFromId = holder.Id, FoundNameKind = "region", FoundNameId = "ua_12_4039" }),
            "nor a piece a war carved out of a state that still claims it");
        // Let go rather than torn away.
        Check(Had(new Nation { Tag = "TRT", RevoltParentId = holder.Id, ReleasedFromId = holder.Id,
                FoundNameKind = "region", FoundNameId = "qx_home" }),
            "a country released by treaty, which nobody claims back, governed as a state");
        Check(Had(new Nation { Tag = "DSM", SecededFromId = gone.Id, RevoltParentId = gone.Id, ReleasedFromId = gone.Id,
                FoundNameKind = "region", FoundNameId = "qx_home" }),
            "so did the successor of a state that has been dismantled: its parent is gone");
        Check(!Had(new Nation { Tag = "SBR", SecededFromId = holder.Id, RevoltParentId = holder.Id, ReleasedFromId = holder.Id,
                FoundNameKind = "culture", FoundNameId = "russian" }),
            "while one its living parent still claims is a piece of that parent");
        Check(!Had(new Nation { Tag = "RLR", FoundedInRevolt = "discontent", SecededFromId = gone.Id, RevoltParentId = gone.Id,
                ReleasedFromId = gone.Id, FoundNameKind = "region", FoundNameId = "qx_home" }),
            "and a rising stays a rising when the state it rose against is gone");
    }

    static void ArmedGroupsAndFragmentsDoNotGoAbroad()
    {
        // The control: a real country, made certain to go (MovementExileBase 2), goes.
        var (control, state, _, _) = Fixture();
        control.Defines.MovementExileBase = 2f;
        MovementFall.Decide(control);
        Check(state.Movement is { Exile: true } m && m.Id.StartsWith(MovementFall.ExilePrefix, StringComparison.Ordinal),
            "the control: a country with a government, certain to go abroad, goes into exile");

        foreach (var (label, shape) in new (string, Action<Nation, Nation>)[]
                 {
                     ("a militia of the bookmark", (n, parent) => { n.FactionId = "test_militia"; n.SecededFromId = n.RevoltParentId = parent.Id; }),
                     ("a fragment a war carved out", (n, parent) => { n.SecededFromId = n.RevoltParentId = n.ReleasedFromId = parent.Id; n.FoundNameKind = "region"; n.FoundNameId = "qx_home"; }),
                     ("a front of a civil war", (n, parent) => { n.Name = "rising:transitional_council:QXB:Holder"; n.FoundedInRevolt = "discontent"; n.SecededFromId = n.RevoltParentId = parent.Id; }),
                 })
        {
            // Certain to go abroad if it were allowed to, and certain not to go underground.
            var (c, group, parent, _) = Fixture();
            shape(group, parent);
            c.Defines.MovementExileBase = 2f;
            c.Defines.MovementUndergroundBase = -5f;
            MovementFall.Decide(c);
            Check(c.Fallen.Contains(group.Id) && group.Movement is null,
                $"{label} that loses its ground does not become a government in exile: it is over");
            // Or it goes underground among its own people - still no office abroad.
            var (c2, group2, parent2, _) = Fixture();
            shape(group2, parent2);
            c2.Defines.MovementExileBase = 2f;
            c2.Defines.MovementUndergroundBase = 5f;
            MovementFall.Decide(c2);
            Check(group2.Movement is { Exile: true } under && !MovementFall.GovernmentInExile(under) &&
                  under.Id.StartsWith(MovementCatalog.UndergroundPrefix, StringComparison.Ordinal) &&
                  c2.States.First(s => s.MapIndex == under.HeadquartersStateMapIndex).Id == "qx_home",
                $"{label} that goes on goes underground in its own homeland, not abroad");
        }
    }

    static void AFadingExileEnds()
    {
        var (c, exile, _, host) = Fixture();
        var state = Abroad(exile, "exile_qxa", 0.02f, "qx_home");
        var fade = c.Defines.MovementExileFadeDays;
        Check(fade > 0 && c.Defines.MovementExileFadeLegitimacy > 0.02f, "the fading rule is on, with its threshold above the fixture's standing");
        SetDay(c, 400);
        MovementFall.Endure(c, exile);
        Check(state is { Dissolved: false } && state.ReadyOnDay.GetValueOrDefault(MovementFall.FadingKey, -1) == 400,
            "an exile nobody takes seriously starts to fade; it is not gone at once");

        // The clock is campaign state: it travels in a snapshot as a save would keep it.
        var snapshot = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "exile"), "exile");
        Check(snapshot.Success && snapshot.Campaign.NationById(exile.Id).Movement.ReadyOnDay.GetValueOrDefault(MovementFall.FadingKey, -1) == 400,
            "the day it began to fade survives a save and a load");

        SetDay(c, 400 + fade - 1);
        MovementFall.Endure(c, exile);
        Check(!state.Dissolved, "a day short of the threshold it still stands");
        SetDay(c, 400 + fade);
        MovementFall.Endure(c, exile);
        Check(state.Dissolved && state.HeadquartersStateMapIndex < 0,
            "after MovementExileFadeDays of it the exile is gone, and its office leaves the map");
        Check(c.Chronicle.Entries.Any(entry => entry.Kind == "movement_dissolved" && entry.ActorId == exile.Id),
            "and the world's chronicle says so");

        // A capital that recognises it and gives nothing is a well-wisher, not a patron.
        var (c2, exile2, _, host2) = Fixture();
        var state2 = Abroad(exile2, "exile_qxa", 0.02f, "qx_home");
        state2.RecognisedBy.Add(host2.Id);
        SetDay(c2, 400);
        MovementFall.Endure(c2, exile2);
        Check(state2.ReadyOnDay.ContainsKey(MovementFall.FadingKey), "recognition alone does not stop the fading");
        // One that recognises it and pays for it is a patron, and the clock is wound back.
        state2.SponsoredBy[host2.Id] = 0.05f;
        SetDay(c2, 407);
        MovementFall.Endure(c2, exile2);
        Check(!state2.ReadyOnDay.ContainsKey(MovementFall.FadingKey), "a patron who recognises it and pays for it winds the clock back");
        SetDay(c2, 400 + fade * 3);
        MovementFall.Endure(c2, exile2);
        Check(!state2.Dissolved, "an exile with a patron is not faded however long it waits");

        // One the world still takes seriously never starts.
        var (c3, exile3, _, _) = Fixture();
        var state3 = Abroad(exile3, "exile_qxa", 0.3f, "qx_home");
        SetDay(c3, 400);
        MovementFall.Endure(c3, exile3);
        Check(!state3.ReadyOnDay.ContainsKey(MovementFall.FadingKey) && !state3.Dissolved,
            "an exile with standing does not fade");
        // Nor one its own people still carry: a following and fighters above the crushed lines.
        var (c5, carried, _, home5) = Fixture();
        var state5 = Abroad(carried, "exile_qxa", 0.02f, "qx_home");
        state5.Support[3001] = 0.2f;
        SetDay(c5, 400);
        MovementFall.Endure(c5, carried);
        SetDay(c5, 400 + fade * 2);
        MovementFall.Endure(c5, carried);
        Check(!state5.ReadyOnDay.ContainsKey(MovementFall.FadingKey) && !state5.Dissolved,
            "an exile its people at home still follow, with fighters behind it, does not fade whatever the world thinks");
        // The same rule for a beaten rising gone underground.
        var (c4, under, _, _) = Fixture();
        under.FoundedInRevolt = "discontent";
        var state4 = Abroad(under, MovementCatalog.UndergroundPrefix + "qxa", 0.02f, "qx_home");
        SetDay(c4, 400);
        MovementFall.Endure(c4, under);
        SetDay(c4, 400 + fade);
        MovementFall.Endure(c4, under);
        Check(state4.Dissolved, "an underground nobody takes seriously and nobody pays for fades the same way");
    }

    static void NeverTwoForOneState()
    {
        // A government is already abroad for this ground; the next to fall from it is refused.
        var (c, fallen, _, _) = Fixture();
        var elder = c.AddNation(new Nation { Tag = "QXD", Name = "Elderland", MapIndex = 504, Government = "parliamentary_republic" });
        Abroad(elder, "exile_qxd", 0.5f, "qx_home");
        c.Defines.MovementExileBase = 2f;
        MovementFall.Decide(c);
        Check(c.Fallen.Contains(fallen.Id) && fallen.Movement is null && elder.Movement is { Dissolved: false },
            "a second government for a state that already has one abroad is not founded");
        Check(c.Nations.Count(n => n.Movement is { Exile: true, Landless: true, Dissolved: false }) == 1,
            "one lost state, one exile");

        // A save made before the refusal holds two: the government abroad keeps the cause, the
        // older country before the younger, and the other passes into history at its review.
        var (old, first, _, _) = Fixture();
        var second = old.AddNation(new Nation { Tag = "QXD", Name = "Elderland", MapIndex = 504, Government = "parliamentary_republic" });
        Abroad(first, "exile_qxa", 0.5f, "qx_home");
        Abroad(second, "exile_qxd", 0.5f, "qx_home");
        SetDay(old, 400);
        MovementFall.Endure(old, first);
        MovementFall.Endure(old, second);
        Check(first.Movement is { Dissolved: false } && second.Movement is { Dissolved: true },
            "of two exiles for one state the older keeps the cause and the younger is dissolved");
        var (mixed, government, _, _) = Fixture();
        var rising = mixed.AddNation(new Nation { Tag = "QXE", Name = "Rebels", MapIndex = 505, FoundedInRevolt = "discontent" });
        Abroad(government, "exile_qxa", 0.5f, "qx_home");
        Abroad(rising, MovementCatalog.UndergroundPrefix + "qxe", 0.5f, "qx_home");
        SetDay(mixed, 400);
        MovementFall.Endure(mixed, rising);
        Check(rising.Movement.Dissolved && !government.Movement.Dissolved,
            "and a government abroad comes before an underground for the same ground");
        // Sharing a valley is not being the same state.
        var (apart, one, _, _) = Fixture();
        var other = apart.AddNation(new Nation { Tag = "QXF", Name = "Neighbour", MapIndex = 506, Government = "parliamentary_republic" });
        Abroad(one, "exile_qxa", 0.5f, "qx_home");
        Abroad(other, "exile_qxf", 0.5f, "qx_home", "qx_far", "qx_refuge");
        SetDay(apart, 400);
        MovementFall.Endure(apart, other);
        Check(!other.Movement.Dissolved, "an exile whose homeland only overlaps another's is not its duplicate");
    }

    static void AnUndergroundDoesNotEraseTheGovernment()
    {
        // A beaten rising went underground on this ground first; then the state itself falls.
        var (c, state, _, _) = Fixture();
        var rising = c.AddNation(new Nation { Tag = "QXE", Name = "Rebels", MapIndex = 505, FoundedInRevolt = "discontent" });
        Abroad(rising, MovementCatalog.UndergroundPrefix + "qxe", 0.3f, "qx_home");
        c.Defines.MovementExileBase = 2f;
        MovementFall.Decide(c);
        Check(state.Movement is { } m && MovementFall.GovernmentInExile(m) && !m.Dissolved,
            "an underground on the same ground does not stop the state's own government going into exile");
        SetDay(c, 400);
        MovementFall.Endure(c, rising);
        Check(rising.Movement.Dissolved && !state.Movement.Dissolved,
            "and the underground gives way to that government at its next review");
    }

    static void ARestoredExileKeepsItsRecord()
    {
        // A country of the bookmark in exile comes home in a rising on its own ground ...
        var (c, exile, holder, _) = Fixture();
        Abroad(exile, "exile_qxa", 0.5f, "qx_home");
        var home = c.States.First(s => s.MapIndex == 3001);
        var report = SecessionEvents.Uprising(c, holder, new[] { home }, null);
        Check(report?.Seceded == exile && exile.Movement is null && c.StatesOf(exile).Length > 0,
            "the government in exile comes home: the rising on its ground is that country restored");
        Check(string.IsNullOrEmpty(exile.FoundedInRevolt) && MovementFall.HadGovernment(c, exile),
            "its birth record is kept: it is not a country born in a rising");
        // ... and loses it again.
        var provinces = Enumerable.Range(1, c.ProvinceMap.ProvinceCount)
            .Where(p => c.ProvinceMap.ProvinceToState[p] == home.MapIndex).ToArray();
        foreach (var p in provinces) c.SetProvinceController(p, (ushort)holder.MapIndex);
        StatePartition.Annex(c, provinces, holder, asConquest: false);
        foreach (var w in c.Wars) w.Ended = true;
        exile.Population = 0;
        c.Defines.MovementExileBase = 2f;
        Check(c.StatesOf(exile).Length == 0 && !c.Fallen.Contains(exile.Id), "the fixture: it holds nothing again and has not yet fallen");
        MovementFall.Decide(c);
        Check(exile.Movement is { } again && MovementFall.GovernmentInExile(again) && !again.Dissolved,
            "beaten again, it goes into exile again as the government it always was");
    }

    static void FoundingsAreStamped()
    {
        var (c, state, _, _) = Fixture();
        SetDay(c, 900);
        c.Defines.MovementExileBase = 2f;
        MovementFall.Decide(c);
        Check(state.Movement?.ReadyOnDay.GetValueOrDefault("lifecycle_born", -1) == 900,
            "a government in exile is stamped with the day it was founded");
        var (c2, group, parent, _) = Fixture();
        group.FactionId = "test_militia"; group.SecededFromId = group.RevoltParentId = parent.Id;
        SetDay(c2, 900);
        c2.Defines.MovementUndergroundBase = 5f;
        MovementFall.Decide(c2);
        Check(group.Movement?.ReadyOnDay.GetValueOrDefault("lifecycle_born", -1) == 900,
            "and so is an underground, so its first year is a year of grace");
        // A save from before the stamp: the review stamps it, and the crushed ending waits a year.
        var (c3, old, _, _) = Fixture();
        old.FoundedInRevolt = "discontent";
        var under = Abroad(old, MovementCatalog.UndergroundPrefix + "qxa", 0.3f, "qx_home");
        under.Cadres = 100f; under.Support[3001] = 0f; old.Treasury = 1f;
        SetDay(c3, 2000);
        MovementFall.Endure(c3, old);
        Check(under.ReadyOnDay.GetValueOrDefault("lifecycle_born", -1) == 2000 && !under.Dissolved,
            "an old save's underground is stamped at its review and not crushed the same week");
        SetDay(c3, 2000 + 365);
        MovementFall.Endure(c3, old);
        Check(under.Dissolved, "a year later the crushed ending applies to it like any organisation at home");
    }

    static void NoRawIdsForNames()
    {
        var (c, front, parent, _) = Fixture();
        front.Name = "rising:transitional_council:QXB:Holder";
        front.FoundedInRevolt = "discontent";
        front.SecededFromId = front.RevoltParentId = parent.Id;
        front.FoundedCultures.Add("russian");
        c.Defines.MovementUndergroundBase = 5f;
        MovementFall.Decide(c);
        Check(!RawId(front.Name) && !RawId(front.DisplayName),
            $"a front of a civil war that falls is named like any new country, not \"rising:...\" (now \"{front.Name}\")");
    }

    static void OldSavesSettle()
    {
        // A save from before the rule: a militia's office abroad. It loads and closes at its next review.
        var (c, militia, _, _) = Fixture();
        militia.FactionId = "codeco";
        militia.SecededFromId = militia.RevoltParentId = 1;
        Abroad(militia, "exile_qxa", 0.55f, "qx_home");
        var loaded = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "exile"), "exile");
        Check(loaded.Success, "a save holding a militia's government in exile loads");
        var world = loaded.Campaign;
        var again = world.NationById(militia.Id);
        SetDay(world, 400);
        MovementFall.Endure(world, again);
        Check(again.Movement is { Dissolved: true } && world.Chronicle.Entries.Any(e => e.Kind == "movement_dissolved" && e.ActorId == again.Id),
            "a militia's government in exile from an old save is dissolved at its next review, with a chronicle line");

        // A released country whose parent is gone, abroad in an old save, is a government: it stays.
        var (cr, released, _, _) = Fixture();
        var gone = cr.AddNation(new Nation { Tag = "QXG", Name = "Gone", MapIndex = 507 });
        released.SecededFromId = released.RevoltParentId = released.ReleasedFromId = gone.Id;
        released.FoundNameKind = "region"; released.FoundNameId = "qx_home";
        Abroad(released, "exile_qxa", 0.5f, "qx_home");
        SetDay(cr, 400);
        MovementFall.Endure(cr, released);
        Check(released.Movement is { Dissolved: false },
            "a released country's government in exile from an old save is kept when nobody claims it back");
        Check(Loc.TextIn("russian", "{0} already speaks for this country: this one has passed into history.") is { } ru &&
              ru.Contains("другое движение", StringComparison.Ordinal) && ru.Contains("{0}", StringComparison.Ordinal),
            "the Russian line names the other movement with a fixed noun, so the verb agrees whatever {0} is");

        // And an underground from such a save still written down as a front of its war.
        var (c2, front, _, _) = Fixture();
        front.Name = "rising:transitional_council:QXB:Holder";
        front.FoundedInRevolt = "discontent";
        front.SecededFromId = front.RevoltParentId = 1;
        Abroad(front, MovementCatalog.UndergroundPrefix + "qxa", 0.4f, "qx_home");
        SetDay(c2, 400);
        MovementFall.Endure(c2, front);
        Check(!RawId(front.Name) && front.Movement is { Dissolved: false },
            $"an underground from an old save gets a proper name at its review and carries on (now \"{front.Name}\")");
    }
}
