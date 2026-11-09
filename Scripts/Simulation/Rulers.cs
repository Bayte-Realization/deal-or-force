using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public bool RulersEnabled { get; set; } = true;
    public int RulerReviewDays { get; set; } = 30;
    public int RulerCabinetCooldownDays { get; set; } = 90;
    public float RulerCabinetAppointmentCostShare { get; set; } = 0.002f;
    public float RulerCabinetAppointmentCostFloor { get; set; } = 0.05f;
    public int RulerMinMortalityAge { get; set; } = 68;
    public int RulerMaxAge { get; set; } = 100;
    public float RulerAnnualMortalityAtMinAge { get; set; } = 0.015f;
    public float RulerAnnualMortalityPerYearOver { get; set; } = 0.008f;
    public float RulerElectionChangeFloorStability { get; set; } = 0.55f;
}

public sealed partial class Nation
{
    public string HeadOfStateName = "";
    public string HeadOfStateTrait = "";
    public int HeadOfStateBirthYear;
    public int HeadOfStateSinceDay = -1;

    public string HeadOfGovernmentName = "";
    public string HeadOfGovernmentTrait = "";
    public int HeadOfGovernmentBirthYear;
    public int HeadOfGovernmentSinceDay = -1;

    public int NextElectionDay = -1;
    public int LastCabinetChangeDay = -9999;
    public int RulerGeneration;
    public Dictionary<string, string> Cabinet = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool HasCustomLeadership =>
        HeadOfStateName.Length > 0 || HeadOfGovernmentName.Length > 0 ||
        HeadOfStateTrait.Length > 0 || HeadOfGovernmentTrait.Length > 0 ||
        HeadOfStateBirthYear != 0 || HeadOfGovernmentBirthYear != 0 ||
        HeadOfStateSinceDay != -1 || HeadOfGovernmentSinceDay != -1 ||
        NextElectionDay != -1 || LastCabinetChangeDay != -9999 ||
        RulerGeneration != 0 || Cabinet.Count > 0;
}

public sealed class RulerOfficeRule
{
    public string Government = "";
    public bool Dual;
    public string HeadOfStateTitle = "President";
    public string HeadOfGovernmentTitle = "Prime Minister";
    public int TermDays = 1460;
    public bool HereditaryState;
    public bool LifeState;
}

public sealed class RulerTraitDefinition
{
    public string Id = "";
    public string Name = "";
    public string Description = "";
    public readonly Dictionary<string, float> Modifiers = new(StringComparer.OrdinalIgnoreCase);
    public float Opportunism;
    public float Cunning;
    public float Foresight;
    public float Volatility;
}

public sealed class CabinetOptionDefinition
{
    public string Id = "";
    public string Name = "";
    public string Description = "";
    public readonly Dictionary<string, float> Modifiers = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CabinetPortfolioDefinition
{
    public string Id = "";
    public string Name = "";
    public readonly List<CabinetOptionDefinition> Options = new();
}

public sealed class AuthoredMinisterEntry
{
    public string Name = "";
    public int Age = 55;
    public string Trait = "";
    public string Option = "";
    public string Portrait = "";
}

public sealed class AuthoredRulerEntry
{
    public string HeadOfState = "";
    public string HeadOfGovernment = "";
    public int StateAge = 58;
    public int GovAge = 52;
    public string StateTrait = "";
    public string GovTrait = "";
    public string StatePortrait = "";
    public string GovPortrait = "";
    public readonly Dictionary<string, AuthoredMinisterEntry> Ministers = new(StringComparer.OrdinalIgnoreCase);
}

public readonly record struct LeaderProfile(
    string Name,
    string Title,
    int Age,
    int BirthYear,
    string TraitId,
    string TraitName,
    int PortraitSeed,
    bool IsHeadOfState,
    string PortraitId = "");

public static class Rulers
{
    private static readonly Dictionary<string, RulerOfficeRule> Offices = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, RulerTraitDefinition> Traits = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<RulerTraitDefinition> TraitList = new();
    private static readonly List<CabinetPortfolioDefinition> Portfolios = new();
    private static readonly Dictionary<string, (string[] Given, string[] Family)> _namePools = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, AuthoredRulerEntry> Authored = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    public static IReadOnlyList<RulerTraitDefinition> AllTraits { get { EnsureLoaded(); return TraitList; } }
    public static IReadOnlyList<CabinetPortfolioDefinition> AllPortfolios { get { EnsureLoaded(); return Portfolios; } }

    /// <summary>Every cultural name pool the world can draw from, by the key <see cref="PoolFor"/> returns.</summary>
    public static IReadOnlyDictionary<string, (string[] Given, string[] Family)> NamePools
    {
        get { EnsureLoaded(); return _namePools; }
    }

    public static void ResetCatalog()
    {
        PersonNameCatalog.ResetCatalog();
        ElectoralPolitics.ResetCatalog();
        _loaded = false;
        Offices.Clear();
        Traits.Clear();
        TraitList.Clear();
        Portfolios.Clear();
        _namePools.Clear();
        Authored.Clear();
    }

    public static void Found(Campaign campaign)
    {
        EnsureLoaded();
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        foreach (var doc in GameDataLoader.DataDocuments("common", "rulers"))
            using (doc) MergeCommon(doc.RootElement);
        foreach (var file in GameDataLoader.DataFiles("history", "countries", "2024"))
        {
            if (file is null || !Path.GetFileName(file).Contains("rulers", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var doc = JsonDocument.Parse(StructuredData.ReadJson(file), GameDataLoader.ForgivingJson);
                MergeHistory(doc.RootElement);
            }
            catch (Exception ex)
            {
                DiagnosticLogs.Write("simulation", $"RULERS_HISTORY_FAILED file={Path.GetFileName(file)} error={ex.Message}");
            }
        }
    }

    public static void MergeCommon(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (root.TryGetProperty("offices", out var offices) && offices.ValueKind == JsonValueKind.Object)
            foreach (var entry in offices.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                var v = entry.Value;
                Offices[entry.Name] = new RulerOfficeRule
                {
                    Government = entry.Name,
                    Dual = Flag(v, "dual"),
                    HeadOfStateTitle = Text(v, "head_of_state", "President"),
                    HeadOfGovernmentTitle = Text(v, "head_of_government", "Prime Minister"),
                    TermDays = Int(v, "term_days", 1460),
                    HereditaryState = Flag(v, "hereditary_state"),
                    LifeState = Flag(v, "life_state")
                };
            }

        if (root.TryGetProperty("traits", out var traits) && traits.ValueKind == JsonValueKind.Object)
            foreach (var entry in traits.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                var v = entry.Value;
                if (!Traits.TryGetValue(entry.Name, out var trait))
                {
                    trait = new RulerTraitDefinition { Id = entry.Name, Name = entry.Name };
                    Traits[entry.Name] = trait;
                    TraitList.Add(trait);
                }
                trait.Name = Text(v, "name", trait.Name);
                trait.Description = Text(v, "description", trait.Description);
                trait.Opportunism = Float(v, "opportunism", trait.Opportunism);
                trait.Cunning = Float(v, "cunning", trait.Cunning);
                trait.Foresight = Float(v, "foresight", trait.Foresight);
                trait.Volatility = Float(v, "volatility", trait.Volatility);
                if (v.TryGetProperty("modifiers", out var mods))
                {
                    trait.Modifiers.Clear();
                    foreach (var (mk, mv) in NationalModifiers.Read(mods, $"rulers/traits/{entry.Name}", errors))
                        trait.Modifiers[mk] = mv;
                }
            }

        if (root.TryGetProperty("portfolios", out var portfolios) && portfolios.ValueKind == JsonValueKind.Object)
            foreach (var entry in portfolios.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                var v = entry.Value;
                var portfolio = Portfolios.Find(p => string.Equals(p.Id, entry.Name, StringComparison.OrdinalIgnoreCase));
                if (portfolio is null)
                {
                    portfolio = new CabinetPortfolioDefinition { Id = entry.Name, Name = entry.Name };
                    Portfolios.Add(portfolio);
                }
                portfolio.Name = Text(v, "name", portfolio.Name);
                if (v.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Object)
                {
                    portfolio.Options.Clear();
                    foreach (var optEntry in opts.EnumerateObject())
                    {
                        if (optEntry.Value.ValueKind != JsonValueKind.Object) continue;
                        var ov = optEntry.Value;
                        var opt = new CabinetOptionDefinition
                        {
                            Id = optEntry.Name,
                            Name = Text(ov, "name", optEntry.Name),
                            Description = Text(ov, "description")
                        };
                        if (ov.TryGetProperty("modifiers", out var mods))
                            foreach (var (mk, mv) in NationalModifiers.Read(mods, $"rulers/portfolios/{entry.Name}/{optEntry.Name}", errors))
                                opt.Modifiers[mk] = mv;
                        portfolio.Options.Add(opt);
                    }
                }
            }

        if (root.TryGetProperty("names", out var names) && names.ValueKind == JsonValueKind.Object)
            foreach (var entry in names.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                var v = entry.Value;
                var given = ArrayStrings(v, "given");
                var family = ArrayStrings(v, "family");
                if (given.Length > 0 && family.Length > 0)
                    _namePools[entry.Name] = (given, family);
            }
    }

    public static void MergeHistory(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            var v = entry.Value;
            Authored.TryGetValue(entry.Name, out var previous);
            var authored = new AuthoredRulerEntry
            {
                HeadOfState = Text(v, "head_of_state", previous?.HeadOfState ?? ""),
                HeadOfGovernment = Text(v, "head_of_government", previous?.HeadOfGovernment ?? ""),
                StateAge = Int(v, "state_age", previous?.StateAge ?? 58),
                GovAge = Int(v, "gov_age", previous?.GovAge ?? 52),
                StateTrait = Text(v, "state_trait", previous?.StateTrait ?? ""),
                GovTrait = Text(v, "gov_trait", previous?.GovTrait ?? ""),
                StatePortrait = Text(v, "state_portrait", previous?.StatePortrait ?? ""),
                GovPortrait = Text(v, "gov_portrait", previous?.GovPortrait ?? "")
            };
            // A one-photo mod must inherit the names/ages/other ministers it did
            // not mention, just like the rest of the game's key-based overlays.
            if (previous is not null)
                foreach (var minister in previous.Ministers) authored.Ministers[minister.Key] = minister.Value;
            if (v.TryGetProperty("ministers", out var minObj) && minObj.ValueKind == JsonValueKind.Object)
            {
                foreach (var minProp in minObj.EnumerateObject())
                {
                    if (minProp.Value.ValueKind != JsonValueKind.Object) continue;
                    var mv = minProp.Value;
                    authored.Ministers.TryGetValue(minProp.Name, out var former);
                    authored.Ministers[minProp.Name] = new AuthoredMinisterEntry
                    {
                        Name = Text(mv, "name", former?.Name ?? ""),
                        Age = Int(mv, "age", former?.Age ?? 55),
                        Trait = Text(mv, "trait", former?.Trait ?? ""),
                        Option = Text(mv, "option", former?.Option ?? ""),
                        Portrait = Text(mv, "portrait", former?.Portrait ?? "")
                    };
                }
            }
            Authored[entry.Name] = authored;
        }
    }

    public static AuthoredMinisterEntry AuthoredMinister(string tag, string portfolioId)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(portfolioId)) return null;
        if (Authored.TryGetValue(tag, out var a) && a.Ministers.TryGetValue(portfolioId, out var m))
            return m;
        return null;
    }

    public static RulerOfficeRule OfficeOf(Nation nation)
    {
        EnsureLoaded();
        if (nation is not null && Offices.TryGetValue(nation.Government ?? "", out var rule)) return rule;
        return Offices.TryGetValue("parliamentary_republic", out var fallback) ? fallback : new RulerOfficeRule();
    }

    public static RulerTraitDefinition TraitOf(string id)
    {
        EnsureLoaded();
        return !string.IsNullOrEmpty(id) && Traits.TryGetValue(id, out var t) ? t : null;
    }

    public static bool HasDualExecutive(Nation nation)
    {
        if (nation is null) return false;
        var office = OfficeOf(nation);
        if (office.Dual) return true;
        EnsureLoaded();
        return nation.HeadOfGovernmentName.Length > 0 ||
               (Authored.TryGetValue(nation.Tag ?? "", out var a) && a.HeadOfGovernment.Length > 0);
    }

    public static LeaderProfile HeadOfState(Campaign campaign, Nation nation)
    {
        EnsureLoaded();
        if (nation is null) return new LeaderProfile("—", "President", 50, 1974, "reformer", "Institutional Reformer", 0, true);
        var year = campaign?.Date.Year ?? 2024;
        var office = OfficeOf(nation);
        var title = CustomTitle(nation, office.HeadOfStateTitle, isState: true);
        string name;
        string traitId;
        int birthYear;
        if (nation.HeadOfStateName.Length > 0)
        {
            name = nation.HeadOfStateName;
            traitId = nation.HeadOfStateTrait;
            birthYear = nation.HeadOfStateBirthYear != 0
                ? nation.HeadOfStateBirthYear
                : Authored.TryGetValue(nation.Tag ?? "", out var authState) &&
                  string.Equals(authState.HeadOfState, name, StringComparison.OrdinalIgnoreCase)
                    ? Rulers.CalendarOriginYear(campaign) - Math.Clamp(authState.StateAge, 18, 99)
                    : Rulers.CalendarOriginYear(campaign) - AgeAtOffice(nation, AgeBand.HeadOfState, StableHash(name, 17));
        }
        else if (Authored.TryGetValue(nation.Tag ?? "", out var authored) && authored.HeadOfState.Length > 0)
        {
            name = authored.HeadOfState;
            traitId = authored.StateTrait;
            birthYear = Rulers.CalendarOriginYear(campaign) - Math.Clamp(authored.StateAge, 18, 99);
        }
        else
        {
            name = GenerateName(nation, 0, isState: true, campaignSeed: campaign?.Seed ?? 0, campaign: campaign);
            traitId = DefaultTraitFor(nation, 0, isState: true);
            birthYear = Rulers.CalendarOriginYear(campaign) - AgeAtOffice(nation, AgeBand.HeadOfState, StableHash(nation.Tag, 17));
        }
        var trait = TraitOf(traitId) ?? TraitOf(DefaultTraitFor(nation, 0, isState: true));
        var age = Math.Max(0, year - birthYear);
        var seed = StableHash(name, birthYear);
        var portrait = Authored.TryGetValue(nation.Tag ?? "", out var authoredPhoto) && authoredPhoto.HeadOfState == name ? authoredPhoto.StatePortrait : "";
        return new LeaderProfile(name, title, age, birthYear, trait?.Id ?? "reformer", trait?.Name ?? "Institutional Reformer", seed, true,
            OfficeholderEffect.PortraitFor(nation, name, birthYear, portrait));
    }

    // Authored ages refer to the scenario's calendar origin, not invariably 2024.
    // Negative birth years are real dates; only the legacy zero sentinel is unknown.
    internal static int CalendarOriginYear(Campaign campaign) => campaign is null ? 2024 :
        new HistoricalDate(campaign.Date.Year, campaign.Date.Month, campaign.Date.Day).AddDays(-campaign.ElapsedDays).Year;

    public static LeaderProfile HeadOfGovernment(Campaign campaign, Nation nation)
    {
        EnsureLoaded();
        if (nation is null) return new LeaderProfile("—", "Prime Minister", 48, 1976, "technocrat", "Fiscal Technocrat", 0, false);
        if (!HasDualExecutive(nation))
            return HeadOfState(campaign, nation);
        var year = campaign?.Date.Year ?? 2024;
        var office = OfficeOf(nation);
        var title = CustomTitle(nation, office.HeadOfGovernmentTitle, isState: false);
        string name;
        string traitId;
        int birthYear;
        if (nation.HeadOfGovernmentName.Length > 0)
        {
            name = nation.HeadOfGovernmentName;
            traitId = nation.HeadOfGovernmentTrait;
            birthYear = nation.HeadOfGovernmentBirthYear != 0
                ? nation.HeadOfGovernmentBirthYear
                : Authored.TryGetValue(nation.Tag ?? "", out var authGov) &&
                  string.Equals(authGov.HeadOfGovernment, name, StringComparison.OrdinalIgnoreCase)
                    ? Rulers.CalendarOriginYear(campaign) - Math.Clamp(authGov.GovAge, 18, 99)
                    : Rulers.CalendarOriginYear(campaign) - AgeAtOffice(nation, AgeBand.HeadOfGovernment, StableHash(name, 31));
        }
        else if (Authored.TryGetValue(nation.Tag ?? "", out var authored) && authored.HeadOfGovernment.Length > 0)
        {
            name = authored.HeadOfGovernment;
            traitId = authored.GovTrait;
            birthYear = Rulers.CalendarOriginYear(campaign) - Math.Clamp(authored.GovAge, 18, 99);
        }
        else
        {
            name = GenerateName(nation, 1, isState: false, campaignSeed: campaign?.Seed ?? 0, campaign: campaign);
            traitId = DefaultTraitFor(nation, 1, isState: false);
            birthYear = Rulers.CalendarOriginYear(campaign) - AgeAtOffice(nation, AgeBand.HeadOfGovernment, StableHash(nation.Tag, 31));
        }
        var trait = TraitOf(traitId) ?? TraitOf(DefaultTraitFor(nation, 1, isState: false));
        var age = Math.Max(0, year - birthYear);
        var seed = StableHash(name, birthYear ^ 0x5A5A);
        var portrait = Authored.TryGetValue(nation.Tag ?? "", out var authoredPhoto) && authoredPhoto.HeadOfGovernment == name ? authoredPhoto.GovPortrait : "";
        return new LeaderProfile(name, title, age, birthYear, trait?.Id ?? "technocrat", trait?.Name ?? "Fiscal Technocrat", seed, false,
            OfficeholderEffect.PortraitFor(nation, name, birthYear, portrait));
    }

    public static LeaderProfile PrimaryLeader(Campaign campaign, Nation nation) => HeadOfState(campaign, nation);

    public static CabinetOptionDefinition ActiveMinister(Nation nation, string portfolioId)
    {
        EnsureLoaded();
        var portfolio = Portfolios.Find(p => string.Equals(p.Id, portfolioId, StringComparison.OrdinalIgnoreCase));
        if (portfolio is null || portfolio.Options.Count == 0) return null;
        if (nation is not null && nation.Cabinet.TryGetValue(portfolioId, out var chosenId))
        {
            var found = portfolio.Options.Find(o => string.Equals(o.Id, chosenId, StringComparison.OrdinalIgnoreCase));
            if (found is not null) return found;
        }
        return null;
    }

    public static string MinisterPersonName(Nation nation, string portfolioId)
    {
        EnsureLoaded();
        if (nation is null) return "";
        if (nation.CabinetPeople.TryGetValue(portfolioId, out var serving) && !string.IsNullOrEmpty(serving.Name))
            return serving.Name;
        if (AuthoredMinister(nation.Tag, portfolioId) is { Name.Length: > 0 } authMin)
            return authMin.Name;
        var salt = StableHash(portfolioId, (nation.Cabinet.TryGetValue(portfolioId, out var opt) ? StableHash(opt, 3) : 0));
        return GenerateName(nation, Math.Abs(salt % 97) + 2, isState: false);
    }

    public static int MinisterBirthYear(Campaign campaign, Nation nation, string portfolioId)
    {
        EnsureLoaded();
        var year = campaign?.Date.Year ?? 2024;
        if (nation is null) return year - 52;
        if (nation.CabinetPeople.TryGetValue(portfolioId, out var serving) && serving.BirthYear != 0)
            return serving.BirthYear;
        if (AuthoredMinister(nation.Tag, portfolioId) is { Age: > 0 } authMin)
            return Rulers.CalendarOriginYear(campaign) - Math.Clamp(authMin.Age, 18, 99);
        return Rulers.CalendarOriginYear(campaign) - GeneratedMinisterAge(nation, portfolioId);
    }

    public static int MinisterPersonAge(Campaign campaign, Nation nation, string portfolioId)
    {
        var year = campaign?.Date.Year ?? 2024;
        return Math.Max(18, year - MinisterBirthYear(campaign, nation, portfolioId));
    }

    public static void AddTo(Dictionary<string, float> totals, Nation nation)
    {
        if (nation is null) return;
        EnsureLoaded();
        var stateTraitId = nation.HeadOfStateTrait;
        if (string.IsNullOrEmpty(stateTraitId) && Authored.TryGetValue(nation.Tag ?? "", out var authored))
            stateTraitId = authored.StateTrait;
        if (string.IsNullOrEmpty(stateTraitId) && TraitList.Count > 0)
            stateTraitId = TraitList[Math.Abs(StableHash(nation.Tag ?? "DEF", 11)) % TraitList.Count].Id;
        if (TraitOf(stateTraitId) is { Modifiers.Count: > 0 } stateTrait)
            foreach (var (k, v) in stateTrait.Modifiers)
                totals[k] = totals.GetValueOrDefault(k) + v;

        if (nation.Cabinet.Count > 0)
            foreach (var portfolio in Portfolios)
                if (ActiveMinister(nation, portfolio.Id) is { Modifiers.Count: > 0 } opt)
                    foreach (var (k, v) in opt.Modifiers)
                        totals[k] = totals.GetValueOrDefault(k) + v;
    }

    public static IEnumerable<(string Source, IReadOnlyDictionary<string, float> Modifiers)> Sources(Nation nation)
    {
        if (nation is null) yield break;
        EnsureLoaded();
        // An initial ruler has no saved trait id: it is authored, or drawn from the pool on
        // the first morning. Kept independent of nation.Government so a regime change only
        // alters GovernmentCatalog modifiers unless the ruler or cabinet actually changes.
        var stateTraitId = nation.HeadOfStateTrait;
        if (string.IsNullOrEmpty(stateTraitId) && Authored.TryGetValue(nation.Tag ?? "", out var authored))
            stateTraitId = authored.StateTrait;
        if (string.IsNullOrEmpty(stateTraitId) && TraitList.Count > 0)
            stateTraitId = TraitList[Math.Abs(StableHash(nation.Tag ?? "DEF", 11)) % TraitList.Count].Id;
        if (TraitOf(stateTraitId) is { Modifiers.Count: > 0 } stateTrait)
            yield return (stateTrait.Name, stateTrait.Modifiers);

        if (nation.Cabinet.Count > 0)
            foreach (var portfolio in Portfolios)
                if (ActiveMinister(nation, portfolio.Id) is { Modifiers.Count: > 0 } opt)
                    yield return ($"{portfolio.Name}: {opt.Name}", opt.Modifiers);
    }

    public static (float Opportunism, float Cunning, float Foresight, float Volatility) TemperamentShift(Campaign campaign, Nation nation)
    {
        if (nation is null) return (0f, 0f, 0f, 0f);
        EnsureLoaded();
        var state = HeadOfState(campaign, nation);
        var st = TraitOf(state.TraitId);
        float opp = st?.Opportunism ?? 0f, cun = st?.Cunning ?? 0f, fore = st?.Foresight ?? 0f, vol = st?.Volatility ?? 0f;
        if (HasDualExecutive(nation))
        {
            var gov = HeadOfGovernment(campaign, nation);
            if (TraitOf(gov.TraitId) is { } gt)
            {
                opp = (opp + gt.Opportunism) * 0.6f;
                cun = (cun + gt.Cunning) * 0.6f;
                fore = (fore + gt.Foresight) * 0.6f;
                vol = (vol + gt.Volatility) * 0.6f;
            }
        }
        return (opp, cun, fore, vol);
    }

    public static float CabinetAppointmentCost(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return 0f;
        return MathF.Max(campaign.Defines.RulerCabinetAppointmentCostFloor,
            MathF.Max(0f, nation.Gdp) * campaign.Defines.RulerCabinetAppointmentCostShare);
    }

    public static bool CanAppointMinister(Campaign campaign, Nation nation, string portfolioId, string optionId, out string refusal,
        bool allowSameOption = false)
    {
        refusal = "";
        if (campaign is null || nation is null) { refusal = "No country selected."; return false; }
        EnsureLoaded();
        var portfolio = Portfolios.Find(p => string.Equals(p.Id, portfolioId, StringComparison.OrdinalIgnoreCase));
        if (portfolio is null) { refusal = "Unknown cabinet portfolio."; return false; }
        var opt = portfolio.Options.Find(o => string.Equals(o.Id, optionId, StringComparison.OrdinalIgnoreCase));
        if (opt is null) { refusal = "Unknown minister candidate."; return false; }
        if (!allowSameOption && nation.Cabinet.TryGetValue(portfolio.Id, out var current) &&
            string.Equals(current, opt.Id, StringComparison.OrdinalIgnoreCase))
        {
            refusal = "Already serving in the cabinet.";
            return false;
        }
        var wait = campaign.Defines.RulerCabinetCooldownDays - (campaign.ElapsedDays - nation.LastCabinetChangeDay);
        if (wait > 0)
        {
            refusal = Loc.Format("The cabinet was reshuffled recently ({0} day(s) left).", wait);
            return false;
        }
        var cost = CabinetAppointmentCost(campaign, nation);
        if (nation.Treasury < cost)
        {
            refusal = Loc.Format("Costs {0}; treasury holds {1}.", $"{cost:0.00}B", $"{nation.Treasury:0.00}B");
            return false;
        }
        return true;
    }

    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>The same review, yielded between countries so a day of successions does not
    /// settle in one frame.</summary>
    public static System.Collections.Generic.IEnumerable<int> TickStages(Campaign campaign)
    {
        if (campaign is null || campaign.ElapsedDays < 0 || !campaign.Defines.RulersEnabled || !campaign.FeatureEnabled("rulers")) yield break;
        ElectoralPolitics.Tick(campaign);
        var every = Math.Max(1, campaign.Defines.RulerReviewDays);
        // The first morning is a review of its own: a ruler past the end of any life is
        // corrected before the world starts, and nothing else is disturbed.
        var firstMorning = campaign.ElapsedDays == 0;
        if (!firstMorning && campaign.ElapsedDays % every != 17 % every) yield break;
        EnsureLoaded();

        var scanned = 0;
        foreach (var nation in campaign.Nations)
        {
            if (++scanned % 16 == 0) yield return 0;
            if (nation.Population <= 0 || nation.Movement is not null || campaign.Fallen.Contains(nation.Id)) continue;
            var office = OfficeOf(nation);
            PoliticalAppointments.ReviewMinisters(campaign, nation, every);
            var stateLeader = HeadOfState(campaign, nation);

            // No one governs at a hundred. The actuarial table is a chance; this is a line.
            if (stateLeader.Age >= campaign.Defines.RulerMaxAge)
            {
                ReplaceHeadOfState(campaign, nation, "ruler_died");
                continue;
            }

            if (!firstMorning && stateLeader.Age >= campaign.Defines.RulerMinMortalityAge)
            {
                var over = stateLeader.Age - campaign.Defines.RulerMinMortalityAge;
                var annual = campaign.Defines.RulerAnnualMortalityAtMinAge + over * campaign.Defines.RulerAnnualMortalityPerYearOver;
                var reviewChance = annual * (every / 365f);
                var roll = Escalation.Roll(campaign.Seed, nation.Id, 0x71A, campaign.ElapsedDays, 0x1);
                if (roll < reviewChance)
                {
                    ReplaceHeadOfState(campaign, nation, "ruler_died");
                    continue;
                }
            }

            if (HasDualExecutive(nation))
            {
                var govLeader = HeadOfGovernment(campaign, nation);
                if (govLeader.Age >= campaign.Defines.RulerMaxAge)
                {
                    ReplaceHeadOfGovernment(campaign, nation, "ruler_died");
                    continue;
                }
                if (!firstMorning && govLeader.Age >= campaign.Defines.RulerMinMortalityAge)
                {
                    var over = govLeader.Age - campaign.Defines.RulerMinMortalityAge;
                    var annual = campaign.Defines.RulerAnnualMortalityAtMinAge + over * campaign.Defines.RulerAnnualMortalityPerYearOver;
                    var reviewChance = annual * (every / 365f);
                    var roll = Escalation.Roll(campaign.Seed, nation.Id, 0x71C, campaign.ElapsedDays, 0x3);
                    if (roll < reviewChance)
                    {
                        ReplaceHeadOfGovernment(campaign, nation, "ruler_died");
                        continue;
                    }
                }
            }

            if (firstMorning) continue;

            // AI periodic cabinet appointment when treasury is healthy
            if ((!nation.IsHuman || Delegation.Holds(nation, Delegation.Personnel)) && nation.Cabinet.Count < Portfolios.Count &&
                campaign.ElapsedDays - nation.LastCabinetChangeDay >= campaign.Defines.RulerCabinetCooldownDays * 2)
            {
                var cost = CabinetAppointmentCost(campaign, nation);
                if (nation.Treasury > cost * 6f)
                {
                    foreach (var portfolio in Portfolios)
                    {
                        if (nation.Cabinet.ContainsKey(portfolio.Id) || portfolio.Options.Count == 0) continue;
                        var pickIdx = Math.Abs(StableHash(nation.Tag, campaign.ElapsedDays + portfolio.Id.Length * 17)) % portfolio.Options.Count;
                        var opt = portfolio.Options[pickIdx];
                        new AppointMinisterCommand { IssuerId = nation.Id, PortfolioId = portfolio.Id, OptionId = opt.Id }.Apply(campaign);
                        break;
                    }
                }
            }
        }
    }

    public readonly record struct PoliticianCandidate(
        int SlotIndex,
        string Name,
        int Age,
        int BirthYear,
        string TraitId,
        string TraitName,
        string PortfolioId,
        string PortfolioName,
        string OptionId,
        string OptionName,
        int PortraitSeed);

    public static List<PoliticianCandidate> CandidatePool(Campaign campaign, Nation nation, int count = 4)
    {
        EnsureLoaded();
        count = Math.Clamp(count, 0, 32);
        var list = new List<PoliticianCandidate>();
        if (nation is null || TraitList.Count == 0 || Portfolios.Count == 0) return list;
        var year = campaign?.Date.Year ?? 2024;
        var cohortYears = Math.Clamp(campaign?.Defines.PoliticianCandidateCohortYears ?? 5, 1, 50);
        var cohort = (int)Math.Floor((year - 2024.0) / cohortYears);
        var cohortYear = 2024 + cohort * cohortYears;
        var baseGen = unchecked(nation.RulerGeneration * 11 + 100 + cohort * 701);
        for (var i = 0; i < count; i++)
        {
            var gen = baseGen + i * 7;
            var name = GenerateName(nation, gen, isState: false, campaignSeed: campaign?.Seed ?? 0, campaign: campaign);
            var traitId = DefaultTraitFor(nation, gen, isState: false);
            var trait = TraitOf(traitId) ?? (TraitList.Count > 0 ? TraitList[0] : new RulerTraitDefinition { Id = traitId, Name = traitId });
            var baseAge = 35 + Math.Abs(StableHash(nation.Tag, gen * 19 + 3)) % 31;
            var birthYear = cohortYear - baseAge;
            var age = year - birthYear;
            var portfolio = Portfolios[i % Portfolios.Count];
            var opt = portfolio.Options.Count > 0
                ? portfolio.Options[Math.Abs(StableHash(nation.Tag, gen * 23 + i)) % portfolio.Options.Count]
                : new CabinetOptionDefinition { Id = "reformer", Name = "Advisor" };
            var seed = StableHash(name, birthYear ^ (i * 0x1357));
            list.Add(new PoliticianCandidate(i, name, age, birthYear, trait.Id, trait.Name,
                portfolio.Id, portfolio.Name, opt.Id, opt.Name, seed));
        }
        return list;
    }

    /// <summary>The office a generated age is drawn for.</summary>
    private enum AgeBand { HeadOfState, HeadOfGovernment, Minister, Hereditary }

    /// <summary>
    /// Life expectancy the age draw is shaped by. The simulation has no life-expectancy figure of
    /// its own; the standard of living is what moves mortality in the demographic model, so it is
    /// used here too. The ends of the range follow the real spread of life expectancy at birth
    /// (UN World Population Prospects 2023: roughly 55 to 86 years), and literacy lifts the top of
    /// it the way it does in the real world.
    /// </summary>
    private static double Longevity(Nation nation)
    {
        var living = Math.Clamp((double)(nation?.StandardOfLiving ?? 0.5f), 0.15, 0.98);
        var literacy = Math.Clamp((double)(nation?.Literacy ?? 0.75f), 0.0, 1.0);
        return Math.Clamp(52.0 + 38.0 * living + 4.0 * (literacy - 0.75), 50.0, 88.0);
    }

    /// <summary>
    /// A standard-normal draw from a 32-bit hash (two uniforms, Box-Muller), cut at 2.6 sigma so
    /// no hash can produce a twelve-year-old chancellor or a hundred-and-thirty-year-old president.
    /// Deterministic: the same country and generation give the same person on every machine.
    /// </summary>
    private static double NormalFromHash(int hash)
    {
        var u1 = ((hash & 0xFFFF) + 1) / 65537.0;
        var u2 = (((hash >> 16) & 0xFFFF) + 1) / 65537.0;
        var z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return Math.Clamp(z, -2.6, 2.6);
    }

    /// <summary>
    /// How old a generated officeholder is on taking office. A national leader is older than the
    /// country's median resident - in 2024 the median leader was about sixty against a median
    /// resident of thirty-eight (Pew Research Center, May 2024) - and a hereditary monarch can be
    /// far younger, because an heir takes the throne at whatever age the succession finds them.
    /// Cabinet ministers sit between the two. The spread is deliberately wide: a cabinet of people
    /// born in the same year is the bug this is written against.
    /// </summary>
    private static int AgeAtOffice(Nation nation, AgeBand band, int seed)
    {
        var life = Longevity(nation);
        var drift = life - 75.0;
        var (mean, spread, floor) = band switch
        {
            AgeBand.HeadOfState => (60.0 + 0.35 * drift, 9.5, 30),
            AgeBand.HeadOfGovernment => (57.0 + 0.30 * drift, 8.5, 30),
            AgeBand.Hereditary => (48.0 + 0.30 * drift, 15.0, 18),
            _ => (53.0 + 0.30 * drift, 10.5, 26)
        };
        return (int)Math.Round(Math.Clamp(mean + spread * NormalFromHash(seed), floor, 92));
    }

    /// <summary>Age for a minister the cabinet draws when no authored person fills the portfolio.</summary>
    public static int GeneratedMinisterAge(Nation nation, string portfolioId) =>
        AgeAtOffice(nation, AgeBand.Minister,
            StableHash(nation?.Tag ?? "DEF", StableHash(portfolioId ?? "", 3)));

    public static void ReplaceHeadOfState(Campaign campaign, Nation nation, string reason = "succession")
    {
        if (campaign is null || nation is null) return;
        EnsureLoaded();
        nation.RulerGeneration++;
        var year = campaign.Date.Year;
        nation.HeadOfStateName = GenerateName(nation, nation.RulerGeneration * 2 + 1, isState: true, campaignSeed: campaign.Seed, campaign: campaign);
        nation.HeadOfStateTrait = DefaultTraitFor(nation, nation.RulerGeneration * 2 + 1, isState: true);
        var band = OfficeOf(nation).HereditaryState ? AgeBand.Hereditary : AgeBand.HeadOfState;
        nation.HeadOfStateBirthYear = year - AgeAtOffice(nation, band, StableHash(nation.Tag, nation.RulerGeneration * 13 + 7));
        nation.HeadOfStateSinceDay = campaign.ElapsedDays;
        if (!OfficeOf(nation).Dual)
        {
            nation.HeadOfGovernmentName = "";
            nation.HeadOfGovernmentTrait = "";
        }
        NationalModifiers.Invalidate(nation);
        if (!string.IsNullOrEmpty(reason))
            campaign.Chronicle.Record(campaign, reason == "ruler_died" ? "ruler_died" : "ruler_changed",
                nation.Id, place: nation.HeadOfStateName);
        campaign.MarkStrategicChange();
    }

    public static void ReplaceHeadOfGovernment(Campaign campaign, Nation nation, string reason = "cabinet_change")
    {
        if (campaign is null || nation is null) return;
        EnsureLoaded();
        nation.RulerGeneration++;
        if (!HasDualExecutive(nation))
        {
            ReplaceHeadOfState(campaign, nation, reason);
            return;
        }
        var year = campaign.Date.Year;
        nation.HeadOfGovernmentName = GenerateName(nation, nation.RulerGeneration * 2 + 2, isState: false, campaignSeed: campaign.Seed, campaign: campaign);
        nation.HeadOfGovernmentTrait = DefaultTraitFor(nation, nation.RulerGeneration * 2 + 2, isState: false);
        nation.HeadOfGovernmentBirthYear = year - AgeAtOffice(nation, AgeBand.HeadOfGovernment, StableHash(nation.Tag, nation.RulerGeneration * 19 + 5));
        nation.HeadOfGovernmentSinceDay = campaign.ElapsedDays;
        NationalModifiers.Invalidate(nation);
        if (!string.IsNullOrEmpty(reason))
            campaign.Chronicle.Record(campaign, reason == "ruler_died" ? "government_head_died" : "ruler_changed",
                nation.Id, place: nation.HeadOfGovernmentName);
        campaign.MarkStrategicChange();
    }

    private static string CustomTitle(Nation nation, string fallback, bool isState)
    {
        if (nation is null) return fallback;
        if (isState && nation.Government is "absolute_monarchy" or "constitutional_monarchy")
        {
            if (nation.Tag is "JPN") return "Emperor";
            if (nation.Tag is "SAU" or "JOR" or "MAR" or "BHR" or "GBR" or "ESP" or "NLD" or "BEL" or "SWE" or "NOR" or "DNK" or "THA" or "MYS" or "KHM" or "BTN" or "LSO" or "SWZ" or "TON")
                return "King";
            if (nation.Tag is "QAT" or "KWT") return "Emir";
            if (nation.Tag is "OMN" or "BRN") return "Sultan";
            if (nation.Tag is "LUX") return "Grand Duke";
        }
        if (isState && nation.Tag == "VAT") return "Pontiff";
        return fallback;
    }

    private static string DefaultTraitFor(Nation nation, int generation, bool isState)
    {
        EnsureLoaded();
        if (TraitList.Count == 0) return "reformer";
        if (isState && nation?.Government == "military_junta") return "veteran_commander";
        if (generation == 0 && nation is not null)
        {
            if (nation.Government == "technocracy") return "technocrat";
            if (nation.Government == "theocracy") return "ideologue";
            if (nation.Ideology is "communism" or "socialism") return isState ? "ideologue" : "industrialist";
            if (nation.Ideology == "social_democracy") return "welfare_architect";
            if (nation.Ideology == "nationalism") return isState ? "hawk" : "iron_hand";
        }
        var idx = Math.Abs(StableHash(nation?.Tag ?? "DEF", generation * 37 + (isState ? 11 : 29))) % TraitList.Count;
        return TraitList[idx].Id;
    }

    public static string GenerateName(Nation nation, int generation, bool isState, int campaignSeed = 0, Campaign campaign = null)
    {
        EnsureLoaded();
        var localPool = PersonNameCatalog.PoolFor(nation);
        // A country with no culture of its own that no table knows - a rising, a junta - is
        // named by the people of its capital, not by the English of the "default" pool.
        var ground = localPool.Length == 0 ? GroundCulture(campaign, nation) : "";
        if (ground.Length > 0) localPool = PersonNameCatalog.PoolFor(nation.Tag, ground);
        var monarch = isState && nation?.Government is "absolute_monarchy" or "constitutional_monarchy";
        if (PersonNameCatalog.TryGenerate(nation?.Tag, localPool, generation, campaignSeed,
            isState ? 17 : 31, out var person, monarch ? false : null))
        {
            if (monarch && DynastyCatalog.DynastyOf(nation.Tag) is { Length: > 0 } localHouse)
                return $"{person.Given} ({Cap(localHouse)})";
            return person.FullName;
        }
        var poolKey = PoolFor(nation, ground);
        if (!_namePools.TryGetValue(poolKey, out var pool) && !_namePools.TryGetValue("default", out pool))
            return "Alexander Vance";
        var h1 = Math.Abs(StableHash(nation?.Tag ?? "NAT", generation * 41 + (isState ? 7 : 19)));
        var h2 = Math.Abs(StableHash(nation?.Tag ?? "NAT", generation * 73 + (isState ? 53 : 89)));
        var given = Cap(pool.Given[h1 % pool.Given.Length]);
        var family = Cap(pool.Family[h2 % pool.Family.Length]);
        if (isState && nation?.Government is "absolute_monarchy" or "constitutional_monarchy" &&
            DynastyCatalog.DynastyOf(nation.Tag) is { Length: > 0 } house)
            return $"{given} ({Cap(house)})";
        if (poolKey is "chinese" or "korean" or "han" or "japanese")
            return $"{family} {given}";
        return $"{given} {family}";
    }

    /// <summary>The leading people of a country's capital, or of its largest region; empty without a campaign.</summary>
    public static string GroundCulture(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null || nation.MapIndex <= 0) return "";
        ScenarioState seat = null;
        foreach (var state in campaign.States)
        {
            if (state.NationMapIndex != nation.MapIndex) continue;
            if (state.MapIndex == nation.CapitalStateMapIndex) { seat = state; break; }
            if (seat is null || state.Population > seat.Population) seat = state;
        }
        return seat is null ? "" : RegionalAdministration.DominantCulture(seat);
    }

    public static string PoolFor(Nation nation, string groundCulture = "")
    {
        if (nation is null) return "default";
        var culture = !string.IsNullOrWhiteSpace(nation.DeclaredCulture)
            ? nation.DeclaredCulture
            : nation.FoundedCultures.FirstOrDefault() ?? "";
        if (string.IsNullOrWhiteSpace(culture)) culture = groundCulture ?? "";
        if (string.IsNullOrWhiteSpace(culture)) culture = DemographyCatalog.PrimaryCulture(nation.Tag);
        if (!string.IsNullOrWhiteSpace(culture))
        {
            var c = culture.ToLowerInvariant();
            if (_namePools.ContainsKey(c)) return c;
            if (c.Contains("russian") || c.Contains("belarus")) return "russian_east_slavic";
            if (c.Contains("ukrain") || c.Contains("ruthen")) return "ukrainian";
            if (c.Contains("polish") || c.Contains("czech") || c.Contains("slovak") || c.Contains("silesian")) return "west_slavic";
            if (c.Contains("serb") || c.Contains("croat") || c.Contains("bosni") || c.Contains("bulgar") || c.Contains("sloven") || c.Contains("macedon") || c.Contains("montenegr")) return "south_slavic";
            if (c.Contains("english") || c.Contains("scottish") || c.Contains("welsh") || c.Contains("irish") || c.Contains("anglo") || c.Contains("american") || c.Contains("australian") || c.Contains("canadian")) return "anglo";
            if (c.Contains("french") || c.Contains("walloon") || c.Contains("quebec") || c.Contains("corsican") || c.Contains("breton")) return "french";
            if (c.Contains("german") || c.Contains("dutch") || c.Contains("flemish") || c.Contains("austrian") || c.Contains("swiss") || c.Contains("frisian")) return "germanic";
            if (c.Contains("swed") || c.Contains("norweg") || c.Contains("danish") || c.Contains("finn") || c.Contains("iceland") || c.Contains("sami")) return "nordic";
            if (c.Contains("italian") || c.Contains("sicilian") || c.Contains("sardinian") || c.Contains("maltese")) return "italian";
            if (c.Contains("spanish") || c.Contains("castilian") || c.Contains("catalan") || c.Contains("basque") || c.Contains("galician") || c.Contains("andalus") || c.Contains("hispanic") || c.Contains("mestizo") || c.Contains("mexican")) return "hispanic";
            if (c.Contains("portug") || c.Contains("brazil")) return "lusophone";
            if (c.Contains("greek") || c.Contains("hellen") || c.Contains("romanian") || c.Contains("moldov") || c.Contains("albanian") || c.Contains("hungarian") || c.Contains("magyar")) return "balkan_hellenic";
            if (c.Contains("lithuan") || c.Contains("latvian") || c.Contains("eston")) return "baltic";
            if (c.Contains("georgian") || c.Contains("armenian") || c.Contains("abkhaz") || c.Contains("osset") || c.Contains("chechen") || c.Contains("dagestan")) return "caucasian";
            if (c.Contains("arab") || c.Contains("bedouin") || c.Contains("palestin") || c.Contains("yemen") || c.Contains("syrian") || c.Contains("iraqi") || c.Contains("egypt") || c.Contains("maghreb") || c.Contains("berber") || c.Contains("amazigh") || c.Contains("sudan")) return "arabic";
            if (c.Contains("persian") || c.Contains("iran") || c.Contains("kurd") || c.Contains("pashtun") || c.Contains("tajik") || c.Contains("baloch") || c.Contains("hazara") || c.Contains("lur")) return "persian_kurdish";
            if (c.Contains("hebrew") || c.Contains("jewish") || c.Contains("israel")) return "hebrew";
            if (c.Contains("turk") || c.Contains("azer") || c.Contains("kazakh") || c.Contains("uzbek") || c.Contains("kyrgyz") || c.Contains("tatar") || c.Contains("bashkir") || c.Contains("uyghur") || c.Contains("chuvash") || c.Contains("yakut")) return "turkic";
            if (c.Contains("han") || c.Contains("chinese") || c.Contains("canton") || c.Contains("hakka") || c.Contains("hoklo") || c.Contains("tibet")) return "chinese";
            if (c.Contains("japan") || c.Contains("ryukyu")) return "japanese";
            if (c.Contains("korean")) return "korean";
            if (c.Contains("mongol") || c.Contains("buryat") || c.Contains("kalmyk")) return "mongolic";
            if (c.Contains("viet") || c.Contains("thai") || c.Contains("burm") || c.Contains("bamar") || c.Contains("rakhine") || c.Contains("kachin") || c.Contains("shan") || c.Contains("karen") || c.Contains("khmer") || c.Contains("lao") || c.Contains("malay") || c.Contains("javan") || c.Contains("sundan") || c.Contains("filipin") || c.Contains("tagalog") || c.Contains("cebuan") || c.Contains("ilocan") || c.Contains("moro")) return "southeast_asian";
            if (c.Contains("hindi") || c.Contains("bengal") || c.Contains("punjab") || c.Contains("tamil") || c.Contains("telugu") || c.Contains("marath") || c.Contains("gujarat") || c.Contains("kannad") || c.Contains("malayal") || c.Contains("odia") || c.Contains("assam") || c.Contains("sindh") || c.Contains("urdu") || c.Contains("sinhal") || c.Contains("nepal") || c.Contains("kashmir")) return "south_asian";
            if (c.Contains("yoruba") || c.Contains("igbo") || c.Contains("hausa") || c.Contains("fulani") || c.Contains("akan") || c.Contains("wolof") || c.Contains("mandin") || c.Contains("bambara") || c.Contains("moss") || c.Contains("ewe") || c.Contains("fon")) return "west_african";
            if (c.Contains("amhar") || c.Contains("orom") || c.Contains("tigray") || c.Contains("tigrin") || c.Contains("somal") || c.Contains("swahil") || c.Contains("kikuy") || c.Contains("luo") || c.Contains("kalenjin") || c.Contains("bagand") || c.Contains("tutsi") || c.Contains("hutu") || c.Contains("dinka") || c.Contains("nuer") || c.Contains("nuba")) return "east_horn_african";
            if (c.Contains("zulu") || c.Contains("xhosa") || c.Contains("sotho") || c.Contains("tswana") || c.Contains("shona") || c.Contains("ndebele") || c.Contains("kongo") || c.Contains("luba") || c.Contains("mongo") || c.Contains("lingala") || c.Contains("bemba") || c.Contains("chewa") || c.Contains("ovambo") || c.Contains("malagas") || c.Contains("afrikan")) return "central_southern_african";
        }

        var tag = (nation.Tag ?? "").ToUpperInvariant();
        if (tag is "RUS" or "BLR" or "PMR") return "russian_east_slavic";
        if (tag is "UKR") return "ukrainian";
        if (tag is "POL" or "CZE" or "SVK") return "west_slavic";
        if (tag is "SRB" or "HRV" or "BGR" or "SVN" or "BIH" or "MKD" or "MNE") return "south_slavic";
        if (tag is "USA" or "GBR" or "CAN" or "AUS" or "NZL" or "IRL" or "JAM" or "TTO" or "BHS" or "BRB" or "GUY" or "BLZ" or "FJI" or "PNG" or "SLB" or "VUT" or "WSM" or "TON") return "anglo";
        if (tag is "FRA" or "MCO" or "HTI") return "french";
        if (tag is "DEU" or "AUT" or "CHE" or "LIE" or "LUX" or "NLD" or "BEL") return "germanic";
        if (tag is "SWE" or "NOR" or "DNK" or "FIN" or "ISL") return "nordic";
        if (tag is "ITA" or "SMR" or "VAT" or "MLT") return "italian";
        if (tag is "PRT" or "BRA" or "AGO" or "MOZ" or "CPV" or "GNB" or "STP") return "lusophone";
        if (tag is "ESP" or "AND" or "MEX" or "ARG" or "COL" or "CHL" or "PER" or "VEN" or "ECU" or "BOL" or "PRY" or "URY" or "CUB" or "DOM" or "GTM" or "HND" or "SLV" or "NIC" or "CRI" or "PAN" or "GNQ") return "hispanic";
        if (tag is "GRC" or "CYP" or "ROU" or "MDA" or "ALB" or "KOS" or "HUN") return "balkan_hellenic";
        if (tag is "LTU" or "LVA" or "EST") return "baltic";
        if (tag is "GEO" or "ARM" or "ABK" or "SOS") return "caucasian";
        if (tag is "TUR" or "AZE" or "KAZ" or "UZB" or "TKM" or "KGZ" or "TRN") return "turkic";
        if (tag is "IRN" or "AFG" or "TJK" or "KRG" or "SDF") return "persian_kurdish";
        if (tag is "ISR") return "hebrew";
        if (tag is "SAU" or "IRQ" or "SYR" or "SIG" or "HTS" or "JOR" or "LBN" or "YEM" or "HTH" or "STC" or "OMN" or "ARE" or "QAT" or "KWT" or "BHR" or "EGY" or "LBY" or "LNA" or "TUN" or "DZA" or "MAR" or "SDN" or "RSF" or "SPN" or "PSE" or "HMS" or "MRT" or "COM") return "arabic";
        if (tag is "CHN" or "TWN" or "HKG" or "MAC" or "SGP") return "chinese";
        if (tag is "JPN") return "japanese";
        if (tag is "KOR" or "PRK") return "korean";
        if (tag is "MNG") return "mongolic";
        if (tag is "VNM" or "THA" or "MMR" or "NUG" or "ARA" or "KIO" or "UWS" or "MND" or "TNL" or "KHM" or "LAO" or "IDN" or "MYS" or "PHL" or "BRN" or "TLS") return "southeast_asian";
        if (tag is "IND" or "PAK" or "BGD" or "LKA" or "NPL" or "BTN" or "MDV" or "MUS") return "south_asian";
        if (tag is "NGA" or "GHA" or "SEN" or "MLI" or "CIV" or "BFA" or "GIN" or "BEN" or "TGO" or "NER" or "SLE" or "LBR" or "GMB") return "west_african";
        if (tag is "ETH" or "KEN" or "TZA" or "UGA" or "RWA" or "BDI" or "SOM" or "SHB" or "PNT" or "SLD" or "ERI" or "DJI" or "SSD" or "SYC") return "east_horn_african";
        if (tag is "ZAF" or "COD" or "AFC" or "COG" or "CMR" or "GAB" or "TCD" or "CAF" or "ZWE" or "ZMB" or "MWI" or "NAM" or "BWA" or "LSO" or "SWZ" or "MDG") return "central_southern_african";
        if (string.Equals(nation.Continent, "Africa", StringComparison.OrdinalIgnoreCase))
            return "west_african";
        // The small territories the tag table never listed: the Caribbean and the Atlantic
        // islands are British, French or Hispanic in their public life; the Pacific ones
        // are Polynesian; the Faroes and Greenland are Nordic.
        if (tag is "ABW" or "AIA" or "ATG" or "BMU" or "CYM" or "DMA" or "GRD" or "KNA" or "LCA"
            or "MSR" or "TCA" or "VCT" or "VGB" or "VIR" or "IMN") return "anglo";
        if (tag is "CUW" or "SXM" or "PRI" or "ASM" or "GUM" or "MNP" or "SUR") return "hispanic";
        if (tag is "FRO" or "GRL") return "nordic";
        if (tag is "FSM" or "KIR" or "MHL" or "NRU" or "PLW") return "polynesian";
        if (tag is "NCL") return "french";
        return "default";
    }

    private static string Cap(string s) =>
        string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s[1..];

    internal static int StableHash(string text, int salt)
    {
        unchecked
        {
            var h = 2166136261u ^ (uint)salt;
            if (text is not null)
                for (var i = 0; i < text.Length; i++)
                    h = (h ^ char.ToUpperInvariant(text[i])) * 16777619u;
            return (int)(h & 0x7FFFFFFF);
        }
    }

    private static string Text(JsonElement el, string key, string fallback = "") =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private static int Int(JsonElement el, string key, int fallback = 0) =>
        el.TryGetProperty(key, out var v) && v.TryGetInt32(out var n) ? n : fallback;

    private static float Float(JsonElement el, string key, float fallback = 0f) =>
        el.TryGetProperty(key, out var v) && v.TryGetSingle(out var n) ? n : fallback;

    private static bool Flag(JsonElement el, string key, bool fallback = false) =>
        el.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;

    private static string[] ArrayStrings(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
            : Array.Empty<string>();
}

public sealed class AppointMinisterCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public string PortfolioId { get; init; } = "";
    public string OptionId { get; init; } = "";
    public bool PromoteToLeader { get; init; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        if (campaign is null || !campaign.Defines.RulersEnabled || !campaign.FeatureEnabled("rulers")) return;
        var nation = campaign?.NationById(IssuerId);
        if (nation is null) return;
        if (PromoteToLeader)
        {
            if (!PoliticalAppointments.MayPromoteMinister(campaign, nation, PortfolioId, out var cost, out _)) return;
            nation.Treasury -= cost;
            var ministerName = Rulers.MinisterPersonName(nation, PortfolioId);
            var ministerBirthYear = Rulers.MinisterBirthYear(campaign, nation, PortfolioId);
            nation.CabinetPeople.TryGetValue(PortfolioId, out var knownPerson);
            var traitId = knownPerson is { TraitId.Length: > 0 } ? knownPerson.TraitId
                : Rulers.AuthoredMinister(nation.Tag, PortfolioId) is { Trait.Length: > 0 } authTrait ? authTrait.Trait
                : PortfolioId switch
                {
                    "foreign" => "diplomat",
                    "economy" => "industrialist",
                    "defense" => "veteran_commander",
                    _ => "reformer"
                };
            if (Rulers.HasDualExecutive(nation))
            {
                nation.HeadOfGovernmentName = ministerName;
                nation.HeadOfGovernmentTrait = traitId;
                nation.HeadOfGovernmentBirthYear = ministerBirthYear;
                nation.HeadOfGovernmentSinceDay = campaign.ElapsedDays;
            }
            else
            {
                nation.HeadOfStateName = ministerName;
                nation.HeadOfStateTrait = traitId;
                nation.HeadOfStateBirthYear = ministerBirthYear;
                nation.HeadOfStateSinceDay = campaign.ElapsedDays;
            }
            nation.LastCabinetChangeDay = campaign.ElapsedDays;
            NationalModifiers.Invalidate(nation);
            campaign.Chronicle.Record(campaign, "ruler_changed", nation.Id, place: ministerName);
            campaign.MarkStrategicChange();
            return;
        }

        if (!Rulers.CanAppointMinister(campaign, nation, PortfolioId, OptionId, out _)) return;
        var price = Rulers.CabinetAppointmentCost(campaign, nation);
        nation.Treasury -= price;
        var hadMinister = nation.CabinetPeople.ContainsKey(PortfolioId);
        nation.Cabinet[PortfolioId] = OptionId;
        var authoredMin = !hadMinister ? Rulers.AuthoredMinister(nation.Tag, PortfolioId) : null;
        var appointedName = authoredMin is { Name.Length: > 0 }
            ? authoredMin.Name
            : Rulers.GenerateName(nation, Rulers.StableHash(PortfolioId + ":" + OptionId, campaign.ElapsedDays),
                false, campaign.Seed, campaign);
        var appointedBirthYear = authoredMin is { Age: > 0 }
            ? Rulers.CalendarOriginYear(campaign) - Math.Clamp(authoredMin.Age, 18, 99)
            : campaign.Date.Year - Rulers.GeneratedMinisterAge(nation, PortfolioId + ":" + OptionId + ":" + campaign.ElapsedDays);
        nation.CabinetPeople[PortfolioId] = new MinisterPerson
        {
            Name = appointedName,
            BirthYear = appointedBirthYear,
            TraitId = authoredMin?.Trait ?? "",
            AppointedDay = campaign.ElapsedDays
        };
        nation.LastCabinetChangeDay = campaign.ElapsedDays;
        ParliamentaryPolitics.OnMinisterAppointed(campaign, nation, PortfolioId);
        NationalModifiers.Invalidate(nation);
        campaign.MarkStrategicChange();
    }
}

public sealed class RecruitPoliticianCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int CandidateSlot { get; init; }
    public bool AsNationalLeader { get; init; }
    public bool RefreshPool { get; init; }
    public string ExpectedName { get; init; } = "";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        if (campaign is null || !campaign.Defines.RulersEnabled || !campaign.FeatureEnabled("rulers")) return;
        var nation = campaign?.NationById(IssuerId);
        if (nation is null) return;
        var baseCost = Rulers.CabinetAppointmentCost(campaign, nation);
        if (RefreshPool)
        {
            var refreshPrice = baseCost * 0.5f;
            if (nation.Treasury < refreshPrice) return;
            nation.Treasury -= refreshPrice;
            nation.RulerGeneration++;
            campaign.MarkStrategicChange();
            return;
        }
        if (!PoliticalAppointments.MayRecruit(campaign, nation, CandidateSlot, AsNationalLeader,
            out var candidate, out var price, out _)) return;
        if (!string.IsNullOrEmpty(ExpectedName) && ExpectedName != candidate.Name) return;
        nation.Treasury -= price;
        if (AsNationalLeader)
        {
            if (Rulers.HasDualExecutive(nation))
            {
                nation.HeadOfGovernmentName = candidate.Name;
                nation.HeadOfGovernmentTrait = candidate.TraitId;
                nation.HeadOfGovernmentBirthYear = candidate.BirthYear;
                nation.HeadOfGovernmentSinceDay = campaign.ElapsedDays;
            }
            else
            {
                nation.HeadOfStateName = candidate.Name;
                nation.HeadOfStateTrait = candidate.TraitId;
                nation.HeadOfStateBirthYear = candidate.BirthYear;
                nation.HeadOfStateSinceDay = campaign.ElapsedDays;
            }
            campaign.Chronicle.Record(campaign, "ruler_changed", nation.Id, place: candidate.Name);
        }
        else
        {
            nation.Cabinet[candidate.PortfolioId] = candidate.OptionId;
            nation.CabinetPeople[candidate.PortfolioId] = new MinisterPerson
            {
                Name = candidate.Name, BirthYear = candidate.BirthYear,
                TraitId = candidate.TraitId, AppointedDay = campaign.ElapsedDays
            };
            ParliamentaryPolitics.OnMinisterAppointed(campaign, nation, candidate.PortfolioId);
        }
        nation.RulerGeneration++;
        nation.LastCabinetChangeDay = campaign.ElapsedDays;
        NationalModifiers.Invalidate(nation);
        campaign.MarkStrategicChange();
    }
}

public static class RulerTraits
{
    public static void ResetCatalog() => Rulers.ResetCatalog();
}
