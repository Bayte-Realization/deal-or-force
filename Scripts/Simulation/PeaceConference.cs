using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// A peace a player writes, rather than one they pick off a menu.
///
/// The fixed terms in <see cref="PeaceTerms"/> are still there and still useful - a white
/// peace is a white peace. But every war they cannot express ends badly: a war fought for
/// two provinces has no button, and a war that took half a country has no way to keep the
/// half that matters and hand back the half that does not.
///
/// So a demand is a set of provinces the winner names, plus whatever else they want out of
/// it, and every part of it costs. War score is the budget. Taking ground you never
/// occupied costs several times what taking ground you hold does; taking the capital
/// costs more than taking a marsh; taking what you actually declared war over is cheaper
/// than taking what you did not. When the bill exceeds what the war was worth, the other
/// side says no - and says why, and by how much, so the answer is a negotiation rather
/// than a refusal.
///
/// This is the Paradox shape: a budget, priced demands, and a counterparty with a number.
/// </summary>
public sealed class PeaceDemand
{
    public int WarId = -1;
    public int ProposerId = -1;

    /// <summary>Provinces the proposer wants to keep, permanently.</summary>
    public readonly HashSet<int> Provinces = new();

    /// <summary>An indemnity scaled to the loser's economy.</summary>
    public bool Reparations;

    /// <summary>A compelled bilateral trade agreement.</summary>
    public bool TradeConcessions;

    /// <summary>The defeated armed forces give up equipment and cohesion.</summary>
    public bool Demilitarise;

    /// <summary>A public humbling: lasting war support and stability damage.</summary>
    public bool Humiliate;

    /// <summary>A long truce instead of the ordinary one.</summary>
    public bool LongTruce;

    /// <summary>
    /// The arrangement the loser is to answer under, by its id in `common/subjects`, or
    /// empty for none.
    ///
    /// A string and not a flag, because the kinds are data: the conference can demand
    /// whatever `common/subjects` declares, including a kind that did not exist when this
    /// was written.
    /// </summary>
    public string Subjugate = "";

    public bool IsEmpty => Provinces.Count == 0 && !Reparations && !TradeConcessions &&
                           !Demilitarise && !Humiliate && !LongTruce && Subjugate.Length == 0;
}

/// <summary>Pricing, acceptance and resolution for a written peace.</summary>
public static class PeaceConference
{
    /// <summary>What one province costs to keep, and why it costs that.</summary>
    public readonly record struct ProvinceCost(int Province, float Cost, string Reason);

    /// <summary>
    /// The war score a side may actually spend. A war nobody is winning buys nothing;
    /// exhaustion on the other side and the sheer length of the war both buy a little
    /// more, because a country that has been bleeding for three years signs things it
    /// would not have signed in the first month.
    /// </summary>
    public static float Budget(Campaign campaign, StrategicWar war, int proposerId)
    {
        if (war is null || war.Ended || !war.Involves(proposerId)) return 0f;
        var score = war.IsAttacking(proposerId) ? war.WarScore : -war.WarScore;
        var opponentSupport = war.IsAttacking(proposerId) ? war.DefenderWarSupport : war.AttackerWarSupport;
        var exhaustion = (1f - M.Clamp01(opponentSupport)) * 30f;
        var duration = M.Clamp((campaign.ElapsedDays - war.StartedDay) / 30f, 0f, 20f);
        return M.Max(0f, score + exhaustion + duration);
    }

    /// <summary>
    /// What making them answer to you is worth, in war score.
    ///
    /// The kind sets the base and their size against yours moves it, because the thing
    /// being bought is not an arrangement in the abstract - it is THIS country's obedience,
    /// and a country your own size does not hand that over for what a minnow charges.
    /// </summary>
    public static float SubjugationCost(Campaign campaign, Nation winner, Nation loser, string kindId)
    {
        var kind = SubjectCatalog.Of(kindId);
        if (kind is null || winner is null || loser is null) return 0f;
        var size = M.Clamp(Math.Max(1L, loser.Population) / (float)Math.Max(1L, winner.Population), 0f, 1.5f);
        return kind.DemandCost * (0.6f + 0.6f * size);
    }

    /// <summary>What one province costs. Builds a ledger; prefer a ledger for many.</summary>
    public static ProvinceCost Price(Campaign campaign, StrategicWar war, int proposerId, int province) =>
        new PeaceLedger(campaign, war, proposerId).Price(province);

    /// <summary>The whole bill. Builds a ledger; prefer a ledger for repeated costing.</summary>
    public static float Cost(Campaign campaign, StrategicWar war, PeaceDemand demand) =>
        demand is null || war is null ? 0f : new PeaceLedger(campaign, war, demand.ProposerId).Cost(demand);

    /// <summary>
    /// Why the other side will not sign this, in words a player can act on. An empty
    /// string means they will.
    ///
    /// In the player's language: the conference prints this as it comes, and it used to come
    /// in English whatever the table was set to - the one line on the screen that says what to
    /// do next.
    /// </summary>
    public static string BlockReason(Campaign campaign, StrategicWar war, PeaceDemand demand)
    {
        if (war is null || war.Ended) return Loc.Get("There is no war to end");
        if (demand is null || demand.IsEmpty) return Loc.Get("The demand is empty; offer a white peace instead");
        if (!war.Involves(demand.ProposerId)) return Loc.Get("You are not a belligerent in this war");

        var topology = campaign.ProvinceMap;
        if (topology is null) return Loc.Get("The map is not ready");

        var loser = campaign.NationById(war.EnemyLeaderOf(demand.ProposerId));
        var enemyOwners = war.EnemiesOf(demand.ProposerId).Select(campaign.NationById)
            .Where(nation => nation is not null).Select(nation => nation.MapIndex).ToHashSet();
        foreach (var province in demand.Provinces)
            if (province <= 0 || province > topology.ProvinceCount ||
                !enemyOwners.Contains(topology.ProvinceToNation[province]))
                return Loc.Get("The demand includes ground that is not theirs to give");

        var rounded = Round(campaign, war, demand);
        // A mod or a changed map may contain a region with more than one owner.
        // Rounding must not silently add a neutral or friendly country's ground.
        if (rounded.Provinces.Any(province => !enemyOwners.Contains(topology.ProvinceToNation[province])))
            return Loc.Get("The demand includes ground that is not theirs to give");

        if (demand.Subjugate.Length > 0)
        {
            if (!SubjectCatalog.Knows(demand.Subjugate))
                return Loc.Format("There is no such arrangement as '{0}'", demand.Subjugate);
            if (loser is null) return Loc.Get("There is nobody left to answer to you");
            if (loser.AnswersTo(demand.ProposerId))
                return Loc.Format("{0} already answers to you", loser.DisplayName);
            if (loser.IsSubject)
                return Loc.Format("{0} answers to somebody else; that has to end first", loser.DisplayName);
            // A country you have taken entirely is not a country that can answer to you.
            // The demand has to leave something standing for the arrangement to be about.
            if (!Enumerable.Range(1, topology.ProvinceCount).Any(province =>
                    topology.ProvinceToNation[province] == loser.MapIndex && !rounded.Provinces.Contains(province)))
                return Loc.Get("A country you are annexing whole cannot also answer to you");
        }

        var ledger = new PeaceLedger(campaign, war, demand.ProposerId);
        var budget = ledger.Budget;

        // Priced as it will be SIGNED, not as it was drawn.
        //
        // `Resolve` rounds a demand to whole regions before it applies it, and rounding up
        // adds ground. Pricing the unrounded version here meant the conference could quote a
        // number, say they would sign, and then refuse - because the thing actually put in
        // front of them was bigger than the thing that was priced. One rounding, used by
        // both.
        var cost = ledger.Cost(rounded);
        if (cost > budget)
            return Loc.Format("They will not sign: the demand is worth {0:0} and the war is worth {1:0}", cost, budget);
        return "";
    }

    /// <summary>
    /// The demand as it would actually be signed: rounded to whole regions.
    ///
    /// Returns a copy. `BlockReason` has to be able to price this without changing the
    /// demand the caller is still editing, and the peace screen calls it on every click.
    /// </summary>
    public static PeaceDemand Round(Campaign campaign, StrategicWar war, PeaceDemand demand)
    {
        if (demand is null) return null;
        var rounded = new PeaceDemand
        {
            WarId = demand.WarId,
            ProposerId = demand.ProposerId,
            Subjugate = demand.Subjugate,
            Reparations = demand.Reparations,
            LongTruce = demand.LongTruce,
            TradeConcessions = demand.TradeConcessions,
            Demilitarise = demand.Demilitarise,
            Humiliate = demand.Humiliate
        };
        var proposer = campaign?.NationById(demand.ProposerId);
        if (demand.Provinces.Count == 0 || proposer is null)
        {
            foreach (var province in demand.Provinces) rounded.Provinces.Add(province);
            return rounded;
        }
        foreach (var province in Tidy(campaign, war, demand.Provinces, proposer.MapIndex))
            rounded.Provinces.Add(province);
        return rounded;
    }

    /// <summary>
    /// Sign it. The provinces change owner through the ordinary annexation path, so every
    /// region the new border cuts is split exactly as it would be by any other border
    /// change - a written peace is not a special case of the map.
    ///
    /// Which is exactly why the rounding happens here as well as where the demand is
    /// written. Every treaty in the game is signed through this method - the one the AI
    /// wrote for itself, the one a player drew by hand, the one an ally's war handed over -
    /// and a rule enforced at the place each of them is written is a rule with as many holes
    /// as there are places. Enforced at the signature it has none.
    ///
    /// Before the price, not after: rounding a region up adds ground the winner was not
    /// standing on, and a demand priced before that is a demand that gets the difference
    /// free. Tidied first, then priced, then refused if the war was not worth it.
    /// </summary>
    public static bool Resolve(Campaign campaign, StrategicWar war, PeaceDemand demand)
    {
        // Validate before indexing provinces in Round, and leave a refused draft intact.
        var block = BlockReason(campaign, war, demand);
        if (war is null) return false;
        war.LastPeaceProposalDay = campaign.ElapsedDays;
        war.LastNegotiation = block.Length == 0 ? "Agreement reached" : block;
        if (block.Length > 0) return false;

        if (demand is not null && demand.Provinces.Count > 0)
        {
            var whole = Round(campaign, war, demand);
            demand.Provinces.Clear();
            foreach (var province in whole.Provinces) demand.Provinces.Add(province);
        }

        var winner = campaign.NationById(demand.ProposerId);
        var loser = campaign.NationById(war.EnemyLeaderOf(demand.ProposerId));
        if (winner is null || loser is null) return false;

        // Anything not being kept goes back to whoever held it when the war began.
        if (campaign.ProvinceController is not null)
            foreach (var pair in war.OriginalController)
                if (!demand.Provinces.Contains(pair.Key))
                    campaign.SetProvinceController(pair.Key, pair.Value);

        if (demand.Provinces.Count > 0)
        {
            var cededOwners = demand.Provinces.Select(province => campaign.ProvinceMap.ProvinceToNation[province]).ToHashSet();
            var created = StatePartition.Annex(campaign, demand.Provinces, winner);
            war.AnnexedProvinces = demand.Provinces.Count;
            war.CreatedStates = created.Count;

            // Losing ground is not a one-day event. The country goes on naming the land
            // for years, which is quieter than a humiliation and lasts longer.
            foreach (var formerOwner in campaign.Nations.Where(nation => cededOwners.Contains((ushort)nation.MapIndex)))
                ModifierSystem.Grant(campaign, formerOwner, "ceded_territory", 0);
        }

        if (demand.Reparations || demand.TradeConcessions)
        {
            var share = demand.Reparations ? 0.03f : 0.015f;
            var payment = MathF.Min(MathF.Max(0f, loser.Treasury), MathF.Max(0.10f, loser.Gdp * share));
            loser.Treasury -= payment;
            winner.Treasury += payment;
            war.Reparations = payment;
        }

        if (demand.TradeConcessions)
            CampaignDiplomacy.Get(campaign, winner.Id, loser.Id).TradeAgreement = true;

        if (demand.Demilitarise)
            foreach (var division in campaign.Armies.Where(division => division.NationId == loser.Id))
            {
                division.Equipment = MathF.Min(division.Equipment, 0.45f);
                division.Cohesion = MathF.Min(division.Cohesion, 0.55f);
                division.Strength = MathF.Min(division.Strength, 0.72f);
            }

        if (demand.Humiliate)
        {
            loser.WarSupport = M.Clamp01(loser.WarSupport - 0.18f);
            loser.Stability = M.Clamp01(loser.Stability - 0.10f);
            winner.WarSupport = M.Clamp01(winner.WarSupport + 0.08f);
            ModifierSystem.Grant(campaign, loser, "humiliated", 365 * 3);
        }

        // Last, because it is the term that needs the loser to still exist: whatever was
        // being annexed has been annexed by now, and what is left is what will answer.
        if (demand.Subjugate.Length > 0)
            SubjectSystem.Subjugate(campaign, winner, loser, demand.Subjugate, out _);

        war.Ended = true;
        war.EndedDay = campaign.ElapsedDays;
        war.WinnerId = winner.Id;
        war.PeaceSummary = Summary(campaign, war, demand, winner, loser);
        campaign.Chronicle.Record(campaign, "peace_signed", winner.Id, loser.Id);
        war.AttackerFront.Clear();
        war.DefenderFront.Clear();
        war.UnopposedProgress.Clear();

        // Every pair across the two sides. One war, one settlement, one truce for all of
        // the people who were in it - a coalition that has to sign thirty separate treaties
        // is thirty wars wearing a coat.
        var truceUntil = campaign.ElapsedDays + (demand.LongTruce ? 365 * 10 : 365 * 5);
        var bitterness = demand.Provinces.Count > 0 ? -60f : -35f;
        foreach (var attackingId in war.Side(true))
            foreach (var defendingId in war.Side(false))
            {
                if (attackingId == defendingId) continue;
                var relation = CampaignDiplomacy.Get(campaign, attackingId, defendingId);
                relation.Stance = DiplomaticStance.Truce;
                relation.TruceUntil = truceUntil;
                relation.LastActionDay = campaign.ElapsedDays;
                relation.Opinion = MathF.Min(relation.Opinion, bitterness);
            }

        foreach (var army in campaign.Armies.Where(army => army.AssignedWarId == war.Id))
        {
            army.AssignedWarId = -1;
            army.OrderKind = ArmyOrder.Manual;
            army.Route.Clear();
            army.PlannedRoute.Clear();
            army.MoveProgress = 0f;
        }
        foreach (var battle in campaign.Battles.Where(battle => battle.WarId == war.Id && !battle.Ended))
        {
            battle.Ended = true;
            battle.Outcome = "Peace agreement";
        }

        campaign.MarkMapChanged();
        return true;
    }

    /// <summary>Every province the loser still owns, for a conference to paint over.</summary>
    public static List<int> Claimable(Campaign campaign, StrategicWar war, int proposerId)
    {
        var topology = campaign.ProvinceMap;
        var result = new List<int>();
        if (topology is null) return result;

        // Everybody on the other side. With allies in a war, claiming only from the enemy
        // leader would leave an ally's ground unclaimable however much of it was taken.
        var losers = new HashSet<ushort>();
        foreach (var enemyId in war.EnemiesOf(proposerId))
            if (campaign.NationById(enemyId) is { } enemy) losers.Add((ushort)enemy.MapIndex);
        if (losers.Count == 0) return result;

        for (var province = 1; province <= topology.ProvinceCount; province++)
            if (losers.Contains(topology.ProvinceToNation[province]))
                result.Add(province);
        return result;
    }


    /// <summary>
    /// Round a settlement to the regions it is mostly about.
    ///
    /// Given the provinces a treaty would transfer, this returns the provinces it should
    /// transfer. A region the winner is taking most of is taken as far as the winner's own
    /// soldiers stand in it; a region the winner is barely taking is given back. The war
    /// goal is exempt from being given back - a war fought over one region does not end
    /// with the winner deciding the region was not worth it.
    ///
    /// Why it matters more than it looks: a border that cuts a region in two creates a
    /// second region, permanently. Province-by-province peaces do not only leave the map
    /// looking chewed, they multiply what the map is made of, and every later war starts
    /// from the smaller pieces the last one left.
    /// </summary>
    public static List<int> Tidy(Campaign campaign, StrategicWar war, IEnumerable<int> provinces,
        int winnerMapIndex, bool roundUp = true)
    {
        var topology = campaign?.ProvinceMap;
        var controllers = campaign?.ProvinceController;
        var wanted = new HashSet<int>(provinces ?? Array.Empty<int>());
        if (topology is null || controllers is null || wanted.Count == 0) return wanted.ToList();

        var defines = campaign.Defines;
        var up = roundUp ? M.Clamp01(defines.PeaceRoundUpShare) : 0f;
        var down = M.Clamp01(defines.PeaceRoundDownShare);
        if (up <= 0f && down <= 0f) return wanted.ToList();

        // Keep the province IDs from this one walk: a large brush can touch dozens of
        // regions, and scanning the entire world again for each of them stalls the UI.
        var byRegion = new Dictionary<int, List<int>>();
        var asked = new Dictionary<int, int>();
        var regions = new HashSet<int>();
        foreach (var province in wanted)
        {
            var region = topology.ProvinceToState[province];
            regions.Add(region);
            asked[region] = asked.GetValueOrDefault(region) + 1;
        }
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            var region = topology.ProvinceToState[province];
            if (!regions.Contains(region)) continue;
            if (!byRegion.TryGetValue(region, out var members))
                byRegion[region] = members = new List<int>();
            members.Add(province);
        }

        // A region goes whole or it does not go.
        //
        // This is the only rule that actually removes border gore, and the arithmetic says
        // why: taking any strict subset of a region cuts that region in two, and a region cut
        // in two becomes two regions permanently. Dropping slivers alone got the cuts down by
        // half and cost the winner most of what it had occupied; taking only what touches
        // home left the cuts exactly where they were. Whole regions leave none at all.
        //
        // It is also the convention every game of this shape uses, and for the same reason:
        // an army that has taken most of a province has taken the province, and a treaty
        // that carves it along the line two armies stopped on is a treaty nobody would sign.
        foreach (var region in regions)
        {
            if (region == 0) continue;
            var size = byRegion.TryGetValue(region, out var members) ? members.Count : 1;
            var share = asked.GetValueOrDefault(region) / (float)(size <= 0 ? 1 : size);
            var goal = war is not null && region == war.ClaimedStateMapIndex;

            // The region a war was declared over is taken whole for any share of it at all.
            //
            // It used to be exempt from being given back, which is right - a war fought over
            // one region does not end with the winner deciding the region was not worth it -
            // and exempt from being rounded up as well, which is how the one region the war
            // was actually about became the one region the treaty cut in half. Every cut a
            // decade of AI wars still left on the map came through here.
            //
            // The winner pays for the whole region: the demand is priced after this runs and
            // the loser refuses a treaty worth more than the war, so an army that walked one
            // province into its own war goal does not get the other thirty-nine for nothing -
            // it gets a refusal, and asks again when it has won more of the war.
            if (up > 0f && (share >= up || (goal && share > 0f)))
            {
                // Whole. Including ground the winner does not stand on: the border follows
                // the region, which is the entire point, and the alternative is the cut.
                if (members is not null)
                    foreach (var province in members) wanted.Add(province);
                continue;
            }
            if (goal && up <= 0f) continue;  // rounding up is forbidden; leave the goal as drawn
            if (share >= down) continue;     // the middle band, left exactly as it was drawn

            if (members is not null)
                foreach (var province in members) wanted.Remove(province);
        }

        // Never down to nothing, and never down to a fragment either: a country that won a
        // war signs for something, and the something is a whole region. Keeping the scraps
        // instead was the obvious floor and it quietly reintroduced exactly the cut this
        // method exists to prevent.
        if (wanted.Count == 0)
        {
            var bestRegion = -1;
            var bestShare = -1f;
            foreach (var region in regions)
            {
                if (region == 0) continue;
                var size = byRegion.TryGetValue(region, out var members) ? members.Count : 1;
                var share = asked.GetValueOrDefault(region) / (float)(size <= 0 ? 1 : size);
                if (share <= bestShare) continue;
                bestShare = share;
                bestRegion = region;
            }
            if (bestRegion > 0 && byRegion.TryGetValue(bestRegion, out var bestMembers))
                foreach (var province in bestMembers) wanted.Add(province);
        }

        return wanted.ToList();
    }

    public static List<int> ProvincesOfState(ProvinceTopology topology, int stateMapIndex)
    {
        var result = new List<int>();
        if (topology is null) return result;
        for (var province = 1; province <= topology.ProvinceCount; province++)
            if (topology.ProvinceToState[province] == (ushort)stateMapIndex)
                result.Add(province);
        return result;
    }

    private static string Summary(Campaign campaign, StrategicWar war, PeaceDemand demand,
        Nation winner, Nation loser)
    {
        var parts = new List<string>();
        if (demand.Provinces.Count > 0)
        {
            var states = demand.Provinces
                .Select(province => campaign.ProvinceMap.ProvinceToState[province])
                .Distinct()
                .Select(index => campaign.States.Find(state => state.MapIndex == index)?.Name)
                .Where(name => !string.IsNullOrEmpty(name))
                .Take(4)
                .ToList();
            var named = states.Count == 0 ? "territory" : string.Join(", ", states);
            parts.Add($"{winner.Name} took {demand.Provinces.Count} province(s) in {named}" +
                      (war.CreatedStates > 0 ? $", splitting {war.CreatedStates} region(s)" : ""));
        }
        if (demand.Reparations) parts.Add($"${war.Reparations:0.00}B in reparations changed hands");
        if (demand.TradeConcessions) parts.Add("a trade agreement was imposed");
        if (demand.Demilitarise) parts.Add($"the {loser.Name} armed forces were partially demobilised");
        if (demand.Humiliate) parts.Add($"{loser.Name} was publicly humbled");
        if (demand.LongTruce) parts.Add("a ten-year truce was signed");
        if (demand.Subjugate.Length > 0 && loser.AnswersTo(winner.Id))
            parts.Add($"{loser.Name} became a " +
                      $"{SubjectCatalog.Of(demand.Subjugate)?.Name ?? demand.Subjugate} of {winner.Name}");
        return parts.Count == 0
            ? $"{winner.Name} and {loser.Name} made peace on no terms at all."
            : char.ToUpperInvariant(parts[0][0]) + parts[0][1..] +
              (parts.Count > 1 ? "; " + string.Join("; ", parts.Skip(1)) : "") + ".";
    }
}

/// <summary>
/// Everything a conference needs to price a demand, worked out once.
///
/// A brush stroke re-prices the whole demand, and the demand can be hundreds of provinces
/// wide, so the parts that do not change between strokes - which region each province is
/// in, how many provinces a region has, where the loser governs from - are found once when
/// the conference opens and read from there. Pricing a province is then a handful of
/// lookups rather than a walk over the whole map.
/// </summary>
public sealed class PeaceLedger
{
    private readonly Campaign _campaign;
    private readonly StrategicWar _war;
    private readonly Nation _proposer;
    private readonly Nation _loser;
    private readonly Dictionary<int, int> _provincesPerState = new();
    private readonly Dictionary<int, long> _statePopulation = new();
    private readonly Dictionary<int, Nation> _enemies = new();
    private readonly HashSet<int> _capitalStates = new();
    private bool _surveyed;

    public PeaceLedger(Campaign campaign, StrategicWar war, int proposerId)
    {
        _campaign = campaign;
        _war = war;
        _proposer = campaign.NationById(proposerId);
        _loser = war is null ? null : campaign.NationById(war.EnemyLeaderOf(proposerId));
        if (war is not null)
            foreach (var enemyId in war.EnemiesOf(proposerId))
                if (campaign.NationById(enemyId) is { } enemy) _enemies[enemy.MapIndex] = enemy;
        Budget = PeaceConference.Budget(campaign, war, proposerId);
    }

    /// <summary>
    /// Work out what the loser owns and where their capital is - once, and only if somebody
    /// asks what a province costs.
    ///
    /// This used to happen in the constructor, which meant a walk of all thirty-eight
    /// thousand provinces every time a ledger was made. The AI makes one every time it
    /// wonders whether to negotiate, reads the budget off it, and in nine cases out of ten
    /// throws it away without pricing anything - ten thousand times over a campaign, which
    /// was three hundred and seventy million province reads and most of the AI's garbage.
    /// </summary>
    private void EnsureSurveyed()
    {
        if (_surveyed) return;
        _surveyed = true;

        var campaign = _campaign;
        var topology = campaign.ProvinceMap;
        if (topology is null || _loser is null) return;

        var capitals = new Dictionary<int, (int Province, float Distance)>();
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            if (!_enemies.TryGetValue(topology.ProvinceToNation[province], out var owner)) continue;
            var state = topology.ProvinceToState[province];
            _provincesPerState[state] = _provincesPerState.GetValueOrDefault(state) + 1;

            var centre = topology.CenterOf(province);
            var latitude = centre.Latitude - owner.CapitalLatitude;
            var longitude = centre.Longitude - owner.CapitalLongitude;
            var distance = latitude * latitude + longitude * longitude;
            if (!capitals.TryGetValue(owner.MapIndex, out var nearest) || distance < nearest.Distance)
                capitals[owner.MapIndex] = (province, distance);
        }
        foreach (var capital in capitals.Values) _capitalStates.Add(topology.ProvinceToState[capital.Province]);

        foreach (var state in campaign.States)
            if (_provincesPerState.ContainsKey(state.MapIndex))
                _statePopulation[state.MapIndex] = state.Population;
    }

    /// <summary>War score this side may spend on the whole demand.</summary>
    public float Budget { get; }

    public Nation Loser => _loser;

    /// <summary>Provinces the loser still owns - the ground a conference may paint over.</summary>
    public IReadOnlyDictionary<int, int> ProvincesPerState
    {
        get
        {
            EnsureSurveyed();
            return _provincesPerState;
        }
    }

    public PeaceConference.ProvinceCost Price(int province)
    {
        var topology = _campaign.ProvinceMap;
        if (topology is null || _proposer is null || _loser is null ||
            province <= 0 || province > topology.ProvinceCount)
            return new PeaceConference.ProvinceCost(province, 0f, "not a province");
        if (!_enemies.ContainsKey(topology.ProvinceToNation[province]))
            return new PeaceConference.ProvinceCost(province, 0f, "not theirs to give");

        var defines = _campaign.Defines;
        var cost = defines.PeaceCostPerProvince;
        string reason;

        var held = _campaign.ProvinceController is not null &&
                   _campaign.ProvinceController[province] == (ushort)_proposer.MapIndex;
        var secured = _campaign.ProvinceOccupation is not null &&
                      _campaign.ProvinceOccupation[province] >= defines.OccupationSecureThreshold;
        if (held && secured) { cost *= defines.PeaceCostSecuredFactor; reason = "occupied and secure"; }
        else if (held) { cost *= defines.PeaceCostHeldFactor; reason = "occupied but not secure"; }
        else { cost *= defines.PeaceCostUnheldFactor; reason = "never taken"; }

        var stateMapIndex = topology.ProvinceToState[province];
        if (_war.ClaimedStateMapIndex >= 0 && stateMapIndex == _war.ClaimedStateMapIndex)
        {
            cost *= defines.PeaceCostWarGoalFactor;
            reason += ", inside the declared war goal";
        }

        EnsureSurveyed();
        if (_provincesPerState.TryGetValue(stateMapIndex, out var provinces) && provinces > 0 &&
            _statePopulation.TryGetValue(stateMapIndex, out var population))
        {
            var share = population / (float)provinces;
            cost *= 1f + M.Clamp(share / M.Max(1f, defines.PeaceCostPopulationScale), 0f, 2.5f);
        }

        if (_capitalStates.Contains(stateMapIndex))
        {
            cost *= defines.PeaceCostCapitalFactor;
            reason += ", the seat of government";
        }

        return new PeaceConference.ProvinceCost(province, cost, reason);
    }

    public float Cost(PeaceDemand demand)
    {
        if (demand is null) return 0f;
        var defines = _campaign.Defines;
        var total = 0f;
        foreach (var province in demand.Provinces) total += Price(province).Cost;
        if (demand.Reparations) total += defines.PeaceCostReparations;
        if (demand.TradeConcessions) total += defines.PeaceCostTradeConcessions;
        if (demand.Demilitarise) total += defines.PeaceCostDemilitarise;
        if (demand.Humiliate) total += defines.PeaceCostHumiliate;
        if (demand.LongTruce) total += defines.PeaceCostLongTruce;
        if (demand.Subjugate.Length > 0)
            total += PeaceConference.SubjugationCost(_campaign, _proposer, _loser, demand.Subjugate);
        return total;
    }
}
