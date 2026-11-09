using System;
using DealOrForce.Simulation;

/// <summary>
/// Two books on one shelf do not double a shared modifier. The larger contribution is kept,
/// and the smaller one adds only DoctrineSameShelfStack of itself. Another shelf still adds
/// in full. A single book is the article, as it was.
/// </summary>
static class DoctrineStackTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL Doctrine stack: " + label);
        Console.WriteLine("PASS Doctrine stack: " + label);
    }

    static void Clear(Nation nation)
    {
        nation.TacticalDoctrine = new TacticalDoctrineMemory();
        NationalModifiers.Invalidate(nation);
    }

    static void Learn(Nation nation, string branchId, bool companion, params string[] nodes)
    {
        var branch = TacticalDoctrineCatalog.Find(branchId);
        TacticalDoctrines.EnsureShelves(nation);
        var track = nation.TacticalDoctrine.Track(branch.Slot);
        if (companion)
        {
            track.Companion ??= new DoctrineTrack();
            track = track.Companion;
        }
        track.Branch = branchId;
        foreach (var id in nodes)
            if (!track.Known.Contains(id)) track.Known.Add(id);
        NationalModifiers.Invalidate(nation);
    }

    public static void Run()
    {
        var fire = TacticalDoctrineCatalog.Find("fire")?.Find("fire_concentrated");
        var columns = TacticalDoctrineCatalog.Find("manoeuvre")?.Find("manoeuvre_columns");
        var march = TacticalDoctrineCatalog.Find("manoeuvre")?.Find("manoeuvre_march");
        var screen = TacticalDoctrineCatalog.Find("manoeuvre")?.Find("manoeuvre_screen");
        var convoy = TacticalDoctrineCatalog.Find("coast")?.Find("coast_convoy");
        Check(fire is not null && columns is not null && march is not null && screen is not null && convoy is not null,
            "the shelf-cap articles are still in the shipped books");
        var attackFire = fire.Modifiers["army_attack"];
        var attackColumns = columns.Modifiers["army_attack"];
        var speedMarch = march.Modifiers["army_speed"];
        var speedColumns = columns.Modifiers["army_speed"];
        var recoveryMarch = march.Modifiers["organisation_recovery"];
        var recoveryFire = fire.Modifiers["support_recovery"];
        var supplyScreen = screen.Modifiers["supply"];
        var supplyConvoy = convoy.Modifiers["supply"];
        var naval = convoy.Modifiers["naval_speed"];
        Check(attackFire == 0.06f && attackColumns == 0.04f && speedMarch == 0.06f && speedColumns == 0.05f &&
              recoveryMarch == 0.05f && supplyScreen == 0.03f && supplyConvoy == 0.03f && naval == 0.03f,
            "shipped articles still carry 0.06 attack, 0.04 attack, and 0.03 supply");

        var campaign = Campaign.CreateBootstrap(20241109);
        var nation = campaign.NationByTag("POL");
        Check(nation is not null, "Poland is on the bootstrap");
        var random = campaign.Random.State;

        var speedBefore = NationalModifiers.Of(nation, "army_speed");
        var recoveryBefore = NationalModifiers.Of(nation, "organisation_recovery");
        Learn(nation, "manoeuvre", false, "manoeuvre_march");
        Check(NationalModifiers.Of(nation, "army_speed") == speedBefore + speedMarch,
            "one book: division speed equals the article");
        Check(NationalModifiers.Of(nation, "organisation_recovery") == recoveryBefore + recoveryMarch,
            "one book: organisation recovery equals the article");
        float bothSpeeds = 0f;
        bothSpeeds += speedMarch;
        bothSpeeds += speedColumns;
        Learn(nation, "manoeuvre", false, "manoeuvre_columns");
        Check(NationalModifiers.Of(nation, "army_speed") == speedBefore + bothSpeeds,
            "two articles of one book still add in full");

        Clear(nation);
        var attackBefore = NationalModifiers.Of(nation, "army_attack");
        speedBefore = NationalModifiers.Of(nation, "army_speed");
        var supportBefore = NationalModifiers.Of(nation, "support_recovery");
        Learn(nation, "fire", false, "fire_concentrated");
        Learn(nation, "manoeuvre", true, "manoeuvre_columns");
        var half = attackBefore + Math.Max(attackFire, attackColumns) +
            Defines.DefaultDoctrineSameShelfStack * Math.Min(attackFire, attackColumns);
        Check(NationalModifiers.Of(nation, "army_attack") == half,
            "same shelf: shared attack is the larger plus half the smaller");
        Check(NationalModifiers.Of(nation, "army_speed") == speedBefore + speedColumns,
            "same shelf: a key only the second book carries is added whole");
        Check(NationalModifiers.Of(nation, "support_recovery") == supportBefore + recoveryFire,
            "same shelf: a key only the first book carries is added whole");

        Clear(nation);
        var supplyBefore = NationalModifiers.Of(nation, "supply");
        var navalBefore = NationalModifiers.Of(nation, "naval_speed");
        Learn(nation, "manoeuvre", false, "manoeuvre_screen");
        Learn(nation, "coast", false, "coast_convoy");
        Check(NationalModifiers.Of(nation, "supply") == supplyBefore + supplyScreen + supplyConvoy,
            "two shelves: a shared supply key adds in full");
        Check(NationalModifiers.Of(nation, "naval_speed") == navalBefore + naval,
            "two shelves: fleet speed is the naval article alone");

        Clear(nation);
        attackBefore = NationalModifiers.Of(nation, "army_attack");
        Learn(nation, "fire", false, "fire_concentrated");
        Learn(nation, "manoeuvre", true, "manoeuvre_columns");
        campaign.Defines.DoctrineSameShelfStack = 1f;
        NationalModifiers.Invalidate(nation);
        Check(MathF.Abs(NationalModifiers.Of(nation, "army_attack") - attackBefore - attackFire - attackColumns) < 0.0001f,
            "define 1: the same shelf adds in full");
        campaign.Defines.DoctrineSameShelfStack = 0f;
        NationalModifiers.Invalidate(nation);
        Check(MathF.Abs(NationalModifiers.Of(nation, "army_attack") - attackBefore - Math.Max(attackFire, attackColumns)) < 0.0001f,
            "define 0: only the larger contribution remains");

        Clear(nation);
        campaign.Defines.DoctrineSameShelfStack = 0f;
        speedBefore = NationalModifiers.Of(nation, "army_speed");
        Learn(nation, "manoeuvre", false, "manoeuvre_march");
        Check(NationalModifiers.Of(nation, "army_speed") == speedBefore + speedMarch,
            "define 0 does not change a single book");
        campaign.Defines.DoctrineSameShelfStack = 1f;
        NationalModifiers.Invalidate(nation);
        Check(NationalModifiers.Of(nation, "army_speed") == speedBefore + speedMarch,
            "define 1 does not change a single book");

        Clear(nation);
        campaign.Defines.DoctrineSameShelfStack = Defines.DefaultDoctrineSameShelfStack;
        NationalModifiers.Invalidate(nation);
        var untouched = NationalModifiers.Of(nation, "army_attack");
        var print = CampaignFingerprint.Of(campaign);
        campaign.Defines.DoctrineSameShelfStack = 0.5f;
        Check(CampaignFingerprint.Of(campaign) == print, "the default half is not mixed into the fingerprint");
        campaign.Defines.DoctrineSameShelfStack = 1f;
        var fullPrint = CampaignFingerprint.Of(campaign);
        Check(fullPrint != print, "define 1 is mixed into the fingerprint");
        campaign.Defines.DoctrineSameShelfStack = 0f;
        var nonePrint = CampaignFingerprint.Of(campaign);
        Check(nonePrint != print && nonePrint != fullPrint, "define 0 is mixed and differs from full stacking");
        campaign.Defines.DoctrineSameShelfStack = Defines.DefaultDoctrineSameShelfStack;
        Check(CampaignFingerprint.Of(campaign) == print, "restoring the half restores the fingerprint");
        Check(NationalModifiers.Of(nation, "army_attack") == untouched,
            "a country with no second book keeps the same modifiers");

        Learn(nation, "manoeuvre", false, "manoeuvre_march");
        var written = CampaignFingerprint.Of(campaign);
        var spent = campaign.Random.State;
        var read = NationalModifiers.Of(nation, "army_speed");
        foreach (var _ in TacticalDoctrines.Sources(nation)) { }
        var line = TacticalDoctrines.SameShelfStackLine();
        Check(read == NationalModifiers.Of(nation, "army_speed") &&
              CampaignFingerprint.Of(campaign) == written && campaign.Random.State == spent,
            "reading the modifiers and the shelf sentence does not change the fingerprint");
        Check(line == TacticalDoctrines.SameShelfStackSentence, "the English sentence is the fixed label");
        var language = Loc.Language;
        try
        {
            Loc.Language = "russian";
            Check(TacticalDoctrines.SameShelfStackLine() ==
                  "Вторая книга на той же полке добавляет половину повторяющегося бонуса.",
                "the Russian sentence is the fixed label");
        }
        finally { Loc.Language = language; }

        var other = Campaign.CreateBootstrap(20241109);
        var twin = other.NationByTag("POL");
        var twinBefore = NationalModifiers.Of(twin, "army_speed");
        Learn(twin, "manoeuvre", false, "manoeuvre_march");
        Check(NationalModifiers.Of(twin, "army_speed") == twinBefore + speedMarch &&
              NationalModifiers.Of(twin, "army_speed") == NationalModifiers.Of(nation, "army_speed"),
            "another campaign with one book keeps the same modifiers");

        var homeAttack = NationalModifiers.Of(nation, "army_attack");
        other.Defines.DoctrineSameShelfStack = 0f;
        Clear(twin);
        var foreignBefore = NationalModifiers.Of(twin, "army_attack");
        Learn(twin, "fire", false, "fire_concentrated");
        Learn(twin, "manoeuvre", true, "manoeuvre_columns");
        Check(NationalModifiers.Of(twin, "army_attack") == foreignBefore + Math.Max(attackFire, attackColumns),
            "the other campaign's define 0 stays on that campaign");
        Check(NationalModifiers.Of(nation, "army_attack") == homeAttack,
            "reading the other campaign does not change this one's modifiers");
        Check(campaign.Random.State == random, "the shelf ceiling does not spend the campaign's randomness");
    }
}
