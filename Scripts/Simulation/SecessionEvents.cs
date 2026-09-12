using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>Why one region ended up in the new country.</summary>
public enum SecessionCause
{
    /// <summary>It is their country. The people whose grievance this whole thing is.</summary>
    Nationality,

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
        BuildAdjacency(campaign, owned);

        var core = Core(campaign, parent, ripe, out var culture);
        core = Cap(parent, core);
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
                Cause = SecessionCause.Nationality,
                Population = state.Population
            });
        }
        if (people < weights.MinimumPopulation) return null;

        Accrete(campaign, parent, owned, taken, leaving, ref people);
        Trim(parent, leaving, ref people);
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
    /// The grievance the whole thing is about: the biggest contiguous group of ripe regions
    /// that share a people the state is not for.
    /// </summary>
    private static List<ScenarioState> Core(Campaign campaign, Nation owner,
        List<ScenarioState> ripe, out string culture)
    {
        culture = "";
        var best = new List<ScenarioState>();
        long bestPeople = 0;

        var byCulture = new Dictionary<string, List<ScenarioState>>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in ripe)
        {
            var largest = EconomySystem.Cultures(state).FirstOrDefault();
            if (largest.Culture is null || largest.Size <= 0) continue;
            // Only a people the state is not for. A region full of the country's own people
            // that has had enough is a revolution, and that is a different mechanic.
            if (DemographyCatalog.IsPrimary(owner.Tag, largest.Culture)) continue;
            if (!byCulture.TryGetValue(largest.Culture, out var list))
                byCulture[largest.Culture] = list = new List<ScenarioState>();
            list.Add(state);
        }

        foreach (var (people, states) in byCulture)
            foreach (var bloc in Contiguous(states))
            {
                long here = 0;
                foreach (var state in bloc) here += state.Population;
                if (best.Count > 0 && here <= bestPeople) continue;
                best = bloc;
                bestPeople = here;
                culture = people;
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
    private static void Accrete(Campaign campaign, Nation parent, ScenarioState[] owned,
        HashSet<int> taken, List<SecessionPiece> leaving, ref long people)
    {
        var weights = SecessionCore.Weights;
        if (weights.JoinLimit <= 0) return;

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
                if (taken.Contains(state.MapIndex)) continue;
                if (!Touches(taken, state.MapIndex)) continue;

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
    private static SecessionReport Secede(Campaign campaign, Nation parent,
        List<SecessionPiece> leaving, string culture, long people)
    {
        var was = parent.Population;
        var born = Found(campaign, parent, leaving, culture, people);
        if (born is null) return null;

        var going = new HashSet<int>();
        foreach (var piece in leaving) going.Add(piece.MapIndex);

        var topology = campaign.ProvinceMap;
        var provinces = new List<int>();
        for (var province = 1; province <= topology.ProvinceCount; province++)
            if (going.Contains(topology.ProvinceToState[province])) provinces.Add(province);

        // Not a conquest. It is standing on the ground it already lived on, and a state born
        // an international pariah for that would be the wrong answer twice over.
        StatePartition.Annex(campaign, provinces, born, asConquest: false);

        foreach (var piece in leaving)
            foreach (var state in campaign.States)
                if (state.MapIndex == piece.MapIndex) { state.SecessionPressure = 0f; break; }

        parent.LastSecessionDay = campaign.ElapsedDays;
        born.LastSecessionDay = campaign.ElapsedDays;
        campaign.LocateCapitals();

        // Its baseline is its own. A country that has never been this country before has no
        // institutions to take credit for, so what the formula explains is all there is -
        // which is also what makes a new state fragile in exactly the way it should be.
        born.StabilityBias = 0f;
        born.StabilityBias = born.Stability - StabilityCore.Target(campaign, born);

        var raised = RaiseFoundingMilitia(campaign, born);
        var outcome = SecessionCore.Response(campaign, parent, leaving, out var reason);
        if (outcome == SecessionOutcome.Contested)
        {
            var war = CampaignDiplomacy.DeclareWar(campaign, parent.Id, born.Id,
                leaving[0].MapIndex, "secession");
            // Refused, and unable to do anything about it - a truce, an alliance, no way to
            // reach them. That is an acceptance whatever the cabinet thinks, and recording
            // it as one keeps the state of the world honest.
            if (war is null) outcome = SecessionOutcome.Accepted;
        }
        if (outcome == SecessionOutcome.Accepted)
            OpinionSystem.Remember(campaign, CampaignDiplomacy.Get(campaign, born.Id, parent.Id), "let_us_go");
        OpinionSystem.Remember(campaign, CampaignDiplomacy.Get(campaign, parent.Id, born.Id), "seceded_from_us");

        campaign.MarkStrategicChange();
        DiagnosticLogs.Write("simulation",
            $"SECESSION parent={parent.Tag} born={born.Tag} culture={culture} " +
            $"regions={leaving.Count} core={leaving.Count(p => p.Cause == SecessionCause.Nationality)} " +
            $"people={people} militia={raised} outcome={outcome} reason={reason}");

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
    private static Nation Found(Campaign campaign, Nation parent, List<SecessionPiece> leaving,
        string culture, long people)
    {
        var tag = FreeTag(campaign, culture, parent.Tag);
        if (tag.Length == 0) return null;
        var mapIndex = FreeMapIndex(campaign);
        if (mapIndex <= 0) return null;

        var weights = SecessionCore.Weights;
        var share = parent.Population <= 0 ? 0f : M.Clamp01(people / (float)parent.Population);

        SecessionPiece seat = null;
        foreach (var piece in leaving)
            if (piece.Cause == SecessionCause.Nationality &&
                (seat is null || piece.Population > seat.Population)) seat = piece;
        seat ??= leaving[0];
        var seatState = campaign.States.Find(state => state.MapIndex == seat.MapIndex);

        var born = campaign.AddNation(new Nation
        {
            Tag = tag,
            Iso = tag,
            Name = NameFor(culture, seat.Name),
            Continent = parent.Continent,
            Region = parent.Region,
            Color = ColourFor(culture, parent),
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
            PoliticsChangedDay = campaign.ElapsedDays
        });

        // The capital is a place, not a coordinate the parent had. Put it in the seat region
        // and let LocateCapitals work the rest out from the ground.
        var capital = CapitalOf(campaign, seatState);
        born.CapitalLatitude = capital.Latitude;
        born.CapitalLongitude = capital.Longitude;

        DeclarePeoples(campaign, born, culture, leaving);
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
    /// Without this a new tag is for nobody: `history/pops` has never heard of it, so every
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
    /// What to call it. The people's own name where there is one, because "Tatarstan" is
    /// what a Tatar secession is called; the seat region's name otherwise, which is what
    /// a secession with no single people has always been called.
    /// </summary>
    private static string NameFor(string culture, string seat)
    {
        var people = DemographyCatalog.CultureName(culture);
        return string.IsNullOrWhiteSpace(people) ? seat : people;
    }

    /// <summary>
    /// A colour that is not the colour it left. The people's own where the catalogue has
    /// one - which is the map a player can read at a glance - and the parent's, shifted far
    /// enough to be a different country, where it does not.
    /// </summary>
    private static string ColourFor(string culture, Nation parent)
    {
        var definition = DemographyCatalog.Culture(culture);
        if (definition is not null && !string.IsNullOrWhiteSpace(definition.Colour) &&
            !string.Equals(definition.Colour, parent.Color, StringComparison.OrdinalIgnoreCase))
            return definition.Colour;

        var packed = Hex(parent.Color);
        var red = (packed >> 16) & 0xFF;
        var green = (packed >> 8) & 0xFF;
        var blue = packed & 0xFF;
        // Rotate the channels and lift it. Cheap, deterministic, and reliably a colour
        // nobody mistakes for the one next to it.
        var shifted = (Math.Min(255, blue + 40) << 16) | (Math.Min(255, red + 20) << 8) | green;
        return $"#{shifted:x6}";
    }

    private static int Hex(string colour)
    {
        var text = (colour ?? "").TrimStart('#');
        return text.Length >= 6 && int.TryParse(text[..6],
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : 0x6E7B8B;
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
