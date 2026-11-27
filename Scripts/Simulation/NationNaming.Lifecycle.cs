using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public static partial class NationNaming
{
    /// <summary>Host-only migration of the old generated tag leak. Never rewrites player
    /// custom names, arbitrary prose, or a network snapshot independently on a client.</summary>
    public static int MigrateLegacy(Campaign campaign)
    {
        if (campaign is null) return 0;
        var changed = 0;
        foreach (var nation in campaign.Nations)
        {
            if (!string.IsNullOrWhiteSpace(nation.CustomName)) continue;

            // Active civil-war fronts legitimately keep a single-level "rising:..." identifier
            // while fighting, so only flatten nested "rising:...:rising:..." recipes on them.
            if (string.IsNullOrEmpty(nation.FoundNameKind) &&
                !string.IsNullOrEmpty(nation.Name) &&
                nation.Name.StartsWith("rising:", StringComparison.Ordinal))
            {
                var parts = nation.Name.Split(':', 4);
                var frontRepaired = false;
                if (parts.Length == 4 && CountryNames.HasRisingRecipe(parts[3]) &&
                    CountryNames.TryUnwrapRisingBase(nation.Name, out var innerTag, out var innerLabel))
                {
                    var cleanTag = (!string.IsNullOrEmpty(innerTag) ? innerTag : parts[2]).Replace(':', ' ').Trim();
                    var cleanLabel = (!string.IsNullOrWhiteSpace(innerLabel) ? innerLabel : CountryNames.Of(cleanTag))
                        .Replace(':', ' ').Trim();
                    nation.Name = $"rising:{parts[1]}:{cleanTag}:{cleanLabel}";
                    frontRepaired = true;
                }
                if (CountryNames.HasRisingRecipe(nation.FoundNameId)) { nation.FoundNameId = ""; frontRepaired = true; }
                if (CountryNames.HasRisingRecipe(nation.FoundNameBase)) { nation.FoundNameBase = ""; frontRepaired = true; }
                if (frontRepaired) changed++;
                continue;
            }

            // Repair settled/region/literal identities holding a leaked "rising:" string or an
            // unbound "region" identity with empty FoundNameId (which produced "region..name").
            var hasRisingBase = CountryNames.HasRisingRecipe(nation.FoundNameBase);
            var hasRisingId = CountryNames.HasRisingRecipe(nation.FoundNameId);
            var hasRisingName = !string.IsNullOrEmpty(nation.Name) &&
                (nation.Name.StartsWith("rising:", StringComparison.Ordinal) || CountryNames.HasRisingRecipe(nation.Name));
            var isUnboundRegion = nation.FoundNameKind == "region" && string.IsNullOrEmpty(nation.FoundNameId);
            if (hasRisingBase || hasRisingId || hasRisingName || isUnboundRegion)
            {
                var repaired = false;
                var largest = LargestOwnedState(campaign, nation);
                var risingRecipe = FindRisingRecipe(nation.FoundNameId, nation.FoundNameBase, nation.Name);
                string innerTag = "", innerLabel = "";
                var unwrapped = risingRecipe.Length > 0 &&
                    CountryNames.TryUnwrapRisingBase(risingRecipe, out innerTag, out innerLabel);
                if (!unwrapped) { innerTag = ""; innerLabel = ""; }

                if (isUnboundRegion)
                {
                    if (largest is not null)
                    {
                        nation.FoundNameId = largest.Id;
                        repaired = true;
                    }
                    else if (unwrapped && !string.IsNullOrEmpty(innerTag) && Loc.Has("country." + innerTag + ".name"))
                    {
                        nation.FoundNameKind = "tag";
                        nation.FoundNameId = innerTag;
                        repaired = true;
                    }
                    else
                    {
                        var fallback = unwrapped && !string.IsNullOrWhiteSpace(innerLabel)
                            ? innerLabel
                            : CountryNames.SanitizeRisingText(nation.FoundNameBase ?? "");
                        if (string.IsNullOrWhiteSpace(fallback))
                            fallback = CountryNames.SanitizeRisingText(nation.Name ?? "");
                        if (string.IsNullOrWhiteSpace(fallback))
                            fallback = CountryNames.Of(nation.Tag);
                        nation.FoundNameKind = "literal";
                        nation.FoundNameId = fallback;
                        repaired = true;
                    }
                }
                else if (hasRisingId ||
                         (nation.FoundNameKind == "literal" && string.IsNullOrEmpty(nation.FoundNameId) &&
                          (hasRisingBase || hasRisingName)) ||
                         (string.IsNullOrEmpty(nation.FoundNameKind) && hasRisingName))
                {
                    var tagTaken = unwrapped && !string.IsNullOrEmpty(innerTag) &&
                        IsLivingNameTaken(campaign, nation, CountryNames.Of(innerTag));
                    if (largest is not null &&
                        (tagTaken || !unwrapped || string.IsNullOrEmpty(innerTag) || !Loc.Has("country." + innerTag + ".name")))
                    {
                        nation.FoundNameKind = "region";
                        nation.FoundNameId = largest.Id;
                        repaired = true;
                    }
                    else if (unwrapped && !string.IsNullOrEmpty(innerTag) && Loc.Has("country." + innerTag + ".name"))
                    {
                        nation.FoundNameKind = "tag";
                        nation.FoundNameId = innerTag;
                        repaired = true;
                    }
                    else
                    {
                        var fallback = unwrapped && !string.IsNullOrWhiteSpace(innerLabel)
                            ? innerLabel
                            : CountryNames.SanitizeRisingText(risingRecipe.Length > 0 ? risingRecipe : nation.Name ?? "");
                        if (string.IsNullOrWhiteSpace(fallback)) fallback = CountryNames.Of(nation.Tag);
                        nation.FoundNameKind = "literal";
                        nation.FoundNameId = fallback;
                        repaired = true;
                    }
                }

                if (!string.IsNullOrEmpty(nation.FoundNameKind))
                {
                    if (string.IsNullOrEmpty(nation.FoundNameForm))
                    {
                        nation.FoundNameForm = FormFor(nation.Government, nation.Ideology,
                            nation.DeclaredReligion, nation.DeclaredCulture);
                        repaired = true;
                    }
                    var newBase = CountryNames.SanitizeRisingText(BaseName(campaign, nation));
                    if (!string.Equals(newBase, nation.FoundNameBase, StringComparison.Ordinal))
                    {
                        nation.FoundNameBase = newBase;
                        repaired = true;
                    }
                }
                else if (hasRisingBase)
                {
                    nation.FoundNameBase = CountryNames.SanitizeRisingText(nation.FoundNameBase);
                    repaired = true;
                }

                if (hasRisingName)
                {
                    var rendered = !string.IsNullOrEmpty(nation.FoundNameKind) ? Render(campaign, nation, false) : "";
                    var cleanName = !string.IsNullOrWhiteSpace(rendered) && !LooksLikeKey(rendered) &&
                                    !CountryNames.HasRisingRecipe(rendered)
                        ? rendered
                        : CountryNames.SanitizeRisingText(nation.Name);
                    if (string.IsNullOrWhiteSpace(cleanName) || LooksLikeKey(cleanName))
                        cleanName = !string.IsNullOrWhiteSpace(nation.FoundNameBase)
                            ? nation.FoundNameBase
                            : CountryNames.Of(nation.Tag);
                    if (!string.Equals(cleanName, nation.Name, StringComparison.Ordinal))
                    {
                        nation.Name = cleanName;
                        repaired = true;
                    }
                }

                if (repaired) changed++;
            }

            if (nation.FoundNameKind == "tag" && !Loc.Has("country." + nation.FoundNameId + ".name") &&
                campaign.NationByTag(nation.FoundNameId) is { } source && source != nation)
            {
                var identity = IdentitySource(source);
                nation.FoundNameKind = identity.Kind;
                nation.FoundNameId = identity.Id;
                nation.FoundNameBase = BaseName(campaign, nation);
                changed++;
            }
            // A save written while "Eastern" was treated as a country's whole name repairs
            // the recipe: the side, anchored on the country it was a side of.
            if (nation.FoundNameKind == "region" &&
                FindStateById(campaign, nation.FoundNameId) is { } oldRegion &&
                CompassDirection(RegionNames.Resolve(oldRegion)) is { Length: > 0 } oldCompass &&
                campaign.NationById(nation.ReleasedFromId) is { } oldParent)
            {
                var parentIdentity = IdentitySource(oldParent);
                nation.FoundNameKind = "direction";
                nation.FoundNameId = oldCompass + ":" + parentIdentity.Kind + ":" + parentIdentity.Id;
                nation.FoundNameBase = BaseName(campaign, nation);
                changed++;
            }
            // A name that was persisted as a key - "formable.gulf_union.name" - is written
            // back as the catalogue's name, so history and war labels stop carrying it too.
            if (LooksLikeKey(nation.Name) && !Loc.Has(nation.Name) &&
                FormableIdOf(nation.Name) is { Length: > 0 } keyFormable &&
                FormableNations.Find(keyFormable) is { Name.Length: > 0 } keyDefinition)
            {
                nation.Name = keyDefinition.Name;
                nation.NameProclaimed = true;
                changed++;
            }
            // The same repair one level down: a base stored as a formable key.
            if (nation.FoundNameKind == "literal" && LooksLikeKey(nation.FoundNameId) &&
                !Loc.Has(nation.FoundNameId) &&
                FormableIdOf(nation.FoundNameId) is { Length: > 0 } literalFormable &&
                FormableNations.Find(literalFormable) is not null)
            {
                nation.FoundNameKind = "formable";
                nation.FoundNameId = literalFormable;
                nation.FoundNameBase = BaseName(campaign, nation);
                changed++;
            }
            if (nation.FoundNameKind != "literal") continue;
            // Exact legacy prefixes only. Localised strings were formerly persisted as
            // literal text, so recognise both shipped languages independent of UI language.
            foreach (var (prefix, direction) in LegacyDirections)
            {
                if (!(nation.FoundNameId ?? "").StartsWith(prefix, StringComparison.Ordinal)) continue;
                var tag = nation.FoundNameId[prefix.Length..];
                if (Loc.Has("country." + tag + ".name")) continue;
                var parent = campaign.NationByTag(tag);
                if (parent is null || parent == nation) continue;
                var identity = IdentitySource(parent);
                nation.FoundNameKind = "direction";
                nation.FoundNameId = direction + ":" + identity.Kind + ":" + identity.Id;
                nation.FoundNameBase = BaseName(campaign, nation);
                changed++;
                break;
            }
        }
        if (changed > 0) { campaign.MarkStrategicChange(); campaign.MarkMapChanged(); }
        return changed;
    }

    private static ScenarioState LargestOwnedState(Campaign campaign, Nation nation)
    {
        var states = campaign.StatesOf(nation);
        ScenarioState best = null;
        for (var i = 0; i < states.Length; i++)
        {
            var s = states[i];
            if (string.IsNullOrEmpty(s.Id)) continue;
            if (best is null || s.Population > best.Population ||
                (s.Population == best.Population && s.MapIndex < best.MapIndex))
                best = s;
        }
        return best;
    }

    private static string FindRisingRecipe(params string[] candidates)
    {
        for (var i = 0; i < candidates.Length; i++)
        {
            var candidate = candidates[i];
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (candidate.StartsWith("rising:", StringComparison.Ordinal)) return candidate.Trim();
            var idx = candidate.IndexOf("rising:", StringComparison.Ordinal);
            if (idx >= 0 && (idx + 7 == candidate.Length || !char.IsWhiteSpace(candidate[idx + 7])))
                return candidate[idx..].Trim();
        }
        return "";
    }

    private static bool IsLivingNameTaken(Campaign campaign, Nation self, string candidateName)
    {
        if (campaign is null || string.IsNullOrWhiteSpace(candidateName)) return false;
        foreach (var other in campaign.Nations)
        {
            if (ReferenceEquals(other, self)) continue;
            var holds = campaign.StatesOf(other).Length > 0;
            if (!holds && other.Movement is not { Dissolved: false }) continue;
            if (string.Equals(CountryNames.Resolve(other, false), candidateName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(CountryNames.Resolve(other, true), candidateName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static readonly (string Prefix, string Direction)[] LegacyDirections =
    {
        ("North ", "north"), ("South ", "south"), ("East ", "east"), ("West ", "west"),
        ("Северная ", "north"), ("Южная ", "south"), ("Восточная ", "east"), ("Западная ", "west")
    };

    /// <summary>The composed name, in the reader's language, with the form the politics call for.</summary>
    public static string Render(Campaign campaign, Nation nation, bool map)
    {
        if (nation is null) return "";
        if (!string.IsNullOrWhiteSpace(nation.CustomName)) return Loc.Get(nation.CustomName);
        if (string.IsNullOrWhiteSpace(nation.FoundNameKind)) return "";
        var basis = BaseOf(campaign, nation);
        if (string.IsNullOrWhiteSpace(basis)) return "";
        var form = string.IsNullOrWhiteSpace(nation.FoundNameForm)
            ? FormFor(nation.Government, nation.Ideology, nation.DeclaredReligion, nation.DeclaredCulture)
            : nation.FoundNameForm;
        // A base that already reads as a state takes no second form of state.
        if (!map && CarriesStateForm(basis)) return basis;

        // An adjective base reads the way the country is spoken of - "the Ukrainian Republic",
        // "the Donetsk Republic" - instead of pushing a noun into a form of state. Its noun is
        // kept for the short map label, so a map never carries a form of state.
        var adjectiveBase = nation.FoundNameKind switch
        {
            "land" when !string.Equals(Loc.Language, "english", StringComparison.OrdinalIgnoreCase) =>
                LandAtlas.Find(nation.FoundNameId) is { } landDef ? LandAtlas.Adjective(landDef) : "",
            "culture_adj" => CultureAdjective(nation.FoundNameId),
            // Russian regions are named with adjectives ("Костанайская"), so the form of state
            // goes after them: "Костанайская Республика", not "Республика Костанайская".
            "region" when LooksAdjectival(basis) => basis,
            "region" when string.Equals(Loc.Language, "russian", StringComparison.OrdinalIgnoreCase) =>
                MakeRussianRegionAdjective(basis),
            _ => ""
        };
        if (adjectiveBase.Length > 0)
        {
            if (map)
            {
                var shortName = nation.FoundNameKind == "land"
                    ? LandAtlas.Name(LandAtlas.Find(nation.FoundNameId))
                    : nation.FoundNameKind == "region"
                        ? basis
                        : ShortPeopleName(nation.FoundNameId, adjectiveBase);
                if (shortName.Length > 0) return shortName;
            }
            else
            {
                var adjectiveKey = "name.found." + form + ".adj";
                if (Loc.Has(adjectiveKey))
                    return Loc.Format(adjectiveKey, Agree(adjectiveBase, GenderOf(form)));
            }
        }

        var key = "name.found." + form + (map ? ".map" : "");
        if (Loc.Has(key)) return Loc.Format(key, basis);
        key = "name.found." + form;
        return Loc.Has(key) ? Loc.Format(key, basis) : basis;
    }

    /// <summary>
    /// Give a state that has just been released the name its people, ground and politics
    /// call for. Called after the country exists, because that is the first moment all three
    /// are known; a custom name the player typed is left exactly as typed.
    /// </summary>
    public static void ApplyTo(Campaign campaign, Nation born, Nation parent, int[] regions,
        string convention, string culture)
    {
        if (campaign is null || born is null) return;
        born.ReleasedFromId = parent?.Id ?? -1;
        if (!string.IsNullOrWhiteSpace(born.CustomName))
        {
            born.NameSettledDay = campaign.ElapsedDays;
            return;
        }
        convention = KnownConvention(convention) ? convention : "auto";
        var (kind, id) = BaseFor(campaign, parent, regions ?? Array.Empty<int>(), culture, convention,
            !string.IsNullOrEmpty(born.FoundedInRevolt));
        born.NameConvention = convention;
        born.FoundNameKind = kind;
        born.FoundNameId = id;
        born.FoundNameForm = FormFor(born.Government, born.Ideology, born.DeclaredReligion, culture);
        born.FoundNameBase = BaseName(campaign, born);
        born.NameSettledDay = -1;
        AvoidClash(campaign, born, regions);
        campaign.MarkStrategicChange();
    }

    /// <summary>
    /// A name another living state already wears is not worn twice. Three Eritreas and four
    /// Ukraines at once were what happens when every piece of the same people's ground leaves on
    /// its own: the second piece is called for its largest region instead.
    /// </summary>
    private static void AvoidClash(Campaign campaign, Nation nation, int[] regions)
    {
        if (campaign is null || nation is null || !string.IsNullOrWhiteSpace(nation.CustomName)) return;
        var name = Render(campaign, nation, false);
        if (name.Length == 0) return;
        // The base as well as the whole name: "Republic of Zimbabwe" does not equal "Zimbabwe",
        // and a Shona rising in Mozambique wore it beside the real one. The country it rose from
        // is left out - a claimant to its own state carries that state's name on purpose.
        var basis = BaseOf(campaign, nation);
        var parentId = nation.SecededFromId >= 0 ? nation.SecededFromId : nation.ReleasedFromId;
        var clash = false;
        foreach (var other in campaign.Nations)
        {
            if (ReferenceEquals(other, nation) || other.Id == parentId ||
                (!campaign.HasOwnedLand(other) && other.Movement is not { Dissolved: false })) continue;
            var full = CountryNames.Resolve(other, false);
            var shortName = CountryNames.Resolve(other, true);
            if (!string.Equals(full, name, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(full, basis, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(shortName, basis, StringComparison.OrdinalIgnoreCase)) continue;
            clash = true;
            break;
        }
        if (!clash) return;
        // Its people's adjective first - the Shona Republic - and only then its largest region.
        // Never the people of the country it rose from, nor a people that already has a state of
        // its own: Russians rising in Siberia held by Kazakhstan were the "Kazakh Republic" for a
        // while, because the parent's people had been written down as theirs.
        var parentNation = parentId >= 0 ? campaign.NationById(parentId) : null;
        string people = null;
        for (var i = 0; i < nation.FoundedCultures.Count; i++)
        {
            var c = nation.FoundedCultures[i];
            if (CultureAdjective(c).Length > 0 && !SamePeople(c, parentNation) && !CountryNameTaken(campaign, c))
            {
                people = c;
                break;
            }
        }
        if (people is not null && !(nation.FoundNameKind == "culture_adj" && nation.FoundNameId == people))
        {
            var keptKind = nation.FoundNameKind;
            var keptId = nation.FoundNameId;
            nation.FoundNameKind = "culture_adj";
            nation.FoundNameId = people;
            var renamed = Render(campaign, nation, false);
            if (!NameAlreadyTaken(campaign, renamed)) { nation.FoundNameBase = BaseName(campaign, nation); return; }
            nation.FoundNameKind = keptKind;
            nation.FoundNameId = keptId;
        }
        var region = LargestRegion(campaign, regions);
        if (region is null || (nation.FoundNameKind == "region" && nation.FoundNameId == region.Id)) return;
        nation.FoundNameKind = "region";
        nation.FoundNameId = region.Id;
        nation.FoundNameBase = BaseName(campaign, nation);
    }

    /// <summary>
    /// The name a country takes when it stops being a provisional committee and becomes a
    /// state: the same people and ground, the form the government now has.
    /// </summary>
    public static void Settle(Campaign campaign, Nation nation, string reason)
    {
        if (campaign is null || nation is null) return;
        if (!string.IsNullOrWhiteSpace(nation.CustomName)) { nation.NameSettledDay = campaign.ElapsedDays; return; }
        if (string.IsNullOrEmpty(nation.FoundNameKind)) return;
        var form = FormFor(nation.Government, nation.Ideology, nation.DeclaredReligion, nation.DeclaredCulture);
        if (string.Equals(form, nation.FoundNameForm, StringComparison.OrdinalIgnoreCase))
        {
            nation.NameSettledDay = campaign.ElapsedDays;
            return;
        }
        var before = Render(campaign, nation, false);
        nation.FoundNameForm = form;
        nation.NameSettledDay = campaign.ElapsedDays;
        var after = Render(campaign, nation, false);
        if (!string.Equals(before, after, StringComparison.Ordinal))
            campaign.Chronicle.Record(campaign, "state_named", nation.Id, place: after);
        DiagnosticLogs.Write("simulation",
            $"FOUNDING_NAME nation={nation.Tag} reason={reason} form={form} before={before} after={after} day={campaign.ElapsedDays}");
        campaign.MarkStrategicChange();
    }

    /// <summary>
    /// What a civil-war victor calls the state it has just taken. The ground and the people
    /// are the old country's; the form of state is the victor's, which is the whole point of
    /// having won.
    /// </summary>
    public static string TakeoverName(Campaign campaign, Nation victor, Nation fallen)
    {
        if (campaign is null || victor is null || fallen is null) return fallen?.Name ?? "";
        var victorCulture = DemographyCatalog.PrimaryCulture(victor.Tag);
        var religion = victor.DeclaredReligion;
        if (string.IsNullOrWhiteSpace(religion))
        {
            // A movement that has just taken the state has not declared a faith on paper;
            // the people it rose from have one, and that is what the form of state answers to.
            var culture = DemographyCatalog.Culture(victorCulture);
            if (culture is not null && culture.Religions.Count > 0)
                religion = culture.Religions.OrderByDescending(pair => pair.Value).First().Key;
        }
        var form = FormFor(victor.Government, victor.Ideology, religion, victorCulture);
        var key = "name.found." + form;
        var identity = IdentitySource(fallen);
        // The fallen country's base, never its displayed name: a victor that took over the
        // Republic of India calls the state it now holds the Republic of India, not the
        // Republic of Republic of India.
        var basis = WithoutFormOfState(SourceName(identity.Kind, identity.Id, fallen.FoundNameBase));
        return Loc.Has(key) ? Loc.Format(key, basis) : basis;
    }

    /// <summary>
    /// How much of a founded state's people still live on what its name is for, 0 to 1: the
    /// people's share for a people's name, the land's share for a land, the region's for a
    /// region, the share of ground that is that country's core for a country's. A kind this
    /// cannot measure answers 1 and is never recomposed.
    /// </summary>
    public static float NameHold(Campaign campaign, Nation nation, int[] regions)
    {
        if (campaign is null || nation is null || regions is null || regions.Length == 0) return 1f;
        long total = 0;
        for (var i = 0; i < regions.Length; i++)
        {
            var s = campaign.StateByMapIndex(regions[i]);
            if (s is { Population: > 0 }) total += s.Population;
        }
        if (total <= 0) return 1f;
        var id = nation.FoundNameId ?? "";
        long held = 0;
        switch (nation.FoundNameKind)
        {
            case "culture":
            case "culture_adj":
                for (var i = 0; i < regions.Length; i++)
                {
                    var state = campaign.StateByMapIndex(regions[i]);
                    if (state is not { Population: > 0 }) continue;
                    held += EconomySystem.CultureSize(state, id);
                }
                break;
            case "land":
                return LandAtlas.Find(id) is { } land ? LandAtlas.Share(campaign, land, regions) : 1f;
            case "region":
                for (var i = 0; i < regions.Length; i++)
                {
                    var state = campaign.StateByMapIndex(regions[i]);
                    if (state is not { Population: > 0 }) continue;
                    if (string.Equals(state.Id, id, StringComparison.OrdinalIgnoreCase) ||
                        campaign.StateByMapIndex(state.ParentMapIndex) is { } whole &&
                        string.Equals(whole.Id, id, StringComparison.OrdinalIgnoreCase))
                        held += state.Population;
                }
                // A region names the state it is the seat of, while a fifth of the people live in
                // it. A "Military Command of Maharashtra" ruling 712 million is not Maharashtra's.
                return M.Clamp01(held / (float)total * 1.5f);
            case "tag":
            case "free_tag":
                for (var i = 0; i < regions.Length; i++)
                {
                    var state = campaign.StateByMapIndex(regions[i]);
                    if (state is not { Population: > 0 }) continue;
                    var cores = state.CoreTags;
                    for (var ci = 0; ci < cores.Count; ci++)
                        if (string.Equals(cores[ci], id, StringComparison.OrdinalIgnoreCase)) { held += state.Population; break; }
                }
                break;
            case "literal" when MovementCatalog.Of(nation.Movement?.Id) is { Peoples.Count: > 0 } movement:
                // The state name a movement fought for ("Belarus") is its peoples' name.
                for (var i = 0; i < regions.Length; i++)
                {
                    var state = campaign.StateByMapIndex(regions[i]);
                    if (state is not { Population: > 0 }) continue;
                    held += EconomySystem.PeoplesSize(state, movement.Peoples);
                }
                break;
            default:
                return 1f;
        }
        return M.Clamp01(held / (float)total);
    }

    /// <summary>
    /// Recompose a founded state's name from the ground it holds now, if the name it has stopped
    /// describing that ground. True when it changed. A typed name is never touched.
    /// </summary>
    public static bool Refit(Campaign campaign, Nation nation, string reason)
    {
        if (campaign is null || nation is null || !string.IsNullOrWhiteSpace(nation.CustomName) ||
            string.IsNullOrEmpty(nation.FoundNameKind)) return false;
        var owned = campaign.StatesOf(nation);
        if (owned.Length == 0) return false;
        var regions = new int[owned.Length];
        for (var i = 0; i < owned.Length; i++) regions[i] = owned[i].MapIndex;
        if (NameHold(campaign, nation, regions) >= campaign.Defines.NameHoldMinimum) return false;
        var parent = campaign.NationById(nation.SecededFromId) ?? campaign.NationById(nation.ReleasedFromId);
        var culture = !string.IsNullOrWhiteSpace(nation.DeclaredCulture) && IsPeoplesGround(campaign, regions, nation.DeclaredCulture)
            ? nation.DeclaredCulture : GroundPeople(campaign, regions);
        var (kind, id) = BaseFor(campaign, parent, regions, culture, "auto");
        if (string.Equals(kind, nation.FoundNameKind, StringComparison.Ordinal) &&
            string.Equals(id, nation.FoundNameId, StringComparison.OrdinalIgnoreCase)) return false;
        var before = Render(campaign, nation, false);
        nation.FoundNameKind = kind;
        nation.FoundNameId = id;
        nation.FoundNameForm = FormFor(nation.Government, nation.Ideology, nation.DeclaredReligion, culture);
        nation.FoundNameBase = BaseName(campaign, nation);
        nation.NameSettledDay = campaign.ElapsedDays;
        AvoidClash(campaign, nation, regions);
        var after = Render(campaign, nation, false);
        if (string.Equals(before, after, StringComparison.Ordinal)) return false;
        campaign.Chronicle.Record(campaign, "state_named", nation.Id, place: after);
        DiagnosticLogs.Write("simulation",
            $"FOUNDING_NAME nation={nation.Tag} reason={reason} kind={kind} id={id} before={before} after={after} day={campaign.ElapsedDays}");
        campaign.MarkStrategicChange();
        campaign.MarkMapChanged();
        return true;
    }

    /// <summary>
    /// Once a month: a founded state that has survived and settled stops calling itself a
    /// council and takes the name its politics have earned; one whose name no longer describes
    /// the ground it holds is renamed from that ground; and once settled, the form of state
    /// follows a government that has held for half a year, so a junta is not a "Republic" and a
    /// restored republic is not a "Military Command".
    /// </summary>
    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>The same monthly pass, yielded between countries.</summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (campaign is null) yield break;
        if (campaign.ElapsedDays % 30 != 11) yield break;
        var scanned = 0;
        foreach (var nation in campaign.Nations)
        {
            if (++scanned % 8 == 0 || Campaign.StageOverdue()) yield return 0;
            if (string.IsNullOrEmpty(nation.FoundNameKind) || !string.IsNullOrWhiteSpace(nation.CustomName)) continue;
            // One large founded country's look at its ground is thirty or forty milliseconds of
            // walking its peoples, and cannot be cut. The one that took that long last month
            // starts a frame of its own (StageCosts).
            if (StageCosts.Heavy("naming", nation.Id)) { Campaign.HeavyNext(); yield return 0; }
            var started = StageCosts.Start();
            Review(campaign, nation);
            StageCosts.Stop("naming", nation.Id, started);
        }
    }

    /// <summary>One founded country's month: its name against its ground, then its form against its politics.</summary>
    private static void Review(Campaign campaign, Nation nation)
    {
        if (nation.Movement is null || !nation.Movement.Landless) Refit(campaign, nation, "ground");
        if (nation.NameSettledDay >= 0)
        {
            if (nation.Government != "provisional_government" &&
                campaign.ElapsedDays - nation.PoliticsChangedDay >= campaign.Defines.NameFormFollowsDays &&
                !string.Equals(FormFor(nation.Government, nation.Ideology, nation.DeclaredReligion, nation.DeclaredCulture),
                    nation.FoundNameForm, StringComparison.OrdinalIgnoreCase))
                Settle(campaign, nation, "regime");
            return;
        }
        if (campaign.ElapsedDays < campaign.Defines.FoundingNameSettleDays) return;
        if (nation.Stability < campaign.Defines.FoundingSettleStability) return;
        if (nation.Government == "provisional_government") return;
        Settle(campaign, nation, "settled");
    }
}

/// <summary>Switch the school a country's name is built from, without typing a name.</summary>
public sealed class NameConventionCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; } = -1;
    public string Convention { get; init; } = "auto";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign?.NationById(TargetId >= 0 ? TargetId : IssuerId);
        if (nation is null || !NationNaming.KnownConvention(Convention)) return;
        var owned = campaign.StatesOf(nation);
        var regions = new int[owned.Length];
        for (var i = 0; i < owned.Length; i++) regions[i] = owned[i].MapIndex;
        var (kind, id) = NationNaming.BaseFor(campaign, nation, regions,
            nation.DeclaredCulture, Convention);
        nation.NameConvention = Convention;
        nation.FoundNameKind = kind;
        nation.FoundNameId = id;
        nation.CustomName = "";
        nation.NameSettledDay = -1;
        nation.FoundNameBase = NationNaming.BaseName(campaign, nation);
        campaign.MarkStrategicChange();
        campaign.Chronicle.Record(campaign, "state_restyled", nation.Id,
            place: CountryNames.Resolve(nation, false));
    }
}
