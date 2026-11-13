using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// Proxy warfare: great powers arming, funding and advising the sides of wars they are
/// not themselves fighting — the Cold War's answer to the nuclear wall.
///
/// ═══════════════════════════════════════════════════════════════════════════════
/// FOR MODDERS — the five levels of support and how the system works:
/// ═══════════════════════════════════════════════════════════════════════════════
///
/// A SPONSORSHIP is one country backing one side of one war at a level:
///
///   1  Advisers    — a handful of officers and money for the client.
///   2  Weapons     — advisers plus arms and ammunition from the sponsor's depots.
///   3  At scale    — the sponsor ships a real share of its own depot intake.
///   4  Volunteers  — "vacationing" citizens arrive as trained men.
///   5  Expedition  — the sponsor's own formations, deniably.
///
/// WHAT ARRIVES: money (ProxyMoneyShare of the cost), equipment (weapons and
/// ammunition from the sponsor's StrategicStockpiles — a war fought on foreign
/// shells runs dry when the shells stop), and at higher levels, volunteers.
///
/// WHAT IT COSTS: ProxyCostShare of the sponsor's GDP a week, at least
/// ProxyMinimumCost. Unpaid for ProxyUnpaidWeeks and the support lapses.
///
/// THE AI decides: which wars to back (ProxyAiInterest — how interested it must
/// be in the outcome), when to stop (ProxyAiStopInterest), and how many backers
/// one side may have (ProxyAiBackersPerSide — no world war by proxy accident).
///
/// TO CHANGE THE WORLD:
///   • Make proxy wars cheaper → lower ProxyCostShare and ProxyMinimumCost.
///   • Make sponsors more cautious → raise ProxyAiInterest and lower the rebel cap.
///   • Let the world flood a rebellion with weapons → raise ProxyAiBackersPerSide.
///   • Cut off all foreign backing → set ProxyAiMinimumGdp to 0 and ProxyAiInterest to 10.
/// ═══════════════════════════════════════════════════════════════════════════════
/// </summary>
public sealed partial class Defines
{
    /// <summary>How often sponsorships deliver, in days.</summary>
    public int ProxyReviewDays { get; set; } = 7;
    /// <summary>What a level of support costs the sponsor a week, as a share of its GDP, and at least.</summary>
    public float ProxyCostShare { get; set; } = 0.000012f;
    public float ProxyMinimumCost { get; set; } = 0.01f;
    /// <summary>The share of it that arrives as money; the rest arrives as weapons.</summary>
    public float ProxyMoneyShare { get; set; } = 0.35f;
    /// <summary>What it costs, in billions, to bring one division from no equipment to full.</summary>
    public float ProxyEquipmentValue { get; set; } = 0.60f;
    /// <summary>
    /// The share of its own depots' daily intake of ammunition and of arms a backer ships to
    /// its client, for each level of support above the first (advisers and money send none):
    /// a quarter at weapons, half at weapons at scale.
    /// Without it a war fought on foreign shells ran dry in four months whatever its backers
    /// sent (StrategicStockpiles.Ship).
    /// </summary>
    public float ProxyShipmentShare { get; set; } = 0.25f;
    /// <summary>Volunteers a week at the highest level, as a share of the sponsor's people, and at most.</summary>
    public float ProxyVolunteerShare { get; set; } = 0.0000015f;
    public float ProxyVolunteersCap { get; set; } = 1500f;
    /// <summary>Weeks a sponsor may fail to pay before the support lapses.</summary>
    public int ProxyUnpaidWeeks { get; set; } = 6;
    /// <summary>The AI: how often each country looks at the wars it might back, how wealthy it must be, and the case it needs.</summary>
    public int ProxyAiReviewDays { get; set; } = 30;
    public float ProxyAiMinimumGdp { get; set; } = 60f;
    public float ProxyAiInterest { get; set; } = 1.35f;
    public float ProxyAiStopInterest { get; set; } = 0.45f;
    /// <summary>How long a backing the bookmark opens with is kept whatever the computer thinks of it.</summary>
    public int ProxyHistoricCommitmentDays { get; set; } = 730;
    public float ProxyAiGdpPerSponsorship { get; set; } = 1500f;
    /// <summary>The most backers the AI lets one side of a war have.</summary>
    public int ProxyAiBackersPerSide { get; set; } = 6;
    /// <summary>
    /// How much less the computer wants to arm a rebellion inside another state, and how much
    /// less again for each share of its own strength that state has; the highest level it backs
    /// rebels at on its own initiative.
    /// </summary>
    public float ProxyRebelReluctance { get; set; } = 0.6f;
    public float ProxyRebelGreatPowerFear { get; set; } = 0.8f;
    public int ProxyRebelMaximumLevel { get; set; } = 1;
    /// <summary>
    /// What it adds to a sponsor's interest that the side it might arm governs by its own
    /// ideology, professes its declared faith, or is its own people - each only when the enemy
    /// does not share it (ProxyWars.Kinship). 0 turns that tie off.
    /// </summary>
    public float ProxyCreedInterest { get; set; } = 0.35f;
    public float ProxyFaithInterest { get; set; } = 0.30f;
    public float ProxyKinInterest { get; set; } = 0.45f;
}

/// <summary>One country backing one side of somebody else's war, without fighting in it.</summary>
public sealed class Sponsorship
{
    public int Id;
    public int SponsorId;
    public int ClientId;
    /// <summary>The war it is for, or -1 for standing patronage in peacetime.</summary>
    public int WarId = -1;
    /// <summary>1 advisers and money, 2 weapons, 3 weapons at scale and volunteers.</summary>
    public int Level = 1;
    public int StartedDay;
    public int LastPaidDay = -1;
    /// <summary>Billions sent so far, and last week.</summary>
    public float Delivered;
    public float LastWeekCost;
    /// <summary>Ammunition and arms shipped from the backer's depots at the last delivery, for the reports.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public float LastShipment;
    public int UnpaidWeeks;
    /// <summary>
    /// The day before which the computer does not withdraw it for want of interest: a backing
    /// history wrote is a policy, not a mood. The Emirates arming the Rapid Support Forces was
    /// dropped in the first month because the opinions the bookmark opens with did not add up
    /// to it - which is a fact about the opinion table, not about Abu Dhabi.
    /// </summary>
    public int CommittedUntilDay = -1;
    /// <summary>
    /// Promised and not yet sent: nothing arrives before this day. The American package for
    /// Ukraine was stuck in Congress from the autumn of 2023 to 24 April 2024, and the winter
    /// of Avdiivka was fought on what was left.
    /// </summary>
    public int HeldUntilDay = -1;
    public bool Ended;
    public int EndedDay = -1;
    public string EndReason = "";
}

public sealed partial class Campaign
{
    public readonly List<Sponsorship> Sponsorships = new();
    public int NextSponsorshipId = 1;
}

/// <summary>
/// Wars fought through somebody else.
///
/// Every war of the real world in 2024 had backers who were not in it: the countries arming
/// Ukraine and the ones arming Russia, the patrons of both sides in Sudan, Yemen, Syria, Libya
/// and Myanmar. The game had none of it. A country could only be in a war or out of it, so a
/// great power with an interest in somebody else's war had nothing to do but declare one of
/// its own or watch.
///
/// A sponsorship is the third way. The sponsor pays every week - a level of support priced as
/// a share of its own economy - and the client receives part of it as money, the rest as
/// weapons that re-equip its divisions; from the second level on it also ships munitions out
/// of its own depots, and at the highest level volunteers. The client wears
/// the backing as a modifier on its army. The sponsor is not at war with anybody, but the
/// country it is arming against knows, and the tension between them rises (Escalation); two
/// powers arming opposite sides of one war are a confrontation, and it shows on both of them.
///
/// The AI backs the side it likes against the side it does not, more readily when a rival of
/// its own is backing the other side, when the war is next door, or when the client is its
/// ally or dependency - and stops when the case fades or the money runs out.
/// </summary>
public static class ProxyWars
{
    public static IEnumerable<Sponsorship> Active(Campaign c) =>
        c?.Sponsorships.Where(s => !s.Ended) ?? Enumerable.Empty<Sponsorship>();

    public static IEnumerable<Sponsorship> For(Campaign c, StrategicWar war) =>
        Active(c).Where(s => s.WarId == war.Id);

    /// <summary>Who is backing which side of this war.</summary>
    public static IEnumerable<Sponsorship> Backing(Campaign c, StrategicWar war, bool attackers) =>
        For(c, war).Where(s => war.IsAttacking(s.ClientId) == attackers);

    /// <summary>Two sides each with a backer: a confrontation of the backers through the war.</summary>
    public static bool IsConfrontation(Campaign c, StrategicWar war) =>
        war is not null && Backing(c, war, true).Any() && Backing(c, war, false).Any();

    /// <summary>
    /// How much these two countries are on opposite sides of other people's wars, 0 to 3: one
    /// arming a side the other is fighting on, or each arming one side of the same war.
    /// </summary>
    public static float Opposition(Campaign c, Nation a, Nation b)
    {
        if (c is null || a is null || b is null || c.Sponsorships.Count == 0) return 0f;
        var count = 0;
        foreach (var s in c.Sponsorships)
        {
            if (s.Ended || s.WarId < 0 || (s.SponsorId != a.Id && s.SponsorId != b.Id)) continue;
            var war = WarById(c, s.WarId);
            if (war is null || war.Ended) continue;
            var other = s.SponsorId == a.Id ? b : a;
            var clientAttacking = war.IsAttacking(s.ClientId);
            if (war.Side(!clientAttacking).Contains(other.Id)) { count++; continue; }
            foreach (var rival in c.Sponsorships)
                if (!rival.Ended && rival.WarId == s.WarId && rival.SponsorId == other.Id &&
                    war.IsAttacking(rival.ClientId) != clientAttacking && s.SponsorId == a.Id)
                { count++; break; }
        }
        return Math.Min(3, count);
    }

    private static StrategicWar WarById(Campaign c, int id)
    {
        foreach (var war in c.Wars)
            if (war.Id == id) return war;
        return null;
    }

    /// <summary>What a level of support costs this sponsor a week.</summary>
    public static float WeeklyCost(Campaign c, Nation sponsor, int level) =>
        sponsor is null ? 0f : MathF.Max(c.Defines.ProxyMinimumCost * level, sponsor.Gdp * c.Defines.ProxyCostShare * level);

    /// <summary>The most a country of this size can give: a small economy cannot ship armies.</summary>
    public static int MaximumLevel(Nation sponsor) =>
        sponsor is null ? 0 : sponsor.Gdp < 300f ? 1 : sponsor.Gdp < 1500f ? 2 : 3;

    /// <summary>Why this country cannot back that side of that war at that level, or "".</summary>
    public static string Block(Campaign c, Nation sponsor, StrategicWar war, Nation client, int level)
    {
        if (c is null || sponsor is null || client is null || war is null) return Loc.Get("Choose a war and a side.");
        if (!MechanicFeatures.SystemEnabled(c, nameof(ProxyWars))) return Loc.Get("Proxy wars are disabled.");
        if (war.Ended) return Loc.Get("That war is over.");
        if (!war.Involves(client.Id)) return Loc.Get("They are not fighting in that war.");
        if (war.Involves(sponsor.Id)) return Loc.Get("You are fighting in that war yourself.");
        if (sponsor.Id == client.Id) return Loc.Get("Choose another country.");
        if (sponsor.Movement is not null) return Loc.Get("A movement cannot arm a country.");
        if (CampaignDiplomacy.AtWar(c, sponsor.Id, client.Id)) return Loc.Get("You are at war with them.");
        if (level < 1 || level > 3) return Loc.Get("Choose a level of support.");
        if (level > MaximumLevel(sponsor)) return Loc.Get("Your economy is too small for that level of support.");
        if (sponsor.Treasury < WeeklyCost(c, sponsor, level) * 4f) return Loc.Get("Not enough money in the treasury.");
        if (Active(c).Any(s => s.SponsorId == sponsor.Id && s.WarId == war.Id && war.IsAttacking(s.ClientId) != war.IsAttacking(client.Id)))
            return Loc.Get("You are already backing the other side.");
        return "";
    }

    /// <summary>Back a side, or change how much; level 0 stops. Returns whether anything changed.</summary>
    public static bool Sponsor(Campaign c, Nation sponsor, StrategicWar war, Nation client, int level)
    {
        if (c is null || sponsor is null || client is null || war is null) return false;
        var existing = Active(c).FirstOrDefault(s => s.SponsorId == sponsor.Id && s.WarId == war.Id && s.ClientId == client.Id);
        if (level <= 0)
        {
            if (existing is null) return false;
            End(c, existing, "withdrawn");
            return true;
        }
        if (Block(c, sponsor, war, client, level).Length > 0) return false;
        if (existing is not null)
        {
            existing.Level = level;
            return true;
        }
        var s = new Sponsorship
        {
            Id = c.NextSponsorshipId++, SponsorId = sponsor.Id, ClientId = client.Id, WarId = war.Id,
            Level = level, StartedDay = c.ElapsedDays
        };
        c.Sponsorships.Add(s);
        OpinionSystem.Remember(c, CampaignDiplomacy.Get(c, sponsor.Id, client.Id), "armed_us");
        foreach (var enemyId in war.EnemiesOf(client.Id))
            if (enemyId != sponsor.Id && CampaignDiplomacy.Get(c, sponsor.Id, enemyId) is { } hostile)
                OpinionSystem.Remember(c, hostile, "armed_our_enemy");
        c.Chronicle.Record(c, "proxy_started", sponsor.Id, client.Id, war.Name);
        c.MarkStrategicChange();
        return true;
    }

    private static void End(Campaign c, Sponsorship s, string reason)
    {
        s.Ended = true;
        s.EndedDay = c.ElapsedDays;
        s.EndReason = reason;
        if (reason is "withdrawn" or "lost interest" or "could not pay")
            c.Chronicle.Record(c, "proxy_ended", s.SponsorId, s.ClientId, WarById(c, s.WarId)?.Name ?? "");
    }

    public static void Tick(Campaign c)
    {
        foreach (var _ in TickStages(c)) { }
    }

    /// <summary>The same proxy-war pass, yielded between its phases.</summary>
    public static IEnumerable<int> TickStages(Campaign c)
    {
        if (c is null || c.ElapsedDays <= 0) yield break;
        ArmsShipmentRequests.Tick(c);
        if (!MechanicFeatures.SystemEnabled(c, nameof(ProxyWars))) yield break;
        var defines = c.Defines;
        var every = Math.Max(1, defines.ProxyReviewDays);
        if (c.ElapsedDays % every == 4 % every)
            foreach (var _ in DeliverStages(c, every)) yield return 0;
        yield return 0;
        foreach (var _ in ConsiderStages(c)) yield return 0;
        yield return 0;
        // A finished record is history, not state: keep the last few hundred.
        if (c.Sponsorships.Count > 400)
            c.Sponsorships.RemoveAll(s => s.Ended && c.ElapsedDays - s.EndedDay > 3650);
    }

    private static IEnumerable<int> DeliverStages(Campaign c, int days)
    {
        var defines = c.Defines;
        var arms = new Dictionary<int, float>();
        var backing = new Dictionary<int, int>();
        var delivered = 0;
        foreach (var s in c.Sponsorships)
        {
            if (++delivered % 64 == 0) yield return 0;
            if (s.Ended) continue;
            var sponsor = c.NationById(s.SponsorId);
            var client = c.NationById(s.ClientId);
            var war = s.WarId >= 0 ? WarById(c, s.WarId) : null;
            if (sponsor is null || client is null || sponsor.Population <= 0 || client.Population <= 0 && client.Movement is null)
            { End(c, s, "gone"); continue; }
            if (s.WarId >= 0 && (war is null || war.Ended)) { End(c, s, "war over"); continue; }
            if (s.HeldUntilDay > c.ElapsedDays) continue;
            if (war is not null && !war.Involves(client.Id)) { End(c, s, "client out"); continue; }
            if (war is not null && war.Involves(sponsor.Id)) { End(c, s, "joined the war"); continue; }
            if (CampaignDiplomacy.AtWar(c, sponsor.Id, client.Id)) { End(c, s, "at war"); continue; }

            var cost = WeeklyCost(c, sponsor, s.Level) * days / 7f;
            if (sponsor.Treasury < cost)
            {
                if (++s.UnpaidWeeks >= defines.ProxyUnpaidWeeks) End(c, s, "could not pay");
                continue;
            }
            s.UnpaidWeeks = 0;
            sponsor.Treasury -= cost;
            s.Delivered += cost;
            s.LastWeekCost = cost;
            s.LastPaidDay = c.ElapsedDays;
            client.Treasury += cost * defines.ProxyMoneyShare;
            arms[client.Id] = arms.GetValueOrDefault(client.Id) + cost * (1f - defines.ProxyMoneyShare);
            backing[client.Id] = backing.GetValueOrDefault(client.Id) + s.Level;
            // And from the backer's own depots, the shells and the guns themselves.
            s.LastShipment = s.Level >= 2
                ? StrategicStockpiles.Ship(c, sponsor, client, defines.ProxyShipmentShare * (s.Level - 1), days)
                : 0f;
            if (s.Level >= 3)
                client.ManpowerReserve += MathF.Min(defines.ProxyVolunteersCap, sponsor.Population * defines.ProxyVolunteerShare) * days / 7f;
        }

        foreach (var (clientId, value) in arms) Equip(c, clientId, value);
        foreach (var (clientId, levels) in backing)
        {
            var client = c.NationById(clientId);
            if (client is null) continue;
            var tier = levels >= 5 ? 3 : levels >= 2 ? 2 : 1;
            ModifierSystem.Grant(c, client, "foreign_backing_" + tier, days * 2);
        }
    }

    /// <summary>Weapons go to the divisions that have least, until the delivery is spent.</summary>
    private static void Equip(Campaign c, int clientId, float value)
    {
        var defines = c.Defines;
        var budget = value / MathF.Max(0.01f, defines.ProxyEquipmentValue);
        if (budget <= 0f) return;
        var armies = c.Armies.Where(a => a.NationId == clientId && a.Equipment < 0.999f)
            .OrderBy(a => a.Equipment).ThenBy(a => a.Id).ToList();
        foreach (var army in armies)
        {
            if (budget <= 0f) break;
            var units = MathF.Max(1f, army.Divisions);
            var need = (1f - army.Equipment) * units;
            var give = MathF.Min(need, budget);
            army.Equipment = MathF.Min(1f, army.Equipment + give / units);
            budget -= give;
        }
    }

    // ------------------------------------------------------------------------- the AI

    /// <summary>Each country nobody plays, on its own day of the month, looks at the wars it could back.</summary>
    private static IEnumerable<int> ConsiderStages(Campaign c)
    {
        var defines = c.Defines;
        var cycle = Math.Max(1, defines.ProxyAiReviewDays);
        var live = c.Wars.Where(w => !w.Ended).ToArray();
        var ticked = 0;
        foreach (var sponsor in c.Nations)
        {
            if (++ticked % 8 == 0) yield return 0;
            if ((sponsor.Id + c.ElapsedDays) % cycle != 0) continue;
            if (sponsor.IsHuman || sponsor.Movement is not null || sponsor.Population <= 0) continue;
            var mine = Active(c).Where(s => s.SponsorId == sponsor.Id).ToList();

            // First whether it still wants what it is paying for.
            foreach (var s in mine)
            {
                var war = WarById(c, s.WarId);
                var client = c.NationById(s.ClientId);
                if (war is null || client is null) continue;
                var enemy = c.NationById(war.EnemyLeaderOf(client.Id));
                if (enemy is null) continue;
                if (c.ElapsedDays < s.CommittedUntilDay) continue;
                if (Interest(c, sponsor, client, enemy, war) < defines.ProxyAiStopInterest) End(c, s, "lost interest");
            }

            if (sponsor.Gdp < defines.ProxyAiMinimumGdp || sponsor.Stability < 0.3f) continue;
            if (live.Any(w => w.Involves(sponsor.Id) && w.Intensity >= 1)) continue;
            var limit = 1 + (int)(sponsor.Gdp / MathF.Max(1f, defines.ProxyAiGdpPerSponsorship));
            if (mine.Count(s => !s.Ended) >= Math.Min(5, limit)) continue;

            StrategicWar bestWar = null;
            Nation bestClient = null;
            var bestInterest = defines.ProxyAiInterest;
            foreach (var war in live)
            {
                if (war.Involves(sponsor.Id) || mine.Any(s => !s.Ended && s.WarId == war.Id)) continue;
                if (c.ElapsedDays - war.StartedDay < 14) continue;
                foreach (var attackers in new[] { true, false })
                {
                    var client = c.NationById(attackers ? war.AttackerId : war.DefenderId);
                    var enemy = c.NationById(attackers ? war.DefenderId : war.AttackerId);
                    if (client is null || enemy is null || client.Movement is not null && client.Population <= 0) continue;
                    if (Backing(c, war, attackers).Count() >= defines.ProxyAiBackersPerSide) continue;
                    if (!HasStake(c, sponsor, client, enemy, war)) continue;
                    var interest = Interest(c, sponsor, client, enemy, war);
                    if (interest <= bestInterest) continue;
                    if (Block(c, sponsor, war, client, 1).Length > 0) continue;
                    bestInterest = interest;
                    bestWar = war;
                    bestClient = client;
                }
            }
            if (bestWar is null) continue;
            var level = Math.Min(MaximumLevel(sponsor), bestInterest >= 2.1f ? 3 : bestInterest >= 1.5f ? 2 : 1);
            // Rebels are armed quietly: advisers and money, never weapons at scale by the
            // computer's own choice (a backing history wrote is another matter).
            if (IsRebel(bestClient, c.NationById(bestWar.EnemyLeaderOf(bestClient.Id))))
                level = Math.Min(level, Math.Max(1, c.Defines.ProxyRebelMaximumLevel));
            while (level > 1 && Block(c, sponsor, bestWar, bestClient, level).Length > 0) level--;
            Sponsor(c, sponsor, bestWar, bestClient, level);
        }
    }

    /// <summary>
    /// Whether the war is any of this country's business: it is next door, the client is its ally,
    /// its dependency or under its guarantee, the enemy is its rival or in a crisis with it, a rival
    /// of its own backs the enemy - or it is a great power, whose business is everywhere.
    /// </summary>
    public static bool HasStake(Campaign c, Nation sponsor, Nation client, Nation enemy, StrategicWar war)
    {
        if (sponsor.Gdp >= 2000f) return true;
        if (client.AnswersTo(sponsor.Id)) return true;
        if (CampaignDiplomacy.SharesLandBorder(c, sponsor.MapIndex, client.MapIndex) ||
            CampaignDiplomacy.SharesLandBorder(c, sponsor.MapIndex, enemy.MapIndex)) return true;
        // An alliance is a promise to defend: it makes an ally's defensive war one's business,
        // and an ally's war of conquest nobody's.
        var toClient = CampaignDiplomacy.Peek(c, sponsor.Id, client.Id);
        if (war.IsDefending(client.Id) && toClient is not null &&
            (toClient.Stance == DiplomaticStance.Allied || toClient.GuaranteeByA || toClient.GuaranteeByB)) return true;
        var toEnemy = CampaignDiplomacy.Peek(c, sponsor.Id, enemy.Id);
        if (toEnemy is not null && (toEnemy.Stance == DiplomaticStance.Rival || toEnemy.Tension >= c.Defines.TensionCrisisAt)) return true;
        foreach (var s in Backing(c, war, !war.IsAttacking(client.Id)))
            if (CampaignDiplomacy.Peek(c, sponsor.Id, s.SponsorId) is { Stance: DiplomaticStance.Rival }) return true;
        return false;
    }

    /// <summary>
    /// What a shared creed, faith or people adds to a sponsor's wish to see a side win, when the
    /// other side does not share it: Iran and the Shia militias, Moscow and the Bolsheviks'
    /// heirs, Armenia and Karabakh, Russia and the Donbas, Turkey and the Turkmen of Syria. A
    /// state where a creed rules backs the people who fight for that creed elsewhere - more so
    /// when the side is a minority rising or a revolution inside a state of another creed,
    /// because that is the war in which nobody else will. Read off what both sides are right
    /// now, the same for every country; a sponsor sharing the creed with both sides gains nothing.
    /// </summary>
    public static float Kinship(Campaign c, Nation sponsor, Nation client, Nation enemy)
    {
        if (c is null || sponsor is null || client is null || enemy is null) return 0f;
        var d = c.Defines;
        var bonus = 0f;
        if (!string.IsNullOrEmpty(sponsor.Ideology) &&
            string.Equals(sponsor.Ideology, client.Ideology, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(sponsor.Ideology, enemy.Ideology, StringComparison.OrdinalIgnoreCase))
            bonus += d.ProxyCreedInterest;
        if (StateIdentity.ShareDeclaredFaith(sponsor, client) && !StateIdentity.ShareDeclaredFaith(sponsor, enemy))
            bonus += d.ProxyFaithInterest;
        var ours = DemographyCatalog.PrimaryCulture(sponsor.Tag);
        var theirs = client.FoundedCultures.Count > 0 ? client.FoundedCultures[0] : DemographyCatalog.PrimaryCulture(client.Tag);
        var enemies = enemy.FoundedCultures.Count > 0 ? enemy.FoundedCultures[0] : DemographyCatalog.PrimaryCulture(enemy.Tag);
        if (!string.IsNullOrEmpty(ours) && string.Equals(ours, theirs, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(ours, enemies, StringComparison.OrdinalIgnoreCase))
            bonus += d.ProxyKinInterest;
        return float.IsFinite(bonus) ? bonus : 0f;
    }

    /// <summary>How much this sponsor wants that client to beat that enemy.</summary>
    /// <summary>Whether this side of a war is a rebellion against that state: a breakaway, a rising, a faction.</summary>
    public static bool IsRebel(Nation client, Nation state) =>
        client is not null && state is not null &&
        (client.SecededFromId == state.Id || client.Movement is not null || client.FactionId.Length > 0);

    public static float Interest(Campaign c, Nation sponsor, Nation client, Nation enemy, StrategicWar war)
    {
        if (sponsor is null || client is null || enemy is null) return 0f;
        var toClient = CampaignDiplomacy.Peek(c, sponsor.Id, client.Id);
        var toEnemy = CampaignDiplomacy.Peek(c, sponsor.Id, enemy.Id);
        if (toEnemy is { Stance: DiplomaticStance.Allied } || enemy.AnswersTo(sponsor.Id)) return -1f;
        if (Factions.ListedBy(client, sponsor)) return -1f;
        var interest = M.Clamp(((toClient?.Opinion ?? 0f) - (toEnemy?.Opinion ?? 0f)) / 100f, -1f, 1f) * 0.7f;
        interest += (toEnemy?.Tension ?? 0f) / 100f * 0.8f;
        var defending = war.IsDefending(client.Id);
        if (toClient is { Stance: DiplomaticStance.Allied }) interest += defending ? 0.6f : 0.1f;
        if (defending && toClient is not null && (toClient.GuaranteeByA || toClient.GuaranteeByB)) interest += 0.4f;
        // Arming an aggression costs a reputation that arming its victim does not.
        if (!defending && !war.CivilConflict) interest -= 0.3f;
        // And arming a rebellion inside somebody else's state is an act of war in all but name,
        // the more so the stronger that state: the West armed Ukraine, not Chechen separatists,
        // and the computer was paying $42 billion a year to an uprising in Dagestan.
        if (IsRebel(client, enemy))
        {
            interest -= c.Defines.ProxyRebelReluctance;
            var theirs = AiStatecraft.CachedStrength(c, enemy);
            var ours = MathF.Max(1f, AiStatecraft.CachedStrength(c, sponsor));
            interest -= c.Defines.ProxyRebelGreatPowerFear * M.Clamp01(theirs / ours);
        }
        if (client.AnswersTo(sponsor.Id)) interest += 1.0f;
        if (toEnemy is { Stance: DiplomaticStance.Rival }) interest += 0.5f;

        // A rival of ours is backing the other side: the war has become ours by proxy.
        foreach (var s in Backing(c, war, !war.IsAttacking(client.Id)))
        {
            if (s.SponsorId == sponsor.Id) continue;
            var withRival = CampaignDiplomacy.Peek(c, sponsor.Id, s.SponsorId);
            if (withRival is not null && (withRival.Stance == DiplomaticStance.Rival || withRival.Tension >= 50f || withRival.Opinion <= -40f))
            { interest += 0.4f; break; }
        }

        var ideology = PoliticsCore.IdeologicalDistance(sponsor, enemy) - PoliticsCore.IdeologicalDistance(sponsor, client);
        interest += ideology * (war.CivilConflict ? 0.6f : 0.3f);
        interest += Kinship(c, sponsor, client, enemy);

        var near = CampaignDiplomacy.SharesLandBorder(c, sponsor.MapIndex, client.MapIndex) ||
                   CampaignDiplomacy.SharesLandBorder(c, sponsor.MapIndex, enemy.MapIndex) ||
                   string.Equals(sponsor.Region, client.Region, StringComparison.OrdinalIgnoreCase);
        if (near) interest += 0.2f;
        else if (sponsor.Gdp < 2000f) interest -= 0.35f;

        // Fear: arming somebody against a much stronger neighbour of one's own.
        if (CampaignDiplomacy.SharesLandBorder(c, sponsor.MapIndex, enemy.MapIndex) && enemy.Gdp > sponsor.Gdp * 3f) interest -= 0.4f;
        return interest;
    }
}

/// <summary>Back a side of somebody else's war, or stop (Level 0).</summary>
public sealed class SponsorWarCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int WarId { get; init; }
    public int ClientId { get; init; }
    public int Level { get; init; }
    public bool Changed { get; private set; }

    public void Apply(Campaign c)
    {
        if (!MechanicFeatures.Allows(c, this)) return;
        var war = c.Wars.FirstOrDefault(w => w.Id == WarId);
        Changed = ProxyWars.Sponsor(c, c.NationById(IssuerId), war, c.NationById(ClientId), Level);
    }
}
