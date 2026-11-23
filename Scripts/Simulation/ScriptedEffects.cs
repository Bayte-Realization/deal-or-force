using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DealOrForce.Simulation;

/// <summary>
/// The scripted layer every piece of content writes its consequences in: events,
/// decisions and mod scripts all resolve to the same small effect vocabulary instead of
/// a closed C# enum a modder cannot extend.
///
/// ═══════════════════════════════════════════════════════════════════════════════
/// FOR MODDERS — the effect grammar, the whole language in one place:
/// ═══════════════════════════════════════════════════════════════════════════════
///
/// FORMAT:
///   "effects": { "key": value, "key_suffix": value, ... }
///
/// SUFFIXES (what the operation is):
///   (none)     — ADD the value: `"stability": 0.02` adds 2 percentage points.
///   _percent   — SCALE by (1 + value): `"stability_percent": 0.05` adds 5% of current.
///   _mult      — MULTIPLY: `"gdp_mult": 1.1` sets GDP to 110% of what it was.
///   _set       — ASSIGN: `"inflation_set": 0.03` sets inflation to exactly 3%.
///
/// PREFIXES (what the target is):
///   (none)     — the nation itself.
///   state.     — every region the nation owns: `"state.radicalism": -0.01`.
///   army.      — every division: `"army.organisation": 0.05`.
///   fleet.     — every fleet: `"fleet.readiness": 0.1`.
///   stockpile. — strategic depot reserve: `"stockpile.ammunition": 25`.
///   feature.   — era/mechanic flag: `"feature.aviation_set": 1` (or `"unlock_feature"` / `"restrict_feature"`).
///
/// WHAT'S AVAILABLE (the most-used keys, money in billions, shares 0..1):
///   NATION: treasury, public_debt, gdp, gdp_percent, stability, war_support,
///           inflation, literacy, standard_of_living, productivity, population,
///           political_points, army/naval/air_experience, manpower, notoriety,
///           nuclear_posture, grand_project_progress
///   STATE:  state.radicalism, state.stability, state.standard_of_living,
///           state.literacy, state.war_damage, state.urbanization
///   ARMY:   army.organisation, army.entrenchment, army.experience, army.equipment
///   FLEET:  fleet.readiness, fleet.supply
///   OTHER:  research, construction_capacity, market_access, stockpile.&lt;good&gt;,
///           unlock_feature / restrict_feature / disable_feature,
///           add_modifier / remove_modifier for spirits, use — a scripted effect library
///   INCOME: "income_penalty": { "share": 0.08, "days": 120 } — a timed cut of 8% to the
///           country's gross budget revenue (not its expenses) for 120 days. Penalties add up,
///           capped by IncomePenaltyCap (common/defines/93_income_penalties.json).
///
/// UNKNOWN KEYS: never fail silently. They are reported at load with a closest-key
/// suggestion and the list of valid keys, so a typo says so on the first launch.
///
/// UNITS: money is in billions ($1 = one billion). Stability/literacy/support are
/// 0..1 shares. Inflation is a rate (0.03 = 3%). Days are campaign days. These
/// units do not change between JSON and YAML mod formats.
/// ═══════════════════════════════════════════════════════════════════════════════
/// </summary>
public static partial class ScriptedEffects
{
    // МОДДЕРАМ: деньги здесь в миллиардах, доли — 0..1, дни — целые дни кампании.
    // stability: 0.02 добавляет 2 процентных пункта, stability_percent: 0.02 — 2%
    // от текущего значения. Формат файла (JSON/YAML) эти единицы не меняет.
    internal enum Operation { Add, Percent, Mult, Set }

    private sealed class Field
    {
        public string Name = "";
        public Func<Nation, float> GetNation;
        public Action<Nation, float> SetNation;
        public Func<ScenarioState, float> GetState;
        public Action<ScenarioState, float> SetState;
        public Func<Army, float> GetArmy;
        public Action<Army, float> SetArmy;
        public Func<Fleet, float> GetFleet;
        public Action<Fleet, float> SetFleet;
        public float Min = float.NegativeInfinity;
        public float Max = float.PositiveInfinity;
        public string Help = "";
    }

    private static readonly Dictionary<string, Field> Fields = new(StringComparer.OrdinalIgnoreCase);
    private static Field TreasuryField;
    // Это явный список доступных записываемых полей, не reflection по всему Nation.
    // Для нового поля задайте единицы, Min/Max и Help; затем проверьте подпись в
    // Labels/Fractions, preview и реальное применение. Одной новой подписи недостаточно.

    private static readonly (string Suffix, Operation Operation)[] Suffixes =
    {
        ("_percent", Operation.Percent),
        ("_mult", Operation.Mult),
        ("_set", Operation.Set)
    };

    static ScriptedEffects()
    {
        NationField("treasury", n => n.Treasury, (n, v) => n.Treasury = v, float.NegativeInfinity, float.PositiveInfinity,
            "Cash on hand. An overdrawn balance is imported into sovereign principal after the complete scripted effect.");
        TreasuryField = Fields["treasury"];
        NationField("public_debt", n => n.PublicDebt, (n, v) => n.PublicDebt = v, 0f, float.PositiveInfinity,
            "Outstanding sovereign principal, separate from cash. Changing this does not issue cash.");
        NationField("credit_confidence", n => n.CreditConfidence, (n, v) => n.CreditConfidence = v, 0f, 1f,
            "Lenders' confidence, 0-1; affects credit access and the risk spread.");
        NationField("debt_arrears", n => n.DebtArrears, (n, v) => n.DebtArrears = v, 0f, float.PositiveInfinity,
            "Unpaid contractual interest, distinct from outstanding principal.");
        NationField("payment_arrears", n => n.PaymentArrears, (n, v) => n.PaymentArrears = v, 0f, float.PositiveInfinity,
            "Unpaid primary public spending; does not alone mean a contractual debt default.");
        NationField("army_experience", n => n.ArmyExperience, (n, v) => n.ArmyExperience = v, 0f, 100000f,
            "Army experience: what the army Doctrines are paid for in. Earned by fighting, drilling and funding.");
        NationField("air_experience", n => n.AirExperience, (n, v) => n.AirExperience = v, 0f, 100000f,
            "Air experience: what the Doctrine of the sky is paid for in. Earned by flying real missions.");
        NationField("naval_experience", n => n.NavalExperience, (n, v) => n.NavalExperience = v, 0f, 100000f,
            "Naval experience: what the Doctrine of the coast is paid for in. Earned at sea and in action.");
        NationField("political_points", n => n.PoliticalPoints, (n, v) => n.PoliticalPoints = v, 0f, 100000f,
            "Political credit: what a government can push through besides money. Spent on rewriting a war Doctrine.");
        NationField("movement.cadres", n => n.Movement?.Cadres ?? 0f,
            (n, v) => { if (n.Movement is not null) n.Movement.Cadres = v; }, 0f, float.PositiveInfinity,
            "A movement's organisers and volunteers, in people.");
        NationField("movement.legitimacy", n => n.Movement?.Legitimacy ?? 0f,
            (n, v) => { if (n.Movement is not null) n.Movement.Legitimacy = v; }, 0f, 1f,
            "A movement's standing, 0-1.");
        NationField("movement.heat", n => n.Movement?.Heat ?? 0f,
            (n, v) => { if (n.Movement is not null) n.Movement.Heat = v; }, 0f, 1f,
            "The temper of the country hosting a movement's headquarters, 0-1; at the expel line it is thrown out.");
        NationField("movement.support", n => n.Movement is null || n.Movement.Support.Count == 0 ? 0f : n.Movement.Support.Values.Max(),
            (n, v) =>
            {
                if (n.Movement is null || n.Movement.Support.Count == 0) return;
                var strongest = n.Movement.Support.Values.Max();
                var scale = strongest <= 0f ? 1f : v / strongest;
                foreach (var region in n.Movement.Support.Keys.ToList())
                    n.Movement.Support[region] = Math.Clamp(n.Movement.Support[region] * scale, 0f, 0.92f);
            }, 0f, 0.92f,
            "A movement's support in its strongest region, 0-1; changing it moves every region by the same share.");
        NationField("gdp", n => n.Gdp, (n, v) => n.Gdp = v, 0.01f, float.PositiveInfinity,
            "Annual gross domestic product.");
        NationField("productivity", n => n.Productivity, (n, v) => n.Productivity = v, 0.05f, 8f,
            "Output multiplier; the long-run driver of GDP growth.");
        NationField("stability", n => n.Stability, (n, v) => n.Stability = v, 0.10f, 0.98f,
            "Domestic political stability, 0-1.");
        NationField("war_support", n => n.WarSupport, (n, v) => n.WarSupport = v, 0f, 1f,
            "Public willingness to sustain a war, 0-1.");
        NationField("standard_of_living", n => n.StandardOfLiving, (n, v) => n.StandardOfLiving = v, 0f, 1f,
            "National average standard of living, 0-1.");
        NationField("social.expected_living", n => n.ExpectedLivingStandard,
            (n, v) => n.ExpectedLivingStandard = v, 0f, 1f, "The population's established living-standard expectation, 0-1.");
        NationField("social.domestic_incident", n => n.DomesticImage?.Incident ?? 0f,
            (n, v) => { n.DomesticImage ??= new PublicImage(); n.DomesticImage.Incident = v; }, -1f, 1f,
            "Pending domestic reputation impulse, -1 to 1; it is absorbed gradually, not an instant status label.");
        NationField("literacy", n => n.Literacy, (n, v) => n.Literacy = v, 0.05f, 0.999f,
            "Share of the population that is literate, 0-1.");
        NationField("brain_drain", n => n.BrainDrainRate, (n, v) => n.BrainDrainRate = v, -1f, 1f,
            "Signed human-capital migration flow, -1 (severe brain drain) to +1 (strong talent inflow).");
        NationField("inflation", n => n.Inflation, (n, v) => n.Inflation = v, -1f, 100f,
            "Annual inflation. 0.04 is four per cent. Re-clamped to the floor and ceiling in common/inflation.");
        NationField("key_rate", CentralBank.KeyRate, CentralBank.SetKeyRate, 0f, 100f,
            "The central bank's key rate, per annum. Clamped to what the debt_interest budget line allows.");
        NationField("government_sets_key_rate",
            n => n.GovernmentSetsKeyRate ? 1f : 0f,
            (n, v) => n.GovernmentSetsKeyRate = v >= 0.5f, 0f, 1f,
            "1 when the government has taken the key rate off its own central bank, 0 when the bank is independent.");
        NationField("construction_capacity", n => n.ConstructionCapacity, (n, v) => n.ConstructionCapacity = v,
            0f, float.PositiveInfinity, "Parallel construction throughput.");
        NationField("manpower", n => n.ManpowerReserve, (n, v) => n.ManpowerReserve = v, 0f, float.PositiveInfinity,
            "Trained manpower available for reinforcement.");
        NationField("market_access", n => n.MarketAccess, (n, v) => n.MarketAccess = v, 0f, 1f,
            "Access to the world market, 0-1.");
        NationField("imperial_overstretch", n => n.ImperialOverstretch, (n, v) => n.ImperialOverstretch = v, 0f, 1f,
            "Imperial overstretch of a state governing distant or divergent territories, 0-1.");
        NationField("notoriety", n => n.Notoriety, (n, v) => n.Notoriety = v, 0f, 100f,
            "International notoriety from conquests and broken treaties, 0-100.");
        NationField("union_cohesion", n => n.UnionCohesion, (n, v) => n.UnionCohesion = v, 0f, 1f,
            "Cohesion of a proclaimed union or restored state, 0-1.");
        NationField("population", n => n.Population, (n, v) => n.Population = (long)MathF.Max(0f, v),
            0f, float.PositiveInfinity, "National population headcount.");
        NationField("nuclear_posture", n => n.NuclearPosture, (n, v) => n.NuclearPosture = (int)MathF.Round(M.Clamp(v, 1f, 5f)),
            1f, 5f, "Nuclear alert posture (5 routine down to 1 launch-on-warning); applies only to deterrent holders.");

        StateField("state.standard_of_living", s => s.StandardOfLiving, (s, v) => s.StandardOfLiving = v, 0f, 1f,
            "Standard of living in every state the nation owns.");
        StateField("state.radicalism", s => s.Radicalism, (s, v) => s.Radicalism = v, 0f, 1f,
            "Radicalism in every state the nation owns.");
        StateField("state.literacy", s => s.Literacy, (s, v) => s.Literacy = v, 0f, 1f,
            "Literacy in every state the nation owns.");
        StateField("state.urbanization", s => s.Urbanization, (s, v) => s.Urbanization = v, 0f, 1f,
            "Urbanisation in every state the nation owns.");
        StateField("state.population", s => s.Population, (s, v) => s.Population = (long)MathF.Max(0f, v),
            0f, float.PositiveInfinity, "Population of every state the nation owns.");
        StateField("state.stability", s => s.Stability, (s, v) => s.Stability = v, 0f, 1f,
            "Stability in every state the nation owns.");
        StateField("state.war_damage", s => s.WarDamage, (s, v) => s.WarDamage = v, 0f, 1f,
            "War damage in every state the nation owns: 0 untouched, 1 ruins.");
        StateField("state.brain_drain", s => s.BrainDrainPressure, (s, v) => s.BrainDrainPressure = v, 0f, 1f,
            "Brain drain emigration pressure in every state the nation owns, 0-1.");

        ArmyField("army.organisation", a => a.Organisation, (a, v) => a.Organisation = v, 0f, 1f,
            "Morale of every division the nation has, 0-1. Legacy key: army.organisation.");
        ArmyField("army.entrenchment", a => a.Entrenchment, (a, v) => a.Entrenchment = v, 0f, 1f,
            "Entrenchment of every division the nation has, 0-1.");
        ArmyField("army.experience", a => a.Experience, (a, v) => a.Experience = v, 0f, 1f,
            "Experience of every division the nation has, 0-1.");
        ArmyField("army.equipment", a => a.Equipment, (a, v) => a.Equipment = v, 0f, 1f,
            "Equipment of every division the nation has, 0-1.");
        FleetField("fleet.readiness", f => f.Readiness, (f, v) => f.Readiness = v, 0f, 1f,
            "Readiness of every fleet the nation has, 0-1.");
        FleetField("fleet.supply", f => f.Supply, (f, v) => f.Supply = v, 0f, 1f,
            "Stores of every fleet the nation has, 0-1.");
        NationField("research",
            n => n.ResearchProgress.GetValueOrDefault(n.CurrentResearch ?? ""),
            (n, v) => { if (!string.IsNullOrEmpty(n.CurrentResearch)) n.ResearchProgress[n.CurrentResearch] = v; },
            0f, float.PositiveInfinity, "Research points on the technology being researched now.");
    }

    private static void ArmyField(string name, Func<Army, float> get, Action<Army, float> set,
        float min, float max, string help) =>
        Fields[name] = new Field { Name = name, GetArmy = get, SetArmy = set, Min = min, Max = max, Help = help };

    private static void FleetField(string name, Func<Fleet, float> get, Action<Fleet, float> set,
        float min, float max, string help) =>
        Fields[name] = new Field { Name = name, GetFleet = get, SetFleet = set, Min = min, Max = max, Help = help };

    private static void NationField(string name, Func<Nation, float> get, Action<Nation, float> set,
        float min, float max, string help) =>
        Fields[name] = new Field { Name = name, GetNation = get, SetNation = set, Min = min, Max = max, Help = help };

    private static void StateField(string name, Func<ScenarioState, float> get, Action<ScenarioState, float> set,
        float min, float max, string help) =>
        Fields[name] = new Field { Name = name, GetState = get, SetState = set, Min = min, Max = max, Help = help };

    /// <summary>Every key content may write to, for documentation and console help.</summary>
    public static IEnumerable<(string Key, string Help)> Vocabulary =>
        Fields.Values.OrderBy(entry => entry.Name, StringComparer.Ordinal).Select(entry => (entry.Name, entry.Help))
            .Concat(new[]
            {
                ("grand_project_progress", "Advance (or delay) construction progress days on the nation's active Grand Projects."),
                ("stockpile.GOOD", "Adjust strategic depot reserve for a tracked commodity (e.g. stockpile.ammunition, stockpile.oil, stockpile.arms); suffixes work as usual."),
                ("unlock_feature", "String or array of feature ids/aliases to enable globally (e.g. \"aviation\", \"nukes\")."),
                ("restrict_feature", "String or array of feature ids/aliases to disable/restrict globally."),
                ("reputation.culture.ID", "Audience-scoped cultural narrative impulse, -1 to 1. Also reputation.nation.TAG and reputation.coalition.TARGET:DAY; suffixes work as usual."),
                ("income_penalty", "{ \"share\": 0.08, \"days\": 120 }: cut gross budget revenue (not expenses) by a share for a number of days; penalties add up to IncomePenaltyCap.")
            });

    internal static bool FeatureEffect(string key, out string id)
    {
        id = "";
        if (key is null || !key.StartsWith("feature.", StringComparison.OrdinalIgnoreCase) ||
            !key.EndsWith("_set", StringComparison.OrdinalIgnoreCase)) return false;
        id = key[8..^4].ToLowerInvariant();
        return MechanicFeatures.ValidId(id);
    }

    internal static bool IsGrandProjectProgressKey(string key) =>
        key is not null && key.Trim().Replace('-', '_').Equals("grand_project_progress", StringComparison.OrdinalIgnoreCase);

    internal static bool TryResolveStockpileGood(string raw, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var clean = raw.Trim().Replace('-', '_');
        var compact = clean.Replace("_", "", StringComparison.Ordinal);
        foreach (var good in StrategicStockpiles.TrackedGoods)
        {
            if (good.Equals(clean, StringComparison.OrdinalIgnoreCase) ||
                good.Equals(compact, StringComparison.OrdinalIgnoreCase))
            {
                canonical = good;
                return true;
            }
        }
        if (CommodityCatalog.TryParse(clean, out var commodity))
        {
            canonical = CommodityCatalog.IdOf(commodity);
            return !string.IsNullOrEmpty(canonical);
        }
        return false;
    }

    public static bool IsKnown(string key) =>
        FeatureEffect(key, out _) || IsGrandProjectProgressKey(key) || Resolve(key, out _, out _);

    /// <summary>Find the closest valid key for an unknown effect or trigger key so modders get an immediate typo suggestion.</summary>
    public static string SuggestClosestKey(string unknownKey, IEnumerable<string> candidates)
    {
        if (string.IsNullOrWhiteSpace(unknownKey) || candidates is null) return "";
        var probe = unknownKey.Trim().Replace('-', '_').ToLowerInvariant();
        string best = "";
        var bestDist = int.MaxValue;
        foreach (var cand in candidates)
        {
            if (string.IsNullOrWhiteSpace(cand)) continue;
            var norm = cand.Trim().ToLowerInvariant();
            var dist = LevenshteinDistance(probe, norm);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = cand;
            }
        }
        var threshold = Math.Max(2, Math.Min(4, probe.Length / 2));
        return bestDist <= threshold ? best : "";
    }

    private static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }

    private static bool Resolve(string rawKey, out Field field, out Operation operation)
    {
        field = null;
        operation = Operation.Add;
        if (string.IsNullOrWhiteSpace(rawKey)) return false;

        var key = rawKey.Trim();
        foreach (var (suffix, candidate) in Suffixes)
        {
            if (!key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            var stem = key[..^suffix.Length];
            if (!FindField(stem, out field)) continue;
            operation = candidate;
            return true;
        }
        return FindField(key, out field);
    }

    private static bool FindField(string key, out Field field)
    {
        if (Fields.TryGetValue(key.Replace('-', '_'), out field)) return true;

        if (key.StartsWith("stockpile.", StringComparison.OrdinalIgnoreCase) && key.Length > 10)
        {
            if (!TryResolveStockpileGood(key[10..], out var canonical)) return false;
            var good = canonical;
            field = new Field
            {
                Name = "stockpile." + good,
                Min = 0f,
                Max = 100000f,
                GetNation = n => n.Stockpiles.GetValueOrDefault(good, 0f),
                SetNation = (n, v) => n.Stockpiles[good] = MathF.Max(0f, v)
            };
            return true;
        }

        if (!key.StartsWith("reputation.", StringComparison.OrdinalIgnoreCase) || key.Length > 128) return false;
        var parts = key.Split('.', 3);
        if (parts.Length != 3 || parts[2].Length == 0 ||
            !parts[2].All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or ':')) return false;
        var kind = parts[1].ToLowerInvariant();
        if (kind is not ("culture" or "nation" or "coalition")) return false;
        var subject = kind + ":" + (kind == "nation" ? parts[2].ToUpperInvariant() : parts[2]);
        field = new Field
        {
            Name = "reputation." + subject,
            Min = -1f,
            Max = 1f,
            GetNation = n => SocialMemory.Image(n, subject)?.Incident ?? 0f
        };
        return true;
    }

    /// <summary>One parsed entry, kept flat so applying an effect allocates nothing.</summary>
    public readonly struct Entry
    {
        internal Entry(string key, float value)
        {
            Key = key;
            Value = value;
        }

        public string Key { get; }
        public float Value { get; }
    }

    [JsonConverter(typeof(ScriptedEffectSaveConverter))]
    public sealed class Effect
    {
        private readonly List<Entry> _entries = new();
        private readonly List<(string Id, int Days)> _grants = new();
        private readonly List<string> _revokes = new();
        private readonly List<OfficeholderEffect> _people = new();
        private List<(float Share, int Days, string Source)> _incomePenalties;

        public IReadOnlyList<Entry> Entries => _entries;
        public IReadOnlyList<(float Share, int Days, string Source)> IncomePenalties =>
            (IReadOnlyList<(float Share, int Days, string Source)>)_incomePenalties ?? Array.Empty<(float, int, string)>();
        public IReadOnlyList<(string Id, int Days)> Grants => _grants;
        public IReadOnlyList<string> Revokes => _revokes;
        public IReadOnlyList<OfficeholderEffect> People => _people;

        public bool IsEmpty => _entries.Count == 0 && _grants.Count == 0 && _revokes.Count == 0 && _people.Count == 0 &&
            (_incomePenalties is null || _incomePenalties.Count == 0);

        internal void Add(string key, float value) => _entries.Add(new Entry(key, value));
        internal void Grant(string id, int days) => _grants.Add((id, days));
        internal void Revoke(string id) => _revokes.Add(id);
        internal void Person(OfficeholderEffect person) => _people.Add(person);
        internal void IncomePenalty(float share, int days, string source) =>
            (_incomePenalties ??= new List<(float, int, string)>(1)).Add((share, days, source ?? ""));

        public void Apply(Campaign campaign, Nation nation)
        {
            if (nation is null) return;
            foreach (var id in _revokes) ModifierSystem.Revoke(nation, id);
            foreach (var (id, days) in _grants) ModifierSystem.Grant(campaign, nation, id, days);
            List<ScenarioState> owned = null;

            foreach (var entry in _entries)
            {
                if (!float.IsFinite(entry.Value)) continue;
                if (FeatureEffect(entry.Key, out var feature))
                {
                    campaign.SetFeature(feature, entry.Value >= .5f);
                    continue;
                }
                if (IsGrandProjectProgressKey(entry.Key))
                {
                    if (campaign is not null && GrandProjects.IsEnabled(campaign))
                    {
                        for (var i = 0; i < campaign.GrandProjects.Count; i++)
                        {
                            var proj = campaign.GrandProjects[i];
                            if (proj.HostNationId != nation.Id || proj.Status != GrandProjectStatus.Active) continue;
                            proj.ProgressDays = M.Clamp(proj.ProgressDays + entry.Value, 0f, MathF.Max(1f, proj.StageDurationDays));
                        }
                    }
                    continue;
                }
                if (!Resolve(entry.Key, out var field, out var operation)) continue;
                if (field.Name == "social.expected_living" && !SocialMemory.HabitsEnabled(campaign)) continue;
                if (field.Name == "social.domestic_incident" && !SocialMemory.ImagesEnabled(campaign)) continue;
                if (field.Name == "nuclear_posture")
                {
                    if (campaign is null || !campaign.FeatureEnabled("nuclear_posture") || Deterrence.TierOf(campaign, nation) <= 0)
                        continue;
                    var afterPosture = Combine(field.GetNation(nation), entry.Value, operation, field);
                    field.SetNation(nation, afterPosture);
                    nation.NuclearPostureDay = campaign.ElapsedDays;
                    continue;
                }
                if (field.Name.StartsWith("stockpile.", StringComparison.Ordinal))
                {
                    if (campaign is not null) StrategicStockpiles.EnsureSeeded(campaign, nation);
                    var beforeStock = field.GetNation(nation);
                    field.SetNation(nation, Combine(beforeStock, entry.Value, operation, field));
                    continue;
                }

                if (field.Name.StartsWith("reputation.", StringComparison.Ordinal))
                {
                    if (!SocialMemory.ImagesEnabled(campaign)) continue;
                    var subject = field.Name[11..];
                    if (subject.StartsWith("nation:", StringComparison.Ordinal) && campaign.NationByTag(subject[7..]) is null) continue;
                    if (subject.StartsWith("culture:", StringComparison.Ordinal) && DemographyCatalog.Culture(subject[8..]) is null) continue;
                    if (subject.StartsWith("coalition:", StringComparison.Ordinal) &&
                        !campaign.Coalitions.Any(group => SocialMemory.CoalitionKey(group) == subject)) continue;
                    var image = SocialMemory.Image(nation, subject, true);
                    var pending = Combine(image.Incident, entry.Value, operation, field);
                    image.Incident = pending;
                    image.LastReason = "Scripted public narrative";
                    image.LastIncidentDay = campaign.ElapsedDays;
                    continue;
                }

                if (field.SetNation is not null)
                {
                    var before = field.GetNation(nation);
                    var after = Combine(before, entry.Value, operation, field);
                    field.SetNation(nation, after);
                    if (ReferenceEquals(field, TreasuryField)) MoneyLedger.Book(nation, MoneyFlow.EventsAndDecisions, after - before);
                    continue;
                }
                if (field.SetArmy is not null)
                {
                    foreach (var army in campaign.Armies)
                        if (army.NationId == nation.Id) field.SetArmy(army, Combine(field.GetArmy(army), entry.Value, operation, field));
                    continue;
                }
                if (field.SetFleet is not null)
                {
                    foreach (var fleet in campaign.Fleets)
                        if (fleet.NationId == nation.Id) field.SetFleet(fleet, Combine(field.GetFleet(fleet), entry.Value, operation, field));
                    continue;
                }

                owned ??= campaign.StatesOf(nation).ToList();
                foreach (var state in owned)
                    field.SetState(state, Combine(field.GetState(state), entry.Value, operation, field));
            }
            foreach (var person in _people) person.Apply(campaign, nation);
            if (_incomePenalties is not null)
                foreach (var (share, days, source) in _incomePenalties)
                    Simulation.IncomePenalties.Add(campaign, nation, share, days, source);
            SovereignFinance.NormalizeAccounts(campaign, nation);
        }

        internal static float PreviewCombine(float current, float value, Operation operation, string fieldName, float min, float max)
        {
            var result = operation switch
            {
                Operation.Percent when fieldName == "treasury" => current + MathF.Abs(current) * value,
                Operation.Percent => current * (1f + value),
                Operation.Mult => current * value,
                Operation.Set => value,
                _ => current + value
            };
            return M.Clamp(result, min, max);
        }

        private static float Combine(float current, float value, Operation operation, Field field) =>
            PreviewCombine(current, value, operation, field.Name, field.Min, field.Max);

        /// <summary>Plain-language summary, so the UI can describe a modded option that shipped no text.</summary>
        public string Describe()
        {
            if (IsEmpty) return "No effect.";
            return string.Join(", ", _entries.Select(entry =>
            {
                var sign = entry.Value >= 0f ? "+" : "";
                return $"{entry.Key} {sign}{entry.Value.ToString("0.###", CultureInfo.InvariantCulture)}";
            }).Concat(ScriptedEffects.IncomePenaltyLines(this)).Concat(ScriptedEffects.SpiritLines(_grants))
                .Concat(_revokes.Select(id => Loc.Format("Loses «{0}»", Loc.Get(ModifierCatalog.Get(id)?.Name ?? id))))
                .Concat(_people.Select(person => person.Describe())));
        }
    }

    /// <summary>Pending event options must keep their actual effects in a save. A plain
    /// deserializer cannot repopulate the private lists behind the read-only views.</summary>
    public sealed class ScriptedEffectSaveConverter : JsonConverter<Effect>
    {
        public override Effect Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a scripted effect.");
            var effect = new Effect();
            if (root.TryGetProperty("Entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
                foreach (var entry in entries.EnumerateArray())
                    if (entry.TryGetProperty("Key", out var key) && entry.TryGetProperty("Value", out var value))
                        effect.Add(key.GetString() ?? "", value.GetSingle());
            if (root.TryGetProperty("Grants", out var grants) && grants.ValueKind == JsonValueKind.Array)
                foreach (var grant in grants.EnumerateArray())
                {
                    if (grant.ValueKind != JsonValueKind.Object) continue;
                    var id = grant.TryGetProperty("Id", out var named) ? named.GetString()
                        : grant.TryGetProperty("Item1", out var old) ? old.GetString() : "";
                    var days = grant.TryGetProperty("Days", out var duration) ? duration.GetInt32()
                        : grant.TryGetProperty("Item2", out var oldDays) ? oldDays.GetInt32() : 0;
                    if (!string.IsNullOrWhiteSpace(id)) effect.Grant(id, days);
                }
            if (root.TryGetProperty("Revokes", out var revokes) && revokes.ValueKind == JsonValueKind.Array)
                foreach (var revoke in revokes.EnumerateArray())
                    if (revoke.ValueKind == JsonValueKind.String) effect.Revoke(revoke.GetString() ?? "");
            if (root.TryGetProperty("IncomePenalties", out var penalties) && penalties.ValueKind == JsonValueKind.Array)
                foreach (var penalty in penalties.EnumerateArray())
                    if (penalty.ValueKind == JsonValueKind.Object &&
                        penalty.TryGetProperty("Share", out var share) && share.TryGetSingle(out var shareValue) &&
                        penalty.TryGetProperty("Days", out var length) && length.TryGetInt32(out var lengthValue))
                        effect.IncomePenalty(shareValue, lengthValue,
                            penalty.TryGetProperty("Source", out var origin) ? origin.GetString() : "");
            if (root.TryGetProperty("People", out var people) && people.ValueKind == JsonValueKind.Array)
                foreach (var person in people.EnumerateArray())
                    if (person.ValueKind == JsonValueKind.Object && JsonSerializer.Deserialize<OfficeholderEffect>(person.GetRawText(), options) is { } restored)
                        effect.Person(restored);
            return effect;
        }

        public override void Write(Utf8JsonWriter writer, Effect value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("Entries");
            writer.WriteStartArray();
            foreach (var entry in value.Entries)
            {
                writer.WriteStartObject();
                writer.WriteString("Key", entry.Key);
                writer.WriteNumber("Value", entry.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("Grants");
            writer.WriteStartArray();
            foreach (var (id, days) in value.Grants)
            {
                writer.WriteStartObject();
                writer.WriteString("Id", id);
                writer.WriteNumber("Days", days);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("Revokes");
            JsonSerializer.Serialize(writer, value.Revokes, options);
            if (value.IncomePenalties.Count > 0)
            {
                writer.WritePropertyName("IncomePenalties");
                writer.WriteStartArray();
                foreach (var (share, days, source) in value.IncomePenalties)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("Share", share);
                    writer.WriteNumber("Days", days);
                    writer.WriteString("Source", source ?? "");
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            if (value.People.Count > 0)
            {
                writer.WritePropertyName("People");
                JsonSerializer.Serialize(writer, value.People, options);
            }
            writer.WriteEndObject();
        }
    }
}
