using System;
using System.Reflection;
using DealOrForce.Simulation;

/// <summary>
/// The short strategic HUD: a fact the campaign already has, or the quiet sentence,
/// and a clock row that does not grow when the short form fits.
/// </summary>
static class CompactHudTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("compact HUD: " + label);
        Console.WriteLine("PASS compact HUD: " + label);
    }

    public static void Run()
    {
        var previous = Loc.Language;
        Loc.Language = "english";
        try
        {
            Check(SaveSystem.FormatVersion == 2, "save format stays 2");
            QuietWeek();
            SupplyNamesTheFact();
            WarNamesTheFact();
            RussianSupplyLine();
            BriefCaptions();
            ClockRow();
        }
        finally { Loc.Language = previous; }
    }

    static void QuietWeek()
    {
        var campaign = Bare();
        var nation = campaign.Nations[0];
        var before = CampaignFingerprint.Of(campaign);
        var lines = StrategicHudBrief.Read(campaign, nation);
        Check(lines.Count == 0, "a week with no war and no short supply has no short line");
        var shown = StrategicHudBrief.Shown(lines);
        Check(shown == Loc.Get(StrategicHudBrief.QuietSentence) && shown.Length > 0,
            "the quiet sentence is what the player reads: " + shown);
        Check(CampaignFingerprint.Of(campaign) == before, "reading the quiet line does not change the fingerprint");
    }

    static void SupplyNamesTheFact()
    {
        var campaign = Bare();
        var army = new Army
        {
            Id = 4, NationId = 0, ProvinceId = 1, OrderKind = ArmyOrder.AdvanceFront,
            Divisions = 1f, Organisation = 1f, SupplyLevel = 0.2f, Strength = 1f,
            DirectOrder = false, CutOff = true, SupplyDistance = -1, FrontLineId = -1
        };
        campaign.Armies.Add(army);
        var before = CampaignFingerprint.Of(campaign);
        var lines = StrategicHudBrief.Read(campaign, campaign.Nations[0]);
        var supply = lines.Find(line => line.Id == "supply");
        Check(supply.Id == "supply" && supply.Short == Loc.Format("Out of supply: {0}", 1),
            "a cut-off formation makes the short supply line: " + supply.Short);
        Check(supply.Full.Contains(Loc.Get("No path to a supply hub, so the attack does not start.")),
            "the tooltip keeps the staff sentence");
        Check(supply.ColourToken == "state.warning" && supply.Screen == "Military" && supply.ArmyId == 4,
            "the line points at the army and does not start an order");
        Check(army.DirectOrder == false && army.OrderKind == ArmyOrder.AdvanceFront && army.CutOff,
            "reading the line leaves the direct order and the cutoff where they were");
        Check(CampaignFingerprint.Of(campaign) == before, "reading the supply line does not change the fingerprint");
        Check(!lines.Exists(line => line.Id.StartsWith("war", StringComparison.Ordinal)),
            "supply alone does not invent a war line");
    }

    static void WarNamesTheFact()
    {
        var campaign = Bare();
        campaign.Wars.Add(new StrategicWar
        {
            Id = 9, AttackerId = 0, DefenderId = 1, WarScore = 12f, AttackerWarSupport = 0.8f
        });
        var before = CampaignFingerprint.Of(campaign);
        var lines = StrategicHudBrief.Read(campaign, campaign.Nations[0]);
        var war = lines.Find(line => line.Id == "war_9");
        Check(war.Id == "war_9" && war.Short.Contains("DF1") && war.Short.Contains("12"),
            "a war is a short line with the tag and the score: " + war.Short);
        Check(war.Short != Loc.Get(StrategicHudBrief.QuietSentence), "a war is not the quiet sentence");
        Check(war.Full.Contains("DF1") && war.Screen == "Wars" && war.WarId == 9,
            "the full sentence and the war screen stay on the line");
        Check(!lines.Exists(line => line.Id == "supply"), "a supplied country at war has no supply line");
        Check(CampaignFingerprint.Of(campaign) == before, "reading the war line does not change the fingerprint");
    }

    static void RussianSupplyLine()
    {
        var previous = Loc.Language;
        try
        {
            Loc.Language = "russian";
            var campaign = Bare();
            campaign.Armies.Add(new Army
            {
                Id = 1, NationId = 0, SupplyLevel = 1f, CutOff = true, DirectOrder = false,
                OrderKind = ArmyOrder.DefendFront
            });
            var line = StrategicHudBrief.Read(campaign, campaign.Nations[0]).Find(item => item.Id == "supply");
            Check(line.Short == "Без снабжения: 1", "the Russian short supply line is Без снабжения: 1");
            Check(StrategicHudBrief.Shown(new System.Collections.Generic.List<StrategicHudBrief.Line>()) ==
                  "Ничего не требует внимания.",
                "the Russian quiet sentence is Ничего не требует внимания.");
        }
        finally { Loc.Language = previous; }
    }

    static void BriefCaptions()
    {
        var previous = Loc.Language;
        try
        {
            foreach (var language in new[] { "english", "russian" })
            {
                Loc.Language = language;
                foreach (var key in new[]
                         {
                             "hud.stat.brief.TREASURY", "hud.stat.brief.INFLATION", "hud.stat.brief.STABILITY",
                             "hud.stat.brief.POLITICAL CAPITAL", "hud.figures.brief", "hud.figures.full",
                             "hud.figures.brief.tip", "hud.figures.full.tip", "hud.brief.war", "hud.brief.papers",
                             "hud.brief.quiet.tip"
                         })
                {
                    var text = Loc.Get(key);
                    Check(text.Length > 0 && text != key, language + " has a caption for " + key);
                }
            }
            Loc.Language = "russian";
            Check(Loc.Get("hud.stat.brief.INFLATION").Length < Loc.Get("hud.stat.short.INFLATION").Length,
                "the Russian brief inflation caption is shorter than the old short one");
            Check(Loc.Get("hud.figures.brief") == "Кратко" && Loc.Get("hud.figures.full") == "Полно",
                "the clock buttons read Кратко and Полно");
            Loc.Language = "english";
            Check(Loc.Get("hud.figures.brief") == "Brief" && Loc.Get("hud.figures.full") == "Full",
                "the clock buttons read Brief and Full");
            Check(Loc.Get("hud.stat.brief.TREASURY") == "CASH" && Loc.Get("hud.stat.brief.POLITICAL CAPITAL") == "POL.",
                "the English brief captions read CASH and POL.");
        }
        finally { Loc.Language = previous; }
    }

    static void ClockRow()
    {
        var fitted = ResponsiveHudLayout.Clock(1280, 1280, 1800, 1100, false, false);
        Check(fitted.Brief && !fitted.SecondRow && fitted.HeaderExtra == 0,
            "a short row that fits stays on the clock and does not push the map");
        var dropped = ResponsiveHudLayout.Clock(1280, 1280, 1800, 1400, false, false);
        Check(dropped.Brief && dropped.SecondRow && dropped.HeaderExtra == 32,
            "a short row that still overflows takes the shorter second row");
        var asked = ResponsiveHudLayout.Clock(1280, 1280, 1800, 1100, true, false);
        Check(!asked.Brief && asked.SecondRow && asked.HeaderExtra == 52,
            "asking for the long form puts the sentences back on the taller row");
        var wide = ResponsiveHudLayout.Clock(1920, 1920, 1400, 1100, false, false);
        Check(!wide.Brief && !wide.SecondRow && wide.HeaderExtra == 0,
            "a wide window keeps the long figures on one row");
        var stretched = ResponsiveHudLayout.Clock(1920, 1280, 1500, 1100, false, false);
        Check(stretched.Brief && !stretched.SecondRow,
            "a stretched canvas still follows the real window, and the short row fits there");
        var forced = ResponsiveHudLayout.Clock(1920, 1920, 1400, 1100, false, true);
        Check(forced.Brief && !forced.SecondRow, "the short form can be asked for on a wide window");
        var unknown = ResponsiveHudLayout.Clock(1280, 1280, 0, 0, false, false);
        Check(unknown.Brief && !unknown.SecondRow, "before the row is measured a narrow window starts short");
        var broken = ResponsiveHudLayout.Clock(float.NaN, float.NaN, float.NaN, float.PositiveInfinity, false, false);
        Check(float.IsFinite(broken.HeaderExtra) && broken.HeaderExtra >= 0,
            "a bad measurement does not produce an infinite header");
    }

    static Campaign Bare()
    {
        var campaign = new Campaign(30);
        campaign.Nations.Add(new Nation
        {
            Id = 0, MapIndex = 1, Tag = "H0", Name = "Home", Population = 2_000_000,
            Stability = 0.8f, Gdp = 0f, Treasury = 0f, Inflation = 0f, ShortageIndex = 0f,
            BrainDrainRate = 0f, WeeklyNetMigration = 0, CoupPlotDay = -1, IsHuman = false
        });
        campaign.Nations.Add(new Nation
        {
            Id = 1, MapIndex = 2, Tag = "DF1", Name = "Other", Population = 2_000_000, Stability = 0.8f
        });
        Check(campaign.Nations[0].Id == 0 && campaign.NationById(0) == campaign.Nations[0],
            "nation id 0 stands at index 0");
        var flags = BindingFlags.Instance | BindingFlags.Public;
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap), flags)!.SetValue(campaign, null);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController), flags)!.SetValue(campaign, new ushort[1]);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceOccupation), flags)!.SetValue(campaign, new float[1]);
        Check(campaign.ProvinceController.Length == 1 && campaign.ProvinceOccupation.Length == 1,
            "the province arrays are attached by reflection and not by AttachProvinceTopology");
        return campaign;
    }
}
