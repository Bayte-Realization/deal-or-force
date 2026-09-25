using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// What every good is made out of, and who eats it - as data rather than as code.
///
/// This used to be a hundred and thirty lines of hand-written arithmetic in the middle of
/// the market pass: `Add(nation, Commodity.Aluminium, industry * 0.65f + smelter * 1.90f +
/// bauxiteOutput * 0.30f) * poweredWorks, ...)`, twenty-five times over. Every number in it
/// was a design decision and not one of them could be reached from a mod. A modder could
/// rename aluminium, reprice it and rewrite its description; what aluminium actually *was*
/// - bauxite, a smelter and cheap electricity - was compiled in.
///
/// That is the wrong way round. The prices are the least interesting thing about a good and
/// the chain is the most: a chain is what makes a trade partner matter, and a partner who
/// matters is what makes an embargo a weapon and a war a decision. So the chains moved out
/// here, into `common/production/`, where the same folder rule applies as everywhere else -
/// files read in name order, later files win, a mod drops `50_my_chains.json` beside the
/// base game's and retunes three recipes without forking anything.
///
/// The format is deliberately small. A recipe names the good it makes, a sum of weighted
/// terms that make it, a sum of weighted terms that consume it, and the goods whose supply
/// last week scales it. A term is a product of named factors, written with `*`, so
/// `population*standard_of_living` means what it looks like. That is enough to say
/// everything the compiled version said, and it does not need an expression parser or a
/// scripting language to do it.
/// </summary>
public static class ProductionChains
{
    // ---------------------------------------------------------------- factors -----------

    /// <summary>
    /// Everything a recipe is allowed to be made out of, and the only vocabulary there is.
    ///
    /// Deliberately a fixed list rather than an open one. A factor is a quantity the
    /// simulation actually tracks; letting a mod invent a new one would mean letting it ask
    /// the simulation a question the simulation cannot answer. What is open is how they are
    /// combined, which is where all the design is.
    /// </summary>
    private const int SlotPopulation = 0;
    private const int SlotUrbanization = 1;
    private const int SlotStandardOfLiving = 2;
    private const int SlotExtractionEfficiency = 3;
    private const int SlotOutputFactor = 4;
    private const int SlotProjects = 5;
    private const int SlotDivisions = 6;
    private const int SlotDivisionsAtWar = 7;
    private const int SlotHulls = 8;
    private const int SlotGdp = 9;
    private const int SlotArmsProductivity = 10;

    /// <summary>
    /// Two compound quantities that are concepts rather than expressions: how good this
    /// region is at getting things out of its own ground, and how good it is at forestry.
    /// Every deposit in every recipe is written against one of them, so they are worked out
    /// once here instead of being spelled out twenty times over in the data.
    /// </summary>
    private const int SlotTimberYield = 11;
    private const int FixedSlots = 12;

    // Sized for what a mod might add rather than for what the enums name, because a good or
    // a building declared in data is a number past the last one the enum has a name for, and
    // it indexes this table like any other. Sixty-four spare of each is a table of a few
    // hundred floats - nothing next to being unable to add a twenty-first good.
    private const int ModdedHeadroom = 64;
    private static readonly int BuildingKinds = Enum.GetValues<BuildingKind>().Length + ModdedHeadroom;
    private static readonly int CommodityKinds = Enum.GetValues<Commodity>().Length + ModdedHeadroom;

    /// <summary>Staffed building count: the count times the nation's output factor.</summary>
    private static int StaffedSlot(BuildingKind kind) => FixedSlots + (int)kind;

    /// <summary>The raw count, unstaffed. Infrastructure and extraction are read this way.</summary>
    private static int RawSlot(BuildingKind kind) => FixedSlots + BuildingKinds + (int)kind;

    /// <summary>What is in the ground here, before anybody digs it up.</summary>
    private static int DepositSlot(Commodity commodity) =>
        FixedSlots + BuildingKinds * 2 + (int)commodity;

    private static int SlotCount => FixedSlots + BuildingKinds * 2 + CommodityKinds;

    private static readonly float[] Slots = new float[FixedSlots + BuildingKinds * 2 + CommodityKinds];

    // ---------------------------------------------------------------- recipes -----------

    /// <summary>One weighted product of factors: the `industry*0.65` in a recipe's sum.</summary>
    private readonly struct Term
    {
        public readonly int[] Factors;
        public readonly float Weight;
        public Term(int[] factors, float weight) { Factors = factors; Weight = weight; }

        public float Of(float[] slots)
        {
            var value = Weight;
            foreach (var slot in Factors) value *= slots[slot];
            return value;
        }
    }

    private sealed class Recipe
    {
        public string Id = "";
        public Commodity Good;

        /// <summary>Whether this is worked out per region and summed, or once for the country.</summary>
        public bool National;

        public Term[] Output = Array.Empty<Term>();
        public Term[] Demand = Array.Empty<Term>();

        /// <summary>Goods whose supply scales the output, with the floor a shortage cannot pass.</summary>
        public (Commodity Good, float Floor)[] Needs = Array.Empty<(Commodity, float)>();

        /// <summary>
        /// Goods whose *shortage* is subtracted from the output.
        ///
        /// The difference from `Needs` matters. A recipe that needs electricity makes less
        /// of its own good when the lights go out. A recipe with a shortfall term makes a
        /// *negative* contribution to somebody else's good - which is how a country cut off
        /// from fertiliser loses a quarter of a harvest it was already growing. Output with
        /// a shortfall term is allowed below zero; output without one is not.
        /// </summary>
        public (Commodity Good, float Floor)[] Shortfall = Array.Empty<(Commodity, float)>();

        public float Sum(Term[] terms, float[] slots)
        {
            var total = 0f;
            foreach (var term in terms) total += term.Of(slots);
            return total;
        }
    }

    private static readonly List<Recipe> Regional = new();
    private static readonly List<Recipe> National = new();

    /// <summary>Which slots any recipe actually asks about, so the rest are never filled.</summary>
    private static readonly List<int> UsedBuildingsStaffed = new();
    private static readonly List<int> UsedBuildingsRaw = new();
    private static readonly List<Commodity> UsedDeposits = new();

    public static int RecipeCount => Regional.Count + National.Count;

    /// <summary>Every good some recipe makes, for anything that wants to check coverage.</summary>
    public static IEnumerable<Commodity> GoodsProduced()
    {
        foreach (var recipe in Regional) if (recipe.Output.Length > 0) yield return recipe.Good;
        foreach (var recipe in National) if (recipe.Output.Length > 0) yield return recipe.Good;
    }

    /// <summary>And every good some recipe wants. A good nobody wants is worth nothing.</summary>
    public static IEnumerable<Commodity> GoodsConsumed()
    {
        foreach (var recipe in Regional) if (recipe.Demand.Length > 0) yield return recipe.Good;
        foreach (var recipe in National) if (recipe.Demand.Length > 0) yield return recipe.Good;
        foreach (var recipe in Regional) foreach (var need in recipe.Needs) yield return need.Good;
        foreach (var recipe in Regional) foreach (var lack in recipe.Shortfall) yield return lack.Good;
    }

    /// <summary>
    /// What a building makes and what it burns, read out of the recipes rather than written
    /// beside them, so a tooltip cannot disagree with the economy and a mod's new chain
    /// describes itself. A good is made when the building is a factor in a term of its
    /// output - a mine through the efficiency its deposits are dug at - and used when the
    /// building is a factor in its demand, or the good is a need of something it makes.
    /// </summary>
    public static void RolesOf(BuildingKind kind, List<Commodity> makes, List<Commodity> uses)
    {
        makes.Clear();
        uses.Clear();
        var staffed = StaffedSlot(kind);
        var raw = RawSlot(kind);
        var compound = kind == BuildingKind.ExtractionIndustry ? SlotExtractionEfficiency : -1;
        foreach (var recipes in new[] { Regional, National })
            foreach (var recipe in recipes)
            {
                if (Mentions(recipe.Output, staffed, raw, compound))
                {
                    if (!makes.Contains(recipe.Good)) makes.Add(recipe.Good);
                    foreach (var need in recipe.Needs)
                        if (!uses.Contains(need.Good)) uses.Add(need.Good);
                }
                if (Mentions(recipe.Demand, staffed, raw, -1) && !uses.Contains(recipe.Good)) uses.Add(recipe.Good);
            }
    }

    private static bool Mentions(Term[] terms, int staffed, int raw, int compound)
    {
        foreach (var term in terms)
            foreach (var slot in term.Factors)
                if (slot == staffed || slot == raw || slot == compound) return true;
        return false;
    }

    // ---------------------------------------------------------------- loading -----------

    private static readonly HashSet<BuildingKind> StaffedSeen = new();
    private static readonly HashSet<BuildingKind> RawSeen = new();
    private static readonly HashSet<Commodity> DepositsSeen = new();

    static ProductionChains() => ResetCatalog();

    /// <summary>Back to what the game ships, before any mod is read.</summary>
    public static void ResetCatalog(List<string> errors = null)
    {
        Regional.Clear();
        National.Clear();
        StaffedSeen.Clear();
        RawSeen.Clear();
        DepositsSeen.Clear();

        foreach (var document in GameDataLoader.DataDocuments("common", "production"))
            using (document)
                Merge(document.RootElement, errors);

        if (RecipeCount == 0)
            errors?.Add("common/production describes no recipes, so nothing in the world is made.");
    }

    /// <summary>
    /// Add or replace recipes from one file's worth of JSON.
    ///
    /// Later wins, by recipe name, which is the rule the rest of the data follows and the
    /// whole reason a mod can retune one chain and inherit the other thirty-nine.
    /// </summary>
    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            var recipe = Read(entry.Name, entry.Value, errors);
            if (recipe is null) continue;
            var into = recipe.National ? National : Regional;
            into.RemoveAll(existing => existing.Id == recipe.Id);
            into.Add(recipe);
        }

        UsedBuildingsStaffed.Clear();
        UsedBuildingsRaw.Clear();
        UsedDeposits.Clear();
        foreach (var kind in StaffedSeen) UsedBuildingsStaffed.Add((int)kind);
        foreach (var kind in RawSeen) UsedBuildingsRaw.Add((int)kind);
        foreach (var commodity in DepositsSeen) UsedDeposits.Add(commodity);
    }

    /// <summary>
    /// Read a file's recipes and report what is wrong with them, without keeping any.
    ///
    /// For the bench, and for anything else that wants to know a mod is sound before the
    /// mod is live. The only trace it leaves is that a factor named here will be filled in
    /// the slot table afterwards even if no live recipe asks for it, which costs one float.
    /// </summary>
    public static void Validate(JsonElement root, List<string> problems)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            Read(entry.Name, entry.Value, problems);
        }
    }

    public static void Merge(string json, List<string> errors = null)
    {
        using var document = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
        Merge(document.RootElement, errors);
    }

    private static Recipe Read(string id, JsonElement value, List<string> errors)
    {
        if (!value.TryGetProperty("good", out var good) || good.ValueKind != JsonValueKind.String ||
            !CommodityCatalog.TryParse(good.GetString(), out var commodity))
        {
            errors?.Add($"Production recipe '{id}' does not name a good that exists.");
            return null;
        }

        var recipe = new Recipe { Id = id, Good = commodity };
        recipe.National = value.TryGetProperty("scope", out var scope) &&
                          scope.ValueKind == JsonValueKind.String &&
                          string.Equals(scope.GetString(), "nation", StringComparison.OrdinalIgnoreCase);
        recipe.Output = Terms(id, value, "output", errors);
        recipe.Demand = Terms(id, value, "demand", errors);
        recipe.Needs = Inputs(id, value, "needs", errors);
        recipe.Shortfall = Inputs(id, value, "shortfall", errors);
        if (recipe.Output.Length == 0 && recipe.Demand.Length == 0)
            errors?.Add($"Production recipe '{id}' neither makes nor consumes anything.");
        return recipe;
    }

    private static Term[] Terms(string id, JsonElement value, string property, List<string> errors)
    {
        if (!value.TryGetProperty(property, out var table) || table.ValueKind != JsonValueKind.Object)
            return Array.Empty<Term>();

        var terms = new List<Term>();
        foreach (var entry in table.EnumerateObject())
        {
            if (!entry.Value.TryGetSingle(out var weight)) continue;
            var factors = Factors(id, entry.Name, errors);
            if (factors is null) continue;
            terms.Add(new Term(factors, weight));
        }
        return terms.ToArray();
    }

    /// <summary>
    /// One term's factors, from `population*standard_of_living` to a bare building name.
    ///
    /// Multiplication is the only operator, which is not a limitation worth apologising for:
    /// every sum in the compiled version was a sum of products, and a sum of products is
    /// what the recipe's own table already is.
    /// </summary>
    private static int[] Factors(string id, string term, List<string> errors)
    {
        var pieces = term.Split('*', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var slots = new int[pieces.Length];
        for (var index = 0; index < pieces.Length; index++)
        {
            var slot = Factor(pieces[index]);
            if (slot < 0)
            {
                errors?.Add($"Production recipe '{id}' asks for '{pieces[index]}', which is not a factor.");
                return null;
            }
            slots[index] = slot;
        }
        return slots;
    }

    private static int Factor(string name)
    {
        switch (name.ToLowerInvariant())
        {
            case "population": return SlotPopulation;
            case "urbanization": return SlotUrbanization;
            case "standard_of_living": return SlotStandardOfLiving;
            case "extraction_efficiency": return SlotExtractionEfficiency;
            case "timber_yield": return SlotTimberYield;
            case "output_factor": return SlotOutputFactor;
            case "projects": return SlotProjects;
            case "divisions": return SlotDivisions;
            case "divisions_at_war": return SlotDivisionsAtWar;
            case "hulls": return SlotHulls;
            case "gdp": return SlotGdp;
            case "arms_productivity": return SlotArmsProductivity;
        }

        if (name.StartsWith("deposit:", StringComparison.OrdinalIgnoreCase) &&
            CommodityCatalog.TryParse(name["deposit:".Length..], out var commodity))
        {
            if ((int)commodity >= CommodityKinds) return -1;
            DepositsSeen.Add(commodity);
            return DepositSlot(commodity);
        }
        if (name.StartsWith("raw:", StringComparison.OrdinalIgnoreCase) &&
            BuildingCatalog.TryParse(name["raw:".Length..], out var unstaffed))
        {
            if ((int)unstaffed >= BuildingKinds) return -1;
            RawSeen.Add(unstaffed);
            return RawSlot(unstaffed);
        }
        if (BuildingCatalog.TryParse(name, out var kind))
        {
            if ((int)kind >= BuildingKinds) return -1;
            StaffedSeen.Add(kind);
            RawSeen.Add(kind);
            return StaffedSlot(kind);
        }
        return -1;
    }

    private static (Commodity, float)[] Inputs(string id, JsonElement value, string property,
        List<string> errors)
    {
        if (!value.TryGetProperty(property, out var table) || table.ValueKind != JsonValueKind.Object)
            return Array.Empty<(Commodity, float)>();

        var inputs = new List<(Commodity, float)>();
        foreach (var entry in table.EnumerateObject())
        {
            if (!CommodityCatalog.TryParse(entry.Name, out var commodity))
            {
                errors?.Add($"Production recipe '{id}' {property} names '{entry.Name}', which is not a good.");
                continue;
            }
            inputs.Add((commodity, entry.Value.TryGetSingle(out var floor) ? M.Clamp01(floor) : 0f));
        }
        return inputs.ToArray();
    }

    // ---------------------------------------------------------------- running -----------

    /// <summary>
    /// Every regional recipe, for one region.
    ///
    /// The slot table is filled first and only where some recipe asked: a world with no
    /// shipyards in any recipe never reads a shipyard count. Then each recipe is a sum of
    /// products over that table, which is the same arithmetic the compiled version did in
    /// the same order, through one level of indirection.
    /// </summary>
    public static void RunRegion(Nation nation, ScenarioState state, float outputFactor)
    {
        if (Regional.Count == 0) return;
        var slots = Slots;
        Array.Clear(slots, 0, SlotCount);

        slots[SlotPopulation] = state.Population / 1_000_000f;
        slots[SlotUrbanization] = state.Urbanization;
        slots[SlotStandardOfLiving] = state.StandardOfLiving;
        slots[SlotOutputFactor] = outputFactor;

        foreach (var kind in UsedBuildingsRaw)
            slots[FixedSlots + BuildingKinds + kind] = state.Buildings.GetValueOrDefault((BuildingKind)kind);
        foreach (var kind in UsedBuildingsStaffed)
            slots[FixedSlots + kind] = slots[FixedSlots + BuildingKinds + kind] * outputFactor;
        foreach (var commodity in UsedDeposits)
            slots[DepositSlot(commodity)] = state.ResourceDeposits.GetValueOrDefault(commodity);

        // Read straight from the region rather than from the table, because the two
        // compound factors below are built out of them whether or not any recipe named them.
        var infrastructure = (float)state.Buildings.GetValueOrDefault(BuildingKind.Infrastructure);
        var extraction = (float)state.Buildings.GetValueOrDefault(BuildingKind.ExtractionIndustry);
        slots[SlotExtractionEfficiency] =
            (0.32f + extraction * 0.72f + infrastructure * 0.11f) * outputFactor;
        slots[SlotTimberYield] =
            0.55f + state.Buildings.GetValueOrDefault(BuildingKind.Agriculture) * outputFactor * 0.06f +
            extraction * 0.20f;

        Run(Regional, nation, slots);
    }

    /// <summary>What a country as a whole makes and spends, once, after its regions.</summary>
    public static void RunNation(Nation nation, float projects, float divisions, float hulls, bool atWar)
    {
        if (National.Count == 0) return;
        var slots = Slots;
        Array.Clear(slots, 0, SlotCount);
        slots[SlotProjects] = projects;
        slots[SlotDivisions] = divisions;
        slots[SlotDivisionsAtWar] = atWar ? divisions : 0f;
        slots[SlotHulls] = hulls;
        slots[SlotGdp] = nation.Gdp;
        slots[SlotArmsProductivity] = M.Clamp(nation.Productivity, 0.55f, 1.55f);
        Run(National, nation, slots);
    }

    private static void Run(List<Recipe> recipes, Nation nation, float[] slots)
    {
        foreach (var recipe in recipes)
        {
            if (!nation.Market.TryGetValue(recipe.Good, out var quote)) continue;

            if (recipe.Output.Length > 0)
            {
                var output = recipe.Sum(recipe.Output, slots);
                foreach (var need in recipe.Needs) output *= Supplied(nation, need.Good, need.Floor);
                foreach (var lack in recipe.Shortfall) output *= Supplied(nation, lack.Good, lack.Floor) - 1f;
                // A recipe with a shortfall term is a penalty by construction and may go
                // below zero; anything else may not, because a factory does not un-make
                // things when it is short of coal.
                quote.Production += recipe.Shortfall.Length > 0 ? output : MathF.Max(0f, output);
            }

            if (recipe.Demand.Length > 0)
                quote.Demand += MathF.Max(0f, recipe.Sum(recipe.Demand, slots));
        }
    }

    /// <summary>
    /// How much of an input actually turned up, floored so that a chain starved of one thing
    /// slows rather than stops. Read from last week's clearing, which is the only honest
    /// answer available while this week's is still being worked out - and which is what
    /// gives an embargo a lag before it bites, exactly as one has in life.
    /// </summary>
    private static float Supplied(Nation nation, Commodity commodity, float floor) =>
        nation.Market.TryGetValue(commodity, out var quote)
            ? floor + (1f - floor) * M.Clamp01(quote.Satisfaction)
            : 1f;
}
