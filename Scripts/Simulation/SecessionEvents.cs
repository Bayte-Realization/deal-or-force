using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>Why one region ended up in the new country.</summary>
public enum SecessionCause
{
    /// <summary>It is their country. The people whose grievance this whole thing is.</summary>
    Nationality,

    /// <summary>
    /// It is the same people, and it has stopped being the same politics. A federal region
    /// leaving a centre it no longer accepts.
    /// </summary>
    Federal,

    /// <summary>It went with them. Angry, ungoverned, and on the wrong side of the new line.</summary>
    Discontent
}

/// <summary>What left, and why it left.</summary>
public sealed class SecessionPiece
{
    public int MapIndex;
    public string Name = "";
    public SecessionCause Cause;
    public long Population;
}

/// <summary>
/// The week in which a country loses part of itself.
///
/// <see cref="SecessionCore"/> says how close a region is; this is what happens when it
/// arrives. What leaves is rarely one tidy nation-shaped piece: there is a core, which is a
/// people with a grievance, and then there is whatever was next to the core and had nothing
/// left to lose. The second kind need not share the first kind's nationality, or anybody's -
/// a province that was merely angry and ungoverned, finding itself on the far side of a new
/// border, frequently just stays there. That is what a secession has actually looked like
/// every time one has happened, and a mechanic that drew the line strictly along a language
/// would produce a map no real century has ever had.
///
/// What is made is a real country: a tag nothing else uses, a name, a colour, a capital, a
/// government it chose rather than inherited, and enough militia to make taking it back a
/// decision rather than a formality. Whether the state it left accepts that is
/// <see cref="SecessionCore.Response"/>, and it is not a coin flip.
/// </summary>
public static class SecessionEvents
{
    /// <summary>
    /// What left today, for whoever draws the interface. Emptied every day, including the
    /// six days in seven on which nothing is worked out - a list that says "this day" on one
    /// day and "some day last week" on the others is a list every reader gets wrong once.
    /// </summary>
    public static readonly List<SecessionReport> Recent = new();

    /// <summary>
    /// Which world <see cref="Recent"/> was written for.
    ///
    /// Two campaigns can exist in one process - a multiplayer client rebuilding from a
    /// snapshot beside the one it is replacing - and a static list belongs to whichever of
    /// them wrote to it last. Nothing in the simulation reads either of these, so this
    /// cannot put two peers in different worlds; it can put the wrong window on the screen,
    /// and an interface that checks this does not.
    /// </summary>
    public static Campaign RecentWorld;

    /// <summary>Scratch, reused across the week. This runs against every country in the world.</summary>
    private static readonly Dictionary<int, ScenarioState> ByRegion = new();
    private static readonly Dictionary<int, List<int>> Adjacent = new();

    /// <summary>
    /// Runs every day and does its work weekly, rather than being called weekly: the
    /// clearing has to happen on the days the working does not.
    /// </summary>
    public static void Tick(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(SecessionEvents))) return;
        if (campaign?.ProvinceMap is null) return;
        Recent.Clear();
        RecentWorld = campaign;

        // The pressure below is authored per week, so it is added once a week whatever the
        // step length - which is the same rule the rest of the day pass follows and the
        // reason a six-hour campaign and a three-day campaign lose the same regions.
        if (campaign.ElapsedDays % 7 != 0) return;
        var weights = SecessionCore.Weights;

        // Countries are taken as a snapshot because a secession adds one, and a country
        // three seconds old has nothing to secede from it.
        var nations = campaign.Nations.ToArray();
        foreach (var nation in nations)
        {
            if (nation.Population <= 0) continue;

            var ripe = (List<ScenarioState>)null;
            foreach (var state in campaign.StatesOf(nation.MapIndex))
            {
                var gained = SecessionCore.Pressure(campaign, nation, state);
                if (gained <= 0f)
                {
                    // Pressure that stops being fed drains away. A grievance that was
                    // answered - the war won, the region governed, the people given a
                    // reason to stay - should be able to be answered permanently, and a
                    // number that only ever went up would make every answer a delay.
                    if (state.SecessionPressure > 0f)
                        state.SecessionPressure = MathF.Max(0f, state.SecessionPressure - weights.Base);
                    continue;
                }
                state.SecessionPressure += gained;
                if (state.SecessionPressure >= 1f) (ripe ??= new List<ScenarioState>()).Add(state);
            }

            // The pressure is still kept while the country is cooling off - what it has not
            // got is anywhere to go. A region that spent the cooling-off period getting
            // angrier leaves the moment the period is over, which is the right behaviour and
            // the reason this check is here rather than at the top of the loop.
            if (ripe is null) continue;
            if (campaign.ElapsedDays - nation.LastSecessionDay < weights.CooldownDays) continue;

            var report = Consider(campaign, nation, ripe);
            if (report is not null) Recent.Add(report);
        }
    }

    /// <summary>Work out what would leave, and if it is a country, let it.</summary>
    private static SecessionReport Consider(Campaign campaign, Nation parent, List<ScenarioState> ripe)
    {
        var weights = SecessionCore.Weights;
        var owned = campaign.StatesOf(parent.MapIndex);
        if (owned.Length < 2) return null;
        BuildAdjacency(campaign, owned);

        var core = Core(campaign, parent, ripe, out var culture);
        core = Cap(parent, core);
        if (core.Count >= owned.Length)
        {
            var keep = core.FirstOrDefault(s => s.MapIndex == parent.CapitalStateMapIndex)
                ?? core.OrderByDescending(s => s.Population).First();
            core.Remove(keep);
        }
        if (core.Count < weights.MinimumRegions) return null;

        var leaving = new List<SecessionPiece>();
        var taken = new HashSet<int>();
        long people = 0;
        foreach (var state in core)
        {
            taken.Add(state.MapIndex);
            people += state.Population;
            leaving.Add(new SecessionPiece
            {
                MapIndex = state.MapIndex,
                Name = state.Name,
                Cause = SecessionCore.IsFederalBreak(campaign, parent, state)
                    ? SecessionCause.Federal
                    : SecessionCause.Nationality,
                Population = state.Population
            });
        }
        if (people < weights.MinimumPopulation) return null;

        Accrete(campaign, parent, owned, taken, leaving, culture, ref people);
        Trim(parent, leaving, ref people);
        while (leaving.Count >= owned.Length && leaving.Count > 0)
        {
            var drop = leaving.FirstOrDefault(p => p.MapIndex == parent.CapitalStateMapIndex)
                ?? leaving.Last();
            leaving.Remove(drop);
            people -= drop.Population;
        }
        if (leaving.Count == 0 || people < weights.MinimumPopulation) return null;

        return Secede(campaign, parent, leaving, culture, people);
    }

    /// <summary>
    /// Which regions touch which, once, for this country.
    ///
    /// Walking the province graph per region pair is what the first draft did, and against
    /// a country with ninety regions it was the whole week. One pass over the country's
    /// provinces gives the same answer.
    /// </summary>
    private static void BuildAdjacency(Campaign campaign, ScenarioState[] owned)
    {
        ByRegion.Clear();
        Adjacent.Clear();
        foreach (var state in owned)
        {
            ByRegion[state.MapIndex] = state;
            Adjacent[state.MapIndex] = new List<int>();
        }

        var topology = campaign.ProvinceMap;
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            var here = topology.ProvinceToState[province];
            if (!Adjacent.TryGetValue(here, out var neighbours)) continue;
            foreach (var next in topology.Neighbors[province])
            {
                var there = topology.ProvinceToState[next];
                if (there == here || !ByRegion.ContainsKey(there)) continue;
                if (!neighbours.Contains(there)) neighbours.Add(there);
            }
        }
    }

    /// <summary>
    /// The grievance the whole thing is about: a contiguous minority homeland, or a bloc
    /// whose own people meet every condition of the separate federal road.
    /// </summary>
    private static List<ScenarioState> Core(Campaign campaign, Nation owner,
        List<ScenarioState> ripe, out string culture)
    {
        culture = "";
        var best = new List<ScenarioState>();
        long bestPeople = 0;

        var byCulture = new Dictionary<string, List<ScenarioState>>(StringComparer.OrdinalIgnoreCase);
        var federal = new List<ScenarioState>();
        foreach (var state in ripe)
        {
            // The parent's national capital cannot secede away from the rest of the nation.
            if (state.MapIndex == owner.CapitalStateMapIndex) continue;
            // A political break does not need a fabricated foreign population. Recheck
            // the current conditions: old pressure alone cannot make a healthy or
            // unreviewed region leave, nor bypass either mechanic's switch.
            if (SecessionCore.IsFederalBreak(campaign, owner, state))
            {
                federal.Add(state);
                continue;
            }
            var largest = EconomySystem.Cultures(state).FirstOrDefault();
            if (largest.Culture is null || largest.Size <= 0) continue;
            // The existing ethnic road still needs a people the state is not for.
            if (DemographyCatalog.IsPrimary(owner.Tag, largest.Culture)) continue;
            if (!byCulture.TryGetValue(largest.Culture, out var list))
                byCulture[largest.Culture] = list = new List<ScenarioState>();
            list.Add(state);
        }

        foreach (var (people, states) in byCulture)
        {
            // A people that already has a living state of its own does not found a second one:
            // Ukrainians on ground Russia took are Ukraine's lost land (LostLands), not a "Republic
            // of Ukraine" that seceded five times and was taken back five times.
            if (KinState(campaign, owner, people) is not null) continue;
            foreach (var bloc in Contiguous(states))
            {
                long here = 0;
                foreach (var state in bloc) here += state.Population;
                if (best.Count > 0 && here <= bestPeople) continue;
                best = bloc;
                bestPeople = here;
                culture = people;
            }
        }
        // Federal regions share their political rejection, not necessarily one language.
        // Keep them separate from an ethnic bloc and apply the same connectivity and
        // subsequent population cap as every other secession.
        foreach (var bloc in Contiguous(federal))
        {
            var here = bloc.Sum(state => state.Population);
            if (best.Count > 0 && here <= bestPeople) continue;
            best = bloc;
            bestPeople = here;
            var seat = bloc.OrderByDescending(state => state.Population).First();
            culture = EconomySystem.Cultures(seat).FirstOrDefault().Culture
                ?? DemographyCatalog.PrimaryCulture(owner.Tag);
        }
        return best;
    }

    /// <summary>
    /// Keep the core a secession rather than a partition.
    ///
    /// Some countries are mostly not their own titular people - Indonesia is thirty peoples
    /// and Afghanistan is six - and for those, "every ripe region that shares a people" can
    /// be most of the country. Losing two thirds of itself in one afternoon is not a
    /// secession; it is a dissolution, and it should be the outcome of a decade of them
    /// rather than of one Tuesday.
    ///
    /// So the core is regrown rather than truncated: start from the region that has gone
    /// furthest, take neighbours in the order they are ready to go, and stop at the cap.
    /// What is left is contiguous, is the angriest part of what wanted to leave, and can
    /// come back for the rest after the cooling-off period - which is how a country that is
    /// genuinely coming apart comes apart, in pieces and over years.
    /// </summary>
    private static List<ScenarioState> Cap(Nation parent, List<ScenarioState> core)
    {
        var cap = SecessionCore.Weights.MaximumShare;
        if (core.Count == 0 || parent.Population <= 0 || cap <= 0f) return core;

        long people = 0;
        foreach (var state in core) people += state.Population;
        var ceiling = (long)(parent.Population * cap);
        if (people <= ceiling) return core;

        var inBloc = new HashSet<int>();
        foreach (var state in core) inBloc.Add(state.MapIndex);

        var seed = core[0];
        foreach (var state in core)
            if (state.SecessionPressure > seed.SecessionPressure) seed = state;

        var kept = new List<ScenarioState> { seed };
        var taken = new HashSet<int> { seed.MapIndex };
        var carried = seed.Population;

        while (true)
        {
            ScenarioState next = null;
            foreach (var state in core)
            {
                if (taken.Contains(state.MapIndex) || !Touches(taken, state.MapIndex)) continue;
                if (carried + state.Population > ceiling) continue;
                if (next is null || state.SecessionPressure > next.SecessionPressure) next = state;
            }
            if (next is null) break;
            kept.Add(next);
            taken.Add(next.MapIndex);
            carried += next.Population;
        }
        return kept;
    }

    /// <summary>Groups of regions that can reach each other over the country's own ground.</summary>
    private static List<List<ScenarioState>> Contiguous(List<ScenarioState> states)
    {
        var wanted = new HashSet<int>();
        foreach (var state in states) wanted.Add(state.MapIndex);

        var groups = new List<List<ScenarioState>>();
        var seen = new HashSet<int>();
        var queue = new Queue<int>();
        foreach (var start in states)
        {
            if (!seen.Add(start.MapIndex)) continue;
            var group = new List<ScenarioState> { start };
            queue.Clear();
            queue.Enqueue(start.MapIndex);
            while (queue.Count > 0)
            {
                if (!Adjacent.TryGetValue(queue.Dequeue(), out var neighbours)) continue;
                foreach (var other in neighbours)
                {
                    if (!wanted.Contains(other) || !seen.Add(other)) continue;
                    group.Add(ByRegion[other]);
                    queue.Enqueue(other);
                }
            }
            groups.Add(group);
        }
        return groups;
    }

    /// <summary>
    /// And whatever goes with them.
    ///
    /// Grows outward from the core one ring at a time, taking any region that touches what
    /// is already leaving and is either nearly gone itself or simply has nothing left to
    /// lose. Nationality is not consulted here at all - this is the ground that was next to
    /// the thing that happened, and a border does not ask.
    /// </summary>
    /// <remarks>
    /// Only the core's own neighbours, and only ground that is plausibly theirs: a region where
    /// the seceding people are a real share, or one breaking for the same federal reasons, and
    /// never another country's core. Growing from whatever had already joined walked a secession
    /// off in a line through the angriest regions next door - a Dagestani republic reaching
    /// Kharkiv through Kalmykia, Rostov and Voronezh.
    /// </remarks>
    private static void Accrete(Campaign campaign, Nation parent, ScenarioState[] owned,
        HashSet<int> taken, List<SecessionPiece> leaving, string culture, ref long people)
    {
        var weights = SecessionCore.Weights;
        if (weights.JoinLimit <= 0) return;
        var core = new HashSet<int>(taken);

        var joined = 0;
        var growing = true;
        while (growing && joined < weights.JoinLimit)
        {
            growing = false;
            // The best candidate in the whole ring, not the first one found: whichever
            // region is least attached goes first, so a limit of six takes the six angriest
            // rather than the six the region list happened to be sorted into.
            ScenarioState pick = null;
            var strongest = 0f;
            foreach (var state in owned)
            {
                // The national capital cannot be accreted into a peripheral border secession.
                if (state.MapIndex == parent.CapitalStateMapIndex) continue;
                if (taken.Contains(state.MapIndex)) continue;
                if (!Touches(core, state.MapIndex)) continue;
                if (!Belongs(campaign, parent, state, culture, weights.JoinPeopleShare)) continue;

                var willing = MathF.Max(
                    state.SecessionPressure >= weights.JoinPressure ? state.SecessionPressure : 0f,
                    SecessionCore.Discontent(campaign, parent, state) >= weights.JoinDiscontent
                        ? SecessionCore.Discontent(campaign, parent, state)
                        : 0f);
                if (willing <= strongest) continue;
                strongest = willing;
                pick = state;
            }
            if (pick is null) break;

            taken.Add(pick.MapIndex);
            people += pick.Population;
            leaving.Add(new SecessionPiece
            {
                MapIndex = pick.MapIndex,
                Name = pick.Name,
                Cause = SecessionCause.Discontent,
                Population = pick.Population
            });
            joined++;
            growing = true;
        }
    }

    /// <summary>Whether a region next to a secession's core is ground that could plausibly go with it.</summary>
    private static bool Belongs(Campaign campaign, Nation parent, ScenarioState state, string culture, float share)
    {
        // Somebody else's core - land the parent conquered - is not the secession's to take.
        if (state.CoreTags.Count > 0 && !state.CoreTags.Contains(parent.Tag, StringComparer.OrdinalIgnoreCase))
            return false;
        if (SecessionCore.IsFederalBreak(campaign, parent, state)) return true;
        if (string.IsNullOrWhiteSpace(culture) || state.Population <= 0) return true;
        long theirs = 0;
        foreach (var (people, size) in EconomySystem.Cultures(state))
            if (string.Equals(people, culture, StringComparison.OrdinalIgnoreCase)) theirs += size;
        return theirs >= state.Population * (double)share;
    }

    /// <summary>The living state, other than the owner, whose own people this is; null when there is none.</summary>
    internal static Nation KinState(Campaign campaign, Nation owner, string culture)
    {
        foreach (var nation in campaign.Nations)
        {
            if (nation.Id == owner.Id || nation.Population <= 0 || nation.Movement is not null ||
                campaign.Fallen.Contains(nation.Id) || !campaign.HasOwnedLand(nation)) continue;
            if (string.Equals(DemographyCatalog.PrimaryCulture(nation.Tag), culture, StringComparison.OrdinalIgnoreCase))
                return nation;
        }
        return null;
    }

    private static bool Touches(HashSet<int> taken, int region) =>
        Adjacent.TryGetValue(region, out var neighbours) && neighbours.Any(taken.Contains);

    /// <summary>
    /// Keep it a secession. Past the authored share of the country, the regions that only
    /// came along are given back - largest first, and never the core, which is the thing
    /// this was about.
    /// </summary>
    private static void Trim(Nation parent, List<SecessionPiece> leaving, ref long people)
    {
        var cap = SecessionCore.Weights.MaximumShare;
        if (parent.Population <= 0 || cap <= 0f) return;

        while (people / (float)parent.Population > cap)
        {
            SecessionPiece biggest = null;
            foreach (var piece in leaving)
            {
                if (piece.Cause != SecessionCause.Discontent) continue;
                if (biggest is null || piece.Population > biggest.Population) biggest = piece;
            }
            // Nothing left to give back but the grievance itself, which does not get given
            // back: a people leaving is the thing that happened, whatever share of the
            // country it turns out to be.
            if (biggest is null) return;
            leaving.Remove(biggest);
            people -= biggest.Population;
        }
    }

    /// <summary>Make the country, move the ground, and find out what the old state does.</summary>
    /// <summary>
    /// Which of the three things this was.
    ///
    /// The order is the order the facts rank in. If most of what left went for its own
    /// reasons rather than a people's, it was a revolt about conditions whatever else was
    /// true of it. Otherwise a people that the state is not for is the defining fact. Only
    /// when the people IS the state's own - a secession by the majority's own kin - does the
    /// faith become the thing that explains it, which is what a sectarian split actually is.
    /// </summary>
    private static string RevoltKind(Nation parent, IReadOnlyList<SecessionPiece> leaving,
        string culture, long people)
    {
        long national = 0, federal = 0;
        foreach (var piece in leaving)
        {
            if (piece.Cause == SecessionCause.Nationality) national += piece.Population;
            else if (piece.Cause == SecessionCause.Federal) federal += piece.Population;
        }
        if (people > 0 && federal / (double)people >= 0.5) return "federal";
        if (people <= 0 || national / (double)people < 0.5) return "discontent";

        // Faith, not language, is what separates the remaining two - and the first draft of
        // this asked whether the seceding culture was one of the parent's own, which can
        // never be true: the core is BUILT from a people the state is not for, so that
        // branch was unreachable and `sectarian` was dead in twenty-seven secessions.
        var theirs = DemographyCatalog.PrimaryCultures(parent.Tag);
        var parentFaith = theirs.Count > 0 ? DemographyCatalog.FaithOf(theirs[0]) : null;
        var ourFaith = DemographyCatalog.FaithOf(culture);
        if (parentFaith is { Count: > 0 } && ourFaith is { Count: > 0 } &&
            !string.Equals(parentFaith[0].Religion, ourFaith[0].Religion, StringComparison.OrdinalIgnoreCase))
            return "sectarian";
        return "national";
    }

    /// <summary>
    /// Put it to the player, and take the border as it stands until they answer.
    ///
    /// Accepted is the honest interim state rather than a default: the region HAS left, and
    /// the parent has not yet decided to do anything about it. Choosing to send the army is
    /// what turns that into a war, and until then the world is simply a country smaller than
    /// it was this morning.
    /// </summary>
    private static SecessionOutcome Ask(Campaign campaign, Nation parent, Nation born,
        IReadOnlyList<SecessionPiece> leaving, long people, long was, out string reason)
    {
        var share = was <= 0 ? 0f : people / (float)was;
        var names = string.Join(", ", leaving.Take(4).Select(piece => piece.Name));
        if (leaving.Count > 4) names += $" and {leaving.Count - 4} more";

        campaign.PendingEvents.Add(new PendingCampaignEvent
        {
            Id = campaign.ElapsedDays * 1000 + parent.Id,
            NationId = parent.Id,
            Title = Loc.Format("{0} has declared independence", born.Name),
            Description = Loc.Format(
                "{0} has proclaimed itself out of {1}, taking {2} and {3} people - {4} of the " +
                "country. Militia are already under arms there. The cabinet is waiting.",
                born.Name, parent.Name, names, people.ToString("N0"), share.ToString("P0")),
            CreatedDay = campaign.ElapsedDays,
            ExpiresDay = campaign.ElapsedDays + 60,
            Options = new[]
            {
                new CampaignEventOption
                {
                    Title = "Recognise the new border",
                    Consequence = "The war nobody wanted does not happen. The country is " +
                                  "smaller, and every other region has watched it work.",
                    Effect = ScriptedEffects.From(("stability", 0.03f), ("war_support", -0.05f))
                },
                new CampaignEventOption
                {
                    Title = "Refuse to recognise it",
                    Consequence = "No war, no acceptance. The claim stands and so does the " +
                                  "resentment on both sides of it.",
                    Effect = ScriptedEffects.From(("stability", -0.02f), ("war_support", 0.04f))
                },
                new CampaignEventOption
                {
                    Title = "Send the army",
                    Consequence = "War, against a state that has just raised militia on its " +
                                  "own ground and has nothing left to lose.",
                    Effect = ScriptedEffects.From(("war_support", 0.08f), ("stability", -0.05f)),
                    DeclareWarOn = born.Id
                }
            }
        });

        reason = $"put to the player ({share:P0} of the country going)";
        return SecessionOutcome.Accepted;
    }

    private static string Slug(string value) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToLowerInvariant().Replace(' ', '_');

    /// <summary>
    /// One region goes now, by the same road the week takes: founded, given the ground it
    /// stands on, armed, and answered by the country it left. For the console and for a mod's
    /// scripted event - a secession somebody wants to happen rather than one that has built
    /// up. The people who found it are the region's own largest, unless a mod names others.
    /// </summary>
    public static SecessionReport Force(Campaign campaign, ScenarioState state, string culture = null,
        SecessionCause cause = SecessionCause.Nationality)
    {
        if (campaign?.ProvinceMap is null || state is null || LakeRegions.IsWaterState(state)) return null;
        var parent = campaign.Nations.Find(nation => nation.MapIndex == state.NationMapIndex);
        if (parent is null || campaign.StatesOf(parent).Length < 2) return null;
        if (string.IsNullOrWhiteSpace(culture))
        {
            var byCulture = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in state.PopulationGroups)
                byCulture[group.Culture] = byCulture.GetValueOrDefault(group.Culture) + group.Size;
            culture = byCulture.Count == 0 ? parent.Tag : byCulture.OrderByDescending(pair => pair.Value).First().Key;
        }
        var piece = new SecessionPiece
        {
            MapIndex = state.MapIndex, Name = state.Name,
            Cause = cause, Population = state.Population
        };
        return Secede(campaign, parent, new List<SecessionPiece> { piece }, culture, state.Population);
    }

    /// <summary>
    /// A civil war: part of the country under an armed opposition of its own people, not a
    /// nation leaving. The same founding as a secession - ground, militia, defecting
    /// garrisons - but nobody is asked and nobody lets it go: it is a war from the first day,
    /// and the side that rose is named for what it wants rather than for whom it is
    /// (CivilWarOutbreak).
    /// </summary>
    internal static SecessionReport Uprising(Campaign campaign, Nation parent, IReadOnlyList<ScenarioState> regions,
        Action<Nation> shape)
    {
        if (campaign?.ProvinceMap is null || parent is null || regions is null || regions.Count == 0) return null;
        var leaving = regions.Select(state => new SecessionPiece
        {
            MapIndex = state.MapIndex, Name = state.Name, Population = state.Population, Cause = SecessionCause.Discontent
        }).ToList();
        var report = Secede(campaign, parent, leaving, "", regions.Sum(state => state.Population), shape, fight: true);
        if (report is not null) Recent.Add(report);
        return report;
    }

    private static SecessionReport Secede(Campaign campaign, Nation parent,
        List<SecessionPiece> leaving, string culture, long people, Action<Nation> shape = null, bool fight = false)
    {
        if (!RegionalResourcePartition.ValidAccounts(parent)) return null;
        // Political succession cannot repaint a third party's military occupation.
        // Do this before creating a tag, transferring accounts, or drawing a response.
        var provinces = RevoltTerritory.Select(campaign, parent, ref leaving);
        if (provinces.Count == 0 || leaving.Count == 0) return null;
        people = leaving.Sum(piece => piece.Population);
        if (people <= 0) return null;
        var residentTotal = campaign.StatesOf(parent).Sum(state => (double)state.Population);
        var was = parent.Population;
        var revolt = RevoltKind(parent, leaving, culture, people);
        var matchingMovement = FindMatchingMovement(campaign, leaving);
        Nation born;
        if (matchingMovement is not null)
        {
            born = matchingMovement;
            born.FoundedInRevolt = revolt;
            born.SecededFromId = parent.Id;
            var movementStateName = MovementCatalog.Of(matchingMovement.Movement?.Id)?.StateName;
            if (!string.IsNullOrEmpty(movementStateName)) born.Name = movementStateName;
            var share = parent.Population <= 0 ? 0f : M.Clamp01(people / (float)parent.Population);
            born.Population = people;
            born.Gdp = parent.Gdp * share;
            born.Treasury = MathF.Max(born.Treasury, parent.Treasury * share);
            born.ManpowerReserve = parent.ManpowerReserve * share;
            born.OutputScale = parent.OutputScale;
            born.StandardOfLiving = parent.StandardOfLiving;
            born.Literacy = parent.Literacy;
            born.LiteracyOffset = parent.LiteracyOffset;
            born.Productivity = parent.Productivity;
            born.Urbanisation = parent.Urbanisation;
            born.AgricultureShare = parent.AgricultureShare;
            born.TaxRate = parent.TaxRate;
            born.WelfareSpending = parent.WelfareSpending;
            born.EducationSpending = parent.EducationSpending;
            born.Stability = SecessionCore.Weights.Stability;
            born.WarSupport = SecessionCore.Weights.WarSupport;
            born.Government = "provisional_government";
            born.PoliticsChangedDay = campaign.ElapsedDays;
            MovementRules.GraduateToStatehood(campaign, born);
        }
        else
        {
            born = Found(campaign, parent, leaving, culture, people, revolt);
            if (born is null) return null;
            born.FoundedInRevolt = revolt;
            born.SecededFromId = parent.Id;
        }
        // The land that left is the new country's homeland from its first minute: without
        // this seed the fall flow (MovementFall.Decide) never sees a secession-born country
        // that later loses everything, and it lingers for ever as a stateless ghost with an
        // economy, a militia and no ground.
        //
        // The core, not everything that came along, and added to rather than replaced: a homeland
        // rewritten with whatever left each time drifted across the map with every fall and rise.
        var homeland = leaving.Where(piece => piece.Cause != SecessionCause.Discontent).Select(piece => piece.MapIndex).ToList();
        if (homeland.Count == 0) homeland = leaving.Select(piece => piece.MapIndex).ToList();
        if (campaign.Homelands.TryGetValue(born.Id, out var earlier) && earlier is { Count: > 0 })
            homeland = earlier.Concat(homeland).Distinct().ToList();
        campaign.Homelands[born.Id] = homeland;
        var coreRegions = homeland.ToHashSet();
        shape?.Invoke(born);

        // Partition productive capacity BEFORE changing the parent's resident count:
        // ProductionCapacity already scales its reference by the population still present.
        RegionalResourcePartition.Transfer(parent, born,
            (float)Math.Clamp(people / Math.Max(1d, residentTotal), 0d, 1d));
        // Not a conquest. It is standing on the ground it already lived on, and a state born
        // an international pariah for that would be the wrong answer twice over.
        StatePartition.Annex(campaign, provinces, born, asConquest: false);
        RegionalResourcePartition.RecountResidents(campaign, parent, born);
        var departedStates = campaign.StatesOf(born);
        // For a region cut by an occupation, inherit only the residents actually leaving,
        // not the entire region's money, debt, research or production capacity.
        var causes = leaving.ToDictionary(piece => piece.MapIndex, piece => piece.Cause);
        leaving = departedStates.Select(state => new SecessionPiece
        {
            MapIndex = state.MapIndex, Name = state.Name, Population = state.Population,
            Cause = causes.GetValueOrDefault(state.MapIndex,
                causes.GetValueOrDefault(state.ParentMapIndex, SecessionCause.Discontent))
        }).ToList();
        people = born.Population;
        CampaignDiplomacy.ForgetBorders();
        SecessionResearch.Inherit(campaign, parent, born, departedStates, was);

        foreach (var piece in leaving)
            foreach (var state in campaign.States)
                if (state.MapIndex == piece.MapIndex) { state.SecessionPressure = 0f; break; }

        parent.LastSecessionDay = campaign.ElapsedDays;
        born.LastSecessionDay = campaign.ElapsedDays;
        // The capital stands in the core - the people's own ground - not in whichever region that
        // came along is the most populous: a Dagestani state does not govern from Rostov.
        var seat = campaign.StatesOf(born).Where(s => coreRegions.Contains(s.MapIndex) || coreRegions.Contains(s.ParentMapIndex))
                       .OrderByDescending(s => s.Population).FirstOrDefault()
                   ?? campaign.StatesOf(born).OrderByDescending(s => s.Population).FirstOrDefault();
        if (seat is not null)
        {
            CapitalCore.Establish(campaign, born, seat);
            born.CapitalName = seat.Name;
        }
        campaign.LocateCapitals(preserveExisting: true);
        // A revived movement keeps the name it fell with only while that name still describes
        // the ground it has come back on.
        if (matchingMovement is not null) NationNaming.Refit(campaign, born, "revived");

        // Its baseline is its own. A country that has never been this country before has no
        // institutions to take credit for, so what the formula explains is all there is -
        // which is also what makes a new state fragile in exactly the way it should be.
        born.StabilityBias = 0f;
        born.StabilityBias = born.Stability - StabilityCore.Target(campaign, born);

        var raised = RaiseFoundingMilitia(campaign, born);

        // A country nobody is playing answers the way it always has. A country somebody is
        // playing is asked, because this is the largest thing that happens to a state and
        // rolling for it is the game playing that turn instead of the player.
        var reason = "";
        var outcome = fight ? SecessionOutcome.Contested : parent.IsHuman
            ? Ask(campaign, parent, born, leaving, people, was, out reason)
            : SecessionCore.Response(campaign, parent, leaving, out reason);
        if (outcome == SecessionOutcome.Contested)
        {
            var war = CampaignDiplomacy.DeclareWar(campaign, parent.Id, born.Id,
                leaving[0].MapIndex, fight ? "uprising" : "secession", onOwnInitiative: !fight);
            // Refused, and unable to do anything about it - a truce, an alliance, no way to
            // reach them. That is an acceptance whatever the cabinet thinks, and recording
            // it as one keeps the state of the world honest.
            if (war is null)
            {
                CampaignDiplomacy.CanDeclareWar(campaign, parent.Id, born.Id, out var refusal);
                reason += $"; could not go to war: {refusal}";
                outcome = SecessionOutcome.Accepted;
            }
            else
            {
                // Part of the garrison standing on the ground goes over: the war is fought by
                // soldiers who were the state's last week, not by four companies of militia.
                var defected = Reintegration.Defect(campaign, parent, born, provinces.ToHashSet());
                if (defected > 0)
                {
                    FrontPlanner.ReassignAi(campaign, war);
                    DiagnosticLogs.Write("simulation", $"SECESSION_DEFECTION born={born.Tag} formations={defected}");
                }
            }
        }
        if (outcome == SecessionOutcome.Accepted)
        {
            // A negotiated independence still divides the garrison standing on the ground.
            // The new state is not born with only a militia because the parent signed a paper.
            var defected = Reintegration.Defect(campaign, parent, born, provinces.ToHashSet(),
                campaign.Defines.PeacefulSecessionDefectionShare);
            if (defected > 0)
                DiagnosticLogs.Write("simulation",
                    $"SECESSION_PEACEFUL_DEFECTION born={born.Tag} formations={defected}");
        }
        if (outcome == SecessionOutcome.Accepted)
            OpinionSystem.Remember(campaign, CampaignDiplomacy.Get(campaign, born.Id, parent.Id), "let_us_go");
        OpinionSystem.Remember(campaign, CampaignDiplomacy.Get(campaign, parent.Id, born.Id), "seceded_from_us");

        if (fight) CivilWarOutbreak.RecordStart(campaign);
        campaign.Chronicle.Record(campaign, fight ? "civil_war" : outcome == SecessionOutcome.Contested ? "secession_contested" : "secession",
            born.Id, parent.Id, place: leaving[0].Name);
        campaign.MarkStrategicChange();
        DiagnosticLogs.Write("simulation",
            $"SECESSION parent={parent.Tag} born={born.Tag} culture={culture} " +
            $"regions={leaving.Count} core={leaving.Count(p => p.Cause == SecessionCause.Nationality)} " +
            $"people={people} militia={raised} revolt={revolt} outcome={outcome} reason={reason}");

        return new SecessionReport
        {
            Seceded = born,
            Parent = parent,
            Outcome = outcome,
            Culture = culture,
            Regions = leaving.Count,
            Population = people,
            ParentPopulation = was,
            Reason = reason,
            Revolt = revolt,
            Pieces = leaving
        };
    }

    /// <summary>
    /// A tag, a name, a colour, a capital and a politics.
    ///
    /// It does not inherit the arrangement it left: a state founded by people who have just
    /// refused to be governed by somebody is provisional by definition, and what it believes
    /// is the thing it seceded over. What it does inherit is the conditions - how literate
    /// it is, what it can produce, what a week of its economy is worth - because those are
    /// facts about the ground rather than about the flag that was over it.
    /// </summary>
    internal static bool CanFoundSettlement(Campaign c, Nation parent, string culture) =>
        FreeTag(c, culture, parent.Tag).Length > 0 && FreeMapIndex(c) > 0;

    internal static Nation FoundSettlement(Campaign c, Nation parent, IReadOnlyList<ScenarioState> regions, string culture) =>
        Found(c, parent, regions.Select(s => new SecessionPiece { MapIndex = s.MapIndex, Name = s.Name,
            Population = s.Population, Cause = SecessionCause.Nationality }).ToList(), culture,
            regions.Sum(s => s.Population), "national");

    internal static Nation FoundReleased(Campaign c, Nation parent, List<SecessionPiece> leaving, string culture, long people) =>
        Found(c, parent, leaving, culture, people);

    private static Nation Found(Campaign campaign, Nation parent, List<SecessionPiece> leaving,
        string culture, long people, string revolt = "")
    {
        var tag = FreeTag(campaign, culture, parent.Tag);
        if (tag.Length == 0) return null;
        var mapIndex = FreeMapIndex(campaign);
        if (mapIndex <= 0) return null;

        var weights = SecessionCore.Weights;
        var share = parent.Population <= 0 ? 0f : M.Clamp01(people / (float)parent.Population);

        SecessionPiece seat = null;
        foreach (var piece in leaving)
            if (piece.Cause is SecessionCause.Nationality or SecessionCause.Federal &&
                (seat is null || piece.Population > seat.Population)) seat = piece;
        seat ??= leaving[0];
        var seatState = campaign.States.Find(state => state.MapIndex == seat.MapIndex);
        var theirs = NamedForItsPeople(campaign, culture, leaving);

        var born = campaign.AddNation(new Nation
        {
            Tag = tag,
            Iso = tag,
            Name = NameFor(campaign, theirs ? culture : "", seatState?.Id ?? "", seat.Name, parent),
            Continent = parent.Continent,
            Region = parent.Region,
            Color = SecessionColours.Choose(campaign, parent, tag, leaving.Select(p => p.MapIndex),
                theirs ? DemographyCatalog.Culture(culture)?.Colour ?? "" : ""),
            MapIndex = mapIndex,
            CapitalName = seat.Name,
            Population = people,

            // The parent's per-head figures, because they describe the people who just left
            // rather than the government they left. The economy re-derives all of it on the
            // week; these are what it is worth until it does.
            Gdp = parent.Gdp * share,
            Treasury = MathF.Max(0f, parent.Treasury * share),
            ManpowerReserve = parent.ManpowerReserve * share,
            OutputScale = parent.OutputScale,
            StandardOfLiving = parent.StandardOfLiving,
            Literacy = parent.Literacy,
            LiteracyOffset = parent.LiteracyOffset,
            Productivity = parent.Productivity,
            Urbanisation = seatState?.Urbanization ?? parent.Urbanisation,
            AgricultureShare = parent.AgricultureShare,
            TaxRate = parent.TaxRate,
            WelfareSpending = parent.WelfareSpending,
            EducationSpending = parent.EducationSpending,

            // Everything is new and nothing is settled, and the war support of a country on
            // the morning it declared itself is the highest it will ever be.
            Stability = weights.Stability,
            WarSupport = weights.WarSupport,
            Government = "provisional_government",
            Ideology = "nationalism",
            DeclaredCulture = culture ?? "",
            PoliticsChangedDay = campaign.ElapsedDays
        });

        if (!string.IsNullOrEmpty(culture) && !born.FoundedCultures.Contains(culture))
            born.FoundedCultures.Add(culture);

        // The capital is a place, not a coordinate the parent had. Put it in the seat region
        // and let LocateCapitals work the rest out from the ground.
        var capital = CapitalOf(campaign, seatState);
        born.CapitalLatitude = capital.Latitude;
        born.CapitalLongitude = capital.Longitude;

        DeclarePeoples(campaign, born, culture, leaving);
        // The circumstances of the birth decide part of the name: a rising calls itself free,
        // a treaty release does not. Set before naming, because naming reads it.
        if (!string.IsNullOrWhiteSpace(revolt)) born.FoundedInRevolt = revolt;
        NationNaming.ApplyTo(campaign, born, parent,
            leaving.Select(piece => piece.MapIndex).ToArray(), "auto", culture);
        return born;
    }

    /// <summary>
    /// Say who the new country is for, which nothing else can say for it.
    ///
    /// The grievance it was founded on, and then every people that is actually a majority
    /// somewhere inside it - because a country founded on Tatarstan and two Russian-majority
    /// oblasts next door is a country for Tatars and Russians in the same plain sense that
    /// Belgium is a country for two peoples, and calling the second group foreigners in a
    /// state they make up half of would be a lie the whole simulation then acts on.
    ///
    /// Without this a new tag is for nobody: history/countries has never heard of it, so every
    /// person in it counts as a minority of it, and it dissolves into its own provinces
    /// within a few years of being founded.
    /// </summary>
    private static void DeclarePeoples(Campaign campaign, Nation born, string culture,
        List<SecessionPiece> leaving)
    {
        var peoples = new List<string>();
        if (!string.IsNullOrWhiteSpace(culture)) peoples.Add(culture);

        foreach (var piece in leaving)
        {
            var state = campaign.States.Find(candidate => candidate.MapIndex == piece.MapIndex);
            if (state is null || state.Population <= 0) continue;
            var largest = EconomySystem.Cultures(state).FirstOrDefault();
            if (largest.Culture is null) continue;
            if (largest.Size * 2 < state.Population) continue;
            if (!peoples.Contains(largest.Culture, StringComparer.OrdinalIgnoreCase))
                peoples.Add(largest.Culture);
        }

        // A country founded on ground with no majority anywhere is still for somebody: the
        // largest single people in it. Better a state that is for its plurality than a state
        // that is for nobody.
        if (peoples.Count == 0)
        {
            var tally = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var piece in leaving)
            {
                var state = campaign.States.Find(candidate => candidate.MapIndex == piece.MapIndex);
                if (state is null) continue;
                foreach (var (people, size) in EconomySystem.Cultures(state))
                    tally[people] = tally.GetValueOrDefault(people) + size;
            }
            var best = "";
            long bestSize = 0;
            foreach (var (people, size) in tally)
                if (size > bestSize) { best = people; bestSize = size; }
            if (best.Length > 0) peoples.Add(best);
        }

        born.FoundedCultures.Clear();
        born.FoundedCultures.AddRange(peoples);
        DemographyCatalog.Declare(born.Tag, peoples);
    }

    /// <summary>
    /// Whether a new country takes its people's name and colour - Kurdistan, Catalonia - or its
    /// seat region's, as Transnistria and Somaliland did.
    ///
    /// A people's name says whose state it is, and that is only true when they are the largest
    /// people on the ground leaving with them. The regions that came along for their own reasons
    /// can outnumber the core many times over: refugees from a war next door became the largest
    /// people of one border region of Egypt, four discontented provinces joined them, and the
    /// country that came out was "Palestine" - twenty-seven million Egyptians under another
    /// people's flag. A secession with nobody along for the ride - one region, or a scripted one
    /// that names its people - keeps the people's name, because there the claim is the point.
    /// </summary>
    public static bool NamedForItsPeople(Campaign campaign, string culture, IReadOnlyList<SecessionPiece> leaving) =>
        !string.IsNullOrWhiteSpace(culture) &&
        !leaving.Any(piece => piece.Cause == SecessionCause.Federal) &&
        (!leaving.Any(piece => piece.Cause == SecessionCause.Discontent) || LeadsTheGround(campaign, culture, leaving));

    /// <summary>Whether this people is the largest on the ground that is leaving, counted region by region.</summary>
    public static bool LeadsTheGround(Campaign campaign, string culture, IReadOnlyList<SecessionPiece> leaving)
    {
        if (campaign is null || string.IsNullOrWhiteSpace(culture) || leaving is null) return false;
        var tally = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var piece in leaving)
        {
            var state = campaign.States.Find(candidate => candidate.MapIndex == piece.MapIndex);
            if (state is null) continue;
            foreach (var (people, size) in EconomySystem.Cultures(state))
                tally[people] = tally.GetValueOrDefault(people) + size;
        }
        var theirs = tally.GetValueOrDefault(culture);
        if (theirs <= 0) return false;
        foreach (var (_, size) in tally)
            if (size > theirs) return false;
        return true;
    }

    /// <summary>Where the middle of the new country's largest region is.</summary>
    private static (float Latitude, float Longitude) CapitalOf(Campaign campaign, ScenarioState seat)
    {
        if (seat is null || campaign.ProvinceMap is null) return (0f, 0f);
        var topology = campaign.ProvinceMap;
        double latitude = 0, longitude = 0;
        var counted = 0;
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            if (topology.ProvinceToState[province] != (ushort)seat.MapIndex) continue;
            var centre = topology.CenterOf(province);
            latitude += centre.Latitude;
            longitude += centre.Longitude;
            counted++;
        }
        return counted == 0 ? (0f, 0f) : ((float)(latitude / counted), (float)(longitude / counted));
    }

    /// <summary>
    /// What to call it (item 42 on the list).
    ///
    /// It used to be the people's adjective: a Tatar secession was a country called "Tatar",
    /// and Koreans leaving South Korea founded "Korean", which is not a country name so much
    /// as a word left over. Now, in order: the name the people's state has where it has one
    /// (`country` in common/cultures - Kurdistan, Catalonia, Ichkeria); otherwise the region
    /// it was founded on, because that is what such a state is called - Tatarstan,
    /// Transnistria, Somaliland - and certainly when the people leaving are the same people
    /// as the country they left, where the adjective would be the country's own; and the
    /// adjective only when there is not even a region name to use.
    /// </summary>
    internal static string NameFor(Campaign campaign, string culture, string regionId, string seat, Nation parent)
    {
        var definition = DemographyCatalog.Culture(culture);
        var country = definition?.Country;
        if (!string.IsNullOrWhiteSpace(country))
        {
            var homeland = definition.CountryRegions.Count == 0 ||
                definition.CountryRegions.Any(prefix => regionId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            var exists = campaign.Nations.Any(n =>
                n != parent &&
                string.Equals(n.Name, country, StringComparison.OrdinalIgnoreCase) &&
                campaign.StatesOf(n).Length > 0);
            if (!homeland || exists)
                return StripAdministrativeSuffix(seat);
        }
        var state = campaign.States.Find(candidate =>
            string.Equals(candidate.Id, regionId, StringComparison.OrdinalIgnoreCase));
        var regions = state is null ? Array.Empty<int>() : new[] { state.MapIndex };
        var (kind, id) = NationNaming.BaseFor(campaign, parent, regions, culture, "auto");
        var composed = new Nation
        {
            Name = parent?.Name ?? "",
            FoundNameKind = kind,
            FoundNameId = id,
            FoundNameBase = StripAdministrativeSuffix(seat),
            FoundNameForm = NationNaming.FormFor("provisional_government", "nationalism", "")
        };
        var name = NationNaming.Render(campaign, composed, false);
        if (!string.IsNullOrWhiteSpace(name)) return name;
        if (!string.IsNullOrWhiteSpace(seat)) return StripAdministrativeSuffix(seat);
        var people = DemographyCatalog.CultureKey(culture);
        return string.IsNullOrWhiteSpace(people) ? parent?.Name ?? "" : people;
    }

    internal static string StripAdministrativeSuffix(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var trimmed = name.Trim();
        foreach (var suffix in new[]
        {
            " Governorate", " Oblast", " Krai", " Province", " Prefecture",
            " Department", " Region", " District", " Voivodeship", " County"
        })
        {
            if (trimmed.Length > suffix.Length + 2 &&
                trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return trimmed[..^suffix.Length].Trim();
        }
        return trimmed;
    }

    private static Nation FindMatchingMovement(Campaign campaign, IReadOnlyList<SecessionPiece> leaving)
    {
        if (campaign?.ProvinceMap is null || leaving is null || leaving.Count == 0) return null;
        var leavingIndices = leaving.Select(p => p.MapIndex).ToHashSet();
        foreach (var nation in campaign.Nations)
        {
            if (nation.Movement is not { Landless: true }) continue;
            // Only match a movement if the seceding ground actually overlaps the movement's defined authentic homeland.
            // A temporary exile office / headquarters outside the homeland must NEVER confer ownership of a foreign region!
            var def = Movements.DefinitionOf(nation);
            if (def?.Homeland is null || def.Homeland.Count == 0) continue;
            var homelandIndices = def.Homeland
                .Select(id => Movements.StateById(campaign, id)?.MapIndex)
                .Where(idx => idx.HasValue && idx.Value > 0)
                .Select(idx => idx.Value)
                .ToHashSet();
            // And a real overlap: half the people leaving live in its homeland. One shared region
            // let an exile Dagestani state come back as the ruler of Rostov and Voronezh.
            long inside = 0, all = 0;
            foreach (var piece in leaving)
            {
                all += Math.Max(0, piece.Population);
                if (homelandIndices.Contains(piece.MapIndex)) inside += Math.Max(0, piece.Population);
            }
            if (inside > 0 && inside * 2 >= all)
                return nation;
        }
        return null;
    }

    /// <summary>
    /// Three letters nothing else is using. The people's own first, because TAT reads as
    /// something; a numbered fallback after, because a tag that collides is a far worse
    /// thing than a tag that is ugly.
    /// </summary>
    private static string FreeTag(Campaign campaign, string culture, string avoid)
    {
        var letters = new string((culture ?? "").Where(char.IsLetter).ToArray()).ToUpperInvariant();
        if (letters.Length < 2) letters = (avoid ?? "XX").ToUpperInvariant();

        var candidates = new List<string>();
        if (letters.Length >= 3) candidates.Add(letters[..3]);
        if (letters.Length >= 4) candidates.Add(string.Concat(letters[0], letters[2..4]));
        for (var suffix = 1; suffix <= 99; suffix++)
            candidates.Add($"{letters[..1]}{suffix:00}");

        foreach (var candidate in candidates)
        {
            if (string.Equals(candidate, avoid, StringComparison.OrdinalIgnoreCase)) continue;
            if (campaign.NationByTag(candidate) is not null) continue;
            return candidate;
        }
        return "";
    }

    /// <summary>
    /// A colour index the political map can paint, above everything the scenario and its
    /// mods declared. The palette holds 65,535 of them and 261 are in use, so this is only
    /// ever about not standing on somebody.
    /// </summary>
    private static int FreeMapIndex(Campaign campaign)
    {
        var taken = new HashSet<int>();
        foreach (var nation in campaign.Nations) taken.Add(nation.MapIndex);
        for (var index = FirstDynamicMapIndex; index < 65535; index++)
            if (!taken.Contains(index)) return index;
        return 0;
    }

    /// <summary>
    /// Where a country the campaign invented starts numbering. The authored table ends at
    /// 261 and a mod would have to declare six hundred countries to reach this, so a new
    /// state can never be handed an index somebody is already painted in.
    /// </summary>
    public const int FirstDynamicMapIndex = 1000;

    /// <summary>
    /// What a new country has instead of an army.
    ///
    /// People who have just declared something are exactly the condition the militia rules
    /// read, so this stands up what those rules would have produced given a few weeks -
    /// now, because the few weeks are the ones it may not be given. Spread across its
    /// regions largest first, so a country of one region does not put four formations in
    /// one province and leave the rest of itself open.
    /// </summary>
    private static int RaiseFoundingMilitia(Campaign campaign, Nation born)
    {
        var wanted = SecessionCore.Weights.MilitiaFormations;
        if (wanted <= 0) return 0;

        var regions = campaign.StatesOf(born.MapIndex)
            .OrderByDescending(state => state.Population)
            .ToList();
        if (regions.Count == 0) return 0;

        var raised = 0;
        for (var attempt = 0; attempt < wanted; attempt++)
            if (MilitiaCore.Found(campaign, born, regions[attempt % regions.Count])) raised++;
        return raised;
    }
}
