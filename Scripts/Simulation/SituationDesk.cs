using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Stability below this is a country in trouble, not a country with a bad week.</summary>
    public float DeskStabilityMust { get; set; } = 0.35f;

    /// <summary>Stability below this is worth a line on the desk.</summary>
    public float DeskStabilityShould { get; set; } = 0.50f;

    /// <summary>Average regional radicalism above this is a movement, not a mood.</summary>
    public float DeskRadicalismShould { get; set; } = 0.25f;

    /// <summary>Inflation above this is felt in every shop.</summary>
    public float DeskInflationShould { get; set; } = 0.06f;

    /// <summary>Credit confidence below this is lenders stepping back.</summary>
    public float DeskCreditShould { get; set; } = 0.40f;

    /// <summary>How far back a trend is measured, in days. A month is what a government feels.</summary>
    public int DeskTrendDays { get; set; } = 28;

    /// <summary>How long a fact about the wider world stays on the desk, in days.</summary>
    public int DeskWorldDays { get; set; } = 14;
}

/// <summary>Whose reading of the world this is. A desk is a lens, not a filter: the same week
/// read as a soldier, a treasurer, a minister of the interior or a diplomat.</summary>
public enum DeskLens
{
    Security,
    Economy,
    Society,
    Diplomacy,
    Cabinet
}

/// <summary>
/// How loud a line is. Ordered, because a desk is read top-down and the first line is the one
/// that will still be true tomorrow if nothing is done about it.
/// </summary>
public enum DeskSeverity
{
    Watch,
    Could,
    Should,
    Must
}

/// <summary>
/// One line of a desk: what is happening, the number that says how much, and where it is dealt
/// with. The trend is the phrase a reader needs to feel that the number moved - "−0.04 in four
/// weeks" is the difference between a statistic and a government in trouble.
/// </summary>
public readonly record struct DeskItem(string Id, DeskLens Lens, DeskSeverity Severity,
    string Title, string Detail, string Screen, string Trend, int NationId = -1)
{
    /// <summary>Whether this line belongs on a desk rather than in the margins of one.</summary>
    public bool Urgent => Severity is DeskSeverity.Must or DeskSeverity.Should;

    /// <summary>Whether the line points at one country rather than at a whole screen.</summary>
    public bool HasNation => NationId >= 0;
}

/// <summary>
/// The situation desk: five readings of one world, taken from what the simulation already
/// knows.
///
/// There is no new state here and no new rule. A war is a war the army is already fighting; a
/// deficit is the treasury the budget already reports; radicalism is the regions' own number.
/// What the desk adds is attention: it decides which of the thousands of true things about a
/// country are worth a minister's morning, puts a figure beside each, and says where to go to
/// do something about it.
///
/// The lenses are the point of the design. A single "alerts" list answers one question - what is
/// on fire - and a grand strategy is not only fires. A soldier reads the front, a treasurer the
/// balance, a minister of the interior the street, a diplomat the papers on the table, and the
/// cabinet what is ready to be decided. A player can sit in any of those chairs, and the week
/// looks different from each.
/// </summary>
public static class SituationDesk
{
    /// <summary>Every line of one reading, loudest first. Deterministic: same world, same desk.</summary>
    public static List<DeskItem> Read(Campaign campaign, Nation nation, DeskLens lens)
    {
        var items = new List<DeskItem>();
        if (campaign is null || nation is null || nation.Population <= 0 || campaign.Fallen.Contains(nation.Id))
            return items;
        switch (lens)
        {
            case DeskLens.Security: Security(campaign, nation, items); break;
            case DeskLens.Economy: Economy(campaign, nation, items); break;
            case DeskLens.Society: Society(campaign, nation, items); break;
            case DeskLens.Diplomacy: Diplomacy(campaign, nation, items); break;
            case DeskLens.Cabinet: Cabinet(campaign, nation, items); break;
        }
        // Every chair also sees the world move, but only the part of it that chair would care
        // about: a war elsewhere is a soldier's news, a crisis elsewhere is a diplomat's.
        World(campaign, nation, lens, items);
        return Fold(items);
    }

    /// <summary>Every lens at once, for a panel that shows one country rather than one chair.</summary>
    public static List<DeskItem> ReadAll(Campaign campaign, Nation nation)
    {
        var items = new List<DeskItem>();
        foreach (var lens in Enum.GetValues<DeskLens>()) items.AddRange(Read(campaign, nation, lens));
        items.Sort((left, right) =>
        {
            var bySeverity = right.Severity.CompareTo(left.Severity);
            return bySeverity != 0 ? bySeverity : string.CompareOrdinal(left.Id, right.Id);
        });
        return items;
    }

    /// <summary>How many urgent lines a lens is asking for.</summary>
    public static int Count(Campaign campaign, Nation nation, DeskLens lens) =>
        Read(campaign, nation, lens).Count(item => item.Urgent);

    private static List<DeskItem> Fold(List<DeskItem> items)
    {
        items.Sort((left, right) =>
        {
            var bySeverity = right.Severity.CompareTo(left.Severity);
            return bySeverity != 0 ? bySeverity : string.CompareOrdinal(left.Id, right.Id);
        });
        return items;
    }

    private static void Security(Campaign campaign, Nation nation, List<DeskItem> items)
    {
        var atWar = false;
        foreach (var war in campaign.Wars)
        {
            if (war.Ended || !war.Involves(nation.Id)) continue;
            atWar = true;
            var enemyId = war.AttackerId == nation.Id ? war.DefenderId : war.AttackerId;
            var enemy = campaign.NationById(enemyId);
            // The war score is written from the attacker's side, so a defender in front reads
            // negative on purpose: the same number means "we are winning" to one chair and
            // "we are losing" to the other.
            var losing = war.IsAttacking(nation.Id) ? war.WarScore < -8f : war.WarScore > 8f;
            items.Add(new DeskItem("war_" + war.Id, DeskLens.Security,
                losing ? DeskSeverity.Must : DeskSeverity.Should,
                Loc.Format("At war with {0}", enemy?.DisplayName ?? Loc.Get("somebody")),
                Loc.Format("war score {0:0.0}", war.WarScore), "Wars", ""));
        }

        var cutOff = 0;
        var inField = 0;
        var spent = 0;
        foreach (var army in campaign.Armies)
        {
            if (army.NationId != nation.Id) continue;
            inField++;
            if (army.CutOff) cutOff++;
            else if (army.Organisation < 0.35f) spent++;
        }
        if (cutOff > 0)
            items.Add(new DeskItem("cut_off", DeskLens.Security, DeskSeverity.Must,
                Loc.Format("{0} divisions are cut off", cutOff),
                Loc.Get("no supply route reaches them"), "Military", ""));
        if (inField == 0)
            items.Add(new DeskItem("no_army", DeskLens.Security, DeskSeverity.Must,
                Loc.Get("There are no divisions in the field"),
                Loc.Get("nothing stands between the country and its borders"), "Military", ""));
        else if (atWar && spent * 2 >= inField)
            items.Add(new DeskItem("spent_army", DeskLens.Security, DeskSeverity.Should,
                Loc.Format("{0} of {1} divisions are spent", spent, inField),
                Loc.Get("organisation below a third"), "Military", ""));
    }

    private static void Economy(Campaign campaign, Nation nation, List<DeskItem> items)
    {
        var defines = campaign.Defines;
        if (nation.Treasury < 0f)
            items.Add(new DeskItem("treasury_negative", DeskLens.Economy, DeskSeverity.Must,
                Loc.Get("The treasury is overdrawn"), Fmt.Money(nation.Treasury), "Economy", ""));

        if (nation.WeeklyBalance < 0f)
            items.Add(new DeskItem("deficit", DeskLens.Economy, DeskSeverity.Should,
                Loc.Get("Spending exceeds income"), Loc.Format("{0} a week", Fmt.Money(nation.WeeklyBalance)),
                "Economy", ""));

        if (nation.Inflation > defines.DeskInflationShould)
            items.Add(new DeskItem("inflation", DeskLens.Economy, DeskSeverity.Should,
                Loc.Get("Prices are running away"), Fmt.Percent(nation.Inflation), "Economy", ""));

        if (nation.CreditConfidence < defines.DeskCreditShould)
            items.Add(new DeskItem("credit", DeskLens.Economy, DeskSeverity.Should,
                Loc.Get("Lenders are stepping back"), Fmt.Percent(nation.CreditConfidence), "Economy", ""));

        var ratio = nation.PublicDebt / MathF.Max(0.01f, nation.Gdp);
        if (ratio > 1f)
            items.Add(new DeskItem("debt_ratio", DeskLens.Economy, DeskSeverity.Should,
                Loc.Get("The debt is larger than the economy"), Fmt.Percent(ratio), "Economy", ""));

        if (nation.WeeklyTaxIncome > 0f && nation.WeeklyExpenses > nation.WeeklyTaxIncome * 1.2f)
            items.Add(new DeskItem("overspend", DeskLens.Economy, DeskSeverity.Could,
                Loc.Get("The budget is spending a fifth more than it raises"),
                Loc.Format("{0} against {1}", Fmt.Money(nation.WeeklyExpenses), Fmt.Money(nation.WeeklyTaxIncome)),
                "Economy", ""));
    }

    private static void Society(Campaign campaign, Nation nation, List<DeskItem> items)
    {
        var defines = campaign.Defines;
        var trend = Trend(campaign, nation, sample => sample.Stability - nation.Stability);
        var severity = nation.Stability < defines.DeskStabilityMust ? DeskSeverity.Must
            : nation.Stability < defines.DeskStabilityShould ? DeskSeverity.Should : DeskSeverity.Watch;
        if (severity != DeskSeverity.Watch)
            items.Add(new DeskItem("stability", DeskLens.Society, severity,
                Loc.Get("The government is losing the country"), Fmt.Percent(nation.Stability),
                "State Affairs", trend));

        var radicalism = AverageRadicalism(campaign, nation);
        if (radicalism > defines.DeskRadicalismShould)
            items.Add(new DeskItem("radicalism", DeskLens.Society, DeskSeverity.Should,
                Loc.Get("Radicalism is rising in the regions"), Fmt.Percent(radicalism),
                "State Affairs", ""));

        var livingTrend = Trend(campaign, nation, sample => sample.StandardOfLiving - nation.StandardOfLiving);
        if (livingTrend.Length > 0 && livingTrend.StartsWith('−'))
            items.Add(new DeskItem("living", DeskLens.Society, DeskSeverity.Could,
                Loc.Get("Living standards are falling"), Fmt.Percent(nation.StandardOfLiving),
                "State Affairs", livingTrend));

        if (nation.WarSupport < 0.25f && campaign.Wars.Any(war => !war.Ended && war.Involves(nation.Id)))
            items.Add(new DeskItem("war_support", DeskLens.Society, DeskSeverity.Should,
                Loc.Get("The country is tired of the war"), Fmt.Percent(nation.WarSupport), "Wars", ""));

        if (nation.WarSupport > 0.7f && campaign.Wars.Any(war => !war.Ended && war.Involves(nation.Id)))
            items.Add(new DeskItem("war_mood", DeskLens.Society, DeskSeverity.Watch,
                Loc.Get("The country is still behind the war"), Fmt.Percent(nation.WarSupport), "Wars", ""));
    }

    private static void Diplomacy(Campaign campaign, Nation nation, List<DeskItem> items)
    {
        var incoming = DiplomaticActs.Incoming(campaign, nation.Id);
        if (incoming > 0)
            items.Add(new DeskItem("papers", DeskLens.Diplomacy, DeskSeverity.Must,
                Loc.Format("{0} papers are waiting for an answer", incoming),
                Loc.Get("a government that does not answer has decided"), "Diplomacy", ""));

        var strained = 0;
        var rivals = 0;
        foreach (var relation in campaign.Relations.Values)
        {
            if (relation.A != nation.Id && relation.B != nation.Id) continue;
            if (relation.Stance == DiplomaticStance.Allied && relation.AllianceCohesion < 0.5f) strained++;
            if (relation.Stance == DiplomaticStance.Rival) rivals++;
        }
        if (strained > 0)
            items.Add(new DeskItem("alliances", DeskLens.Diplomacy, DeskSeverity.Could,
                Loc.Format("{0} alliances are strained", strained),
                Loc.Get("a friend who is not sure is a friend who is counting"), "Diplomacy", ""));
        if (rivals > 0)
            items.Add(new DeskItem("rivals", DeskLens.Diplomacy, DeskSeverity.Could,
                Loc.Format("{0} neighbours count us as a rival", rivals),
                Loc.Get("their plans are written against ours"), "Diplomacy", ""));
    }

    private static void Cabinet(Campaign campaign, Nation nation, List<DeskItem> items)
    {
        var ready = 0;
        foreach (var decision in DecisionCatalog.All)
            if (!DecisionCatalog.IsBlocked(campaign, nation, decision)) ready++;
        if (ready > 0)
            items.Add(new DeskItem("decisions_ready", DeskLens.Cabinet, DeskSeverity.Could,
                Loc.Format("{0} decisions are ready to be taken", ready),
                Loc.Get("waiting is also a policy"), "Decisions", ""));
        if (nation.PoliticalPoints >= 10f)
            items.Add(new DeskItem("political_credit", DeskLens.Cabinet, DeskSeverity.Watch,
                Loc.Get("The government has credit to spend"), Fmt.Number(nation.PoliticalPoints), "Laws", ""));
    }

    /// <summary>
    /// What moved in the world this fortnight, read from the chronicle rather than guessed at.
    ///
    /// A desk that only watches its own country is a desk in a sealed room: the war next door,
    /// the alliance that ended, the crisis that opened are exactly what a minister is paid to
    /// notice before it arrives. Each fact is routed to the chair that would care about it, and
    /// carries the country it names so the reader can go and look at that country rather than at
    /// a list.
    /// </summary>
    private static void World(Campaign campaign, Nation nation, DeskLens lens, List<DeskItem> items)
    {
        var entries = campaign.Chronicle?.Entries;
        if (entries is null || entries.Count == 0) return;
        var window = Math.Max(1, campaign.Defines.DeskWorldDays);
        foreach (var entry in entries)
        {
            if (!entry.Public) continue;
            var age = campaign.ElapsedDays - entry.Day;
            if (age < 0 || age > window) continue;
            // Our own deeds are already read in the lenses proper; the world line is for others.
            var aboutUs = entry.ActorId == nation.Id || entry.TargetId == nation.Id;
            if (!TryRoute(entry.Kind, out var entryLens, out var severity)) continue;
            if (entryLens != lens) continue;
            var other = entry.ActorId == nation.Id ? entry.TargetId
                : entry.TargetId == nation.Id ? entry.ActorId
                : entry.TargetId >= 0 ? entry.TargetId : entry.ActorId;
            var title = Loc.Format(WorldTitle(entry.Kind),
                entry.ActorName.Length > 0 ? Loc.Get(entry.ActorName) : Loc.Get("somebody"),
                entry.TargetName.Length > 0 ? Loc.Get(entry.TargetName) : Loc.Get("somebody"));
            var detail = Loc.Format("{0} days ago", Math.Max(0, age));
            items.Add(new DeskItem("world_" + entry.Sequence, entryLens,
                aboutUs ? DeskSeverity.Could : severity, title, detail, "Diplomacy", "",
                other >= 0 && other != nation.Id ? other : -1));
        }
    }

    /// <summary>Which chair a kind of news belongs to, and how loud it is from a distance.</summary>
    internal static bool TryRoute(string kind, out DeskLens lens, out DeskSeverity severity)
    {
        switch (kind)
        {
            case "war_started" or "world_war_began" or "border_war" or "nuclear_strike" or "nuclear_retaliation":
                lens = DeskLens.Security; severity = DeskSeverity.Should; return true;
            case "nuclear_test" or "nuclear_incident" or "red_telephone":
                lens = DeskLens.Security; severity = DeskSeverity.Watch; return true;
            case "great_power_intervened" or "military_exercises_conducted":
                lens = DeskLens.Security; severity = DeskSeverity.Watch; return true;
            case "alliance_ended" or "alliance_strained" or "crisis_opened" or "crisis_compromise" or
                 "talks_opened" or "denunciation" or "spy_network_exposed" or "proxy_started" or
                 "peace_signed" or "country_released" or "deal_signed" or "organization_founded":
                lens = DeskLens.Diplomacy; severity = DeskSeverity.Could; return true;
            case "general_strike" or "political_scandal" or "anti_war_protests" or
                 "civil_war_escalated" or "coup_plot" or "regime_change":
                lens = DeskLens.Society; severity = DeskSeverity.Watch; return true;
            case "sovereign_default" or "sovereign_insolvency" or "debt_restructured":
                lens = DeskLens.Economy; severity = DeskSeverity.Could; return true;
            default:
                lens = DeskLens.Cabinet; severity = DeskSeverity.Watch; return false;
        }
    }

    /// <summary>The sentence a kind of news is told in. A kind with no sentence is not shown.</summary>
    private static string WorldTitle(string kind) => kind switch
    {
        "war_started" => "War: {0} against {1}",
        "world_war_began" => "The world war has begun: {0} against {1}",
        "border_war" => "Shots on a border: {0} and {1}",
        "nuclear_strike" => "A nuclear weapon has been used: {0} against {1}",
        "nuclear_retaliation" => "A nuclear strike answered: {0} against {1}",
        "nuclear_test" => "A nuclear test: {0}",
        "nuclear_incident" => "A nuclear incident: {0} and {1}",
        "red_telephone" => "The red telephone: {0} and {1}",
        "great_power_intervened" => "A great power has intervened: {0} over {1}",
        "military_exercises_conducted" => "Exercises near our interests: {0}",
        "alliance_ended" => "{0} has left its alliance with {1}",
        "alliance_strained" => "An alliance is strained: {0} and {1}",
        "crisis_opened" => "A crisis is opening: {0} and {1}",
        "crisis_compromise" => "A crisis was settled by compromise: {0} and {1}",
        "talks_opened" => "Talks have opened: {0} and {1}",
        "denunciation" => "{0} has denounced {1}",
        "spy_network_exposed" => "A spy network was exposed: {0} in {1}",
        "proxy_started" => "A proxy war has started: {0} behind {1}",
        "peace_signed" => "Peace signed: {0} and {1}",
        "country_released" => "{0} has released {1}",
        "deal_signed" => "A deal was signed: {0} and {1}",
        "organization_founded" => "A new organization: {0}",
        "general_strike" => "A general strike in {0}",
        "political_scandal" => "A political scandal in {0}",
        "anti_war_protests" => "Anti-war protests in {0}",
        "civil_war_escalated" => "A civil war is escalating in {0}",
        "coup_plot" => "A coup plot in {0}",
        "regime_change" => "The regime has changed in {0}",
        "sovereign_default" => "{0} has defaulted",
        "sovereign_insolvency" => "{0} is insolvent",
        "debt_restructured" => "{0} has restructured its debt",
        _ => "{0}"
    };

    /// <summary>
    /// The movement of a figure over the trend window, as the sentence a desk shows. Empty when
    /// there is not enough history yet: a campaign in its first month must not invent a past.
    /// </summary>
    private static string Trend(Campaign campaign, Nation nation, Func<NationHistorySample, float> change)
    {
        var samples = campaign.History?.ForNation(nation.Id);
        if (samples is null || samples.Count == 0) return "";
        var cutoff = campaign.ElapsedDays - Math.Max(7, campaign.Defines.DeskTrendDays);
        NationHistorySample? oldest = null;
        foreach (var sample in samples)
            if (sample.Day <= cutoff) oldest = sample;
        if (oldest is null) return "";
        var delta = change(oldest.Value);
        if (MathF.Abs(delta) < 0.001f) return "";
        var weeks = Math.Max(1, (campaign.ElapsedDays - oldest.Value.Day) / 7);
        return Loc.Format("{0}{1} in {2} weeks", delta > 0f ? "+" : "−",
            MathF.Abs(delta).ToString("0.000"), weeks);
    }

    private static float AverageRadicalism(Campaign campaign, Nation nation)
    {
        var total = 0f;
        var count = 0;
        foreach (var state in campaign.StatesOf(nation))
        {
            total += state.Radicalism;
            count++;
        }
        return count == 0 ? 0f : total / count;
    }
}
