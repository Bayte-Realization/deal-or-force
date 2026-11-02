using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public sealed partial class Nation
{
    /// <summary>
    /// The history/factions entry this country was founded from on the first day, or empty. A
    /// faction is a real power that held ground of its own on 1 January 2024 without being a
    /// recognised state - the Houthis in Sanaa, the Rapid Support Forces in Darfur - and the id
    /// is what lets the interface say so, and say who it is.
    /// </summary>
    public string FactionId = "";
}

/// <summary>One de facto power of the bookmark: who it is, whose ground it holds, and how it stands with that state.</summary>
public sealed class FactionDefinition
{
    public string Id = "";
    public string Tag = "";
    public string Name = "";
    public string Description = "";
    public string Parent = "";
    public string Colour = "#6E7B8B";
    public string Capital = "";
    public float CapitalLatitude = float.NaN;
    public float CapitalLongitude = float.NaN;
    /// <summary>Where the parent government actually sat, when the war had moved it: Port Sudan, Aden.</summary>
    public string ParentCapital = "";
    public float ParentCapitalLatitude = float.NaN;
    public float ParentCapitalLongitude = float.NaN;
    public string Government = "provisional_government";
    public string Ideology = "nationalism";
    public List<string> Peoples = new();
    /// <summary>Whole regions it owns.</summary>
    public List<string> Regions = new();
    /// <summary>Areas it owns: every province of the parent's whose middle lies inside one of these (longitude, latitude) rings.</summary>
    public List<(float Longitude, float Latitude)[]> Areas = new();
    /// <summary>Areas inside its regions that it does not own - a port the government still holds.</summary>
    public List<(float Longitude, float Latitude)[]> Except = new();
    /// <summary>Ground of the parent's it holds at war without owning it: the Rapid Support Forces in Khartoum.</summary>
    public List<string> HoldsRegions = new();
    public List<(float Longitude, float Latitude)[]> Holds = new();
    /// <summary>"war", "truce" or "peace".</summary>
    public string Relation = "war";
    public string WarName = "";
    public int TruceDays = 365;
    /// <summary>The share of the parent's formations that are the faction's on the first day, and the militia it stands up besides.</summary>
    public float ArmyShare;
    public int Militia;
    /// <summary>Fighters under arms of its own, stood up as formations of the parent's size: the parent's order of battle never counted them.</summary>
    public int Fighters;
    /// <summary>Additional wartime formations stood up for the parent government when this civil war opens.</summary>
    public int ParentFighters;
    /// <summary>Factions against which this faction joins its parent's active war as a co-belligerent (e.g. STC fighting HTH alongside YEM).</summary>
    public List<string> CoBelligerentAgainst = new();
    public float Stability = 0.45f;
    public float WarSupport = 0.75f;
    /// <summary>A state it answers to, and how: the Syrian Interim Government and Turkey.</summary>
    public string Overlord = "";
    public string SubjectType = "";
    /// <summary>The countries that keep it going, whose opinion of it starts at "armed us" whether or not there is a war to arm it in.</summary>
    public List<string> Patrons = new();
    /// <summary>
    /// The states that list it as a terrorist organisation or sanction it as one. The computer in
    /// those capitals never arms it, whatever it thinks of the government it fights: the United
    /// States backing the Rapid Support Forces against Khartoum was the arithmetic of opinion
    /// and nothing else.
    /// </summary>
    public List<string> ListedBy = new();
    /// <summary>
    /// A condition of the parent's that the war it is losing had put on it, for the same years as
    /// the faction's own: Myanmar's army after Operation 1027, surrendering by the battalion.
    /// </summary>
    public string ParentModifier = "";
    /// <summary>Whether it fights on after losing most of its ground while it has an army: an insurgency, not a state.</summary>
    public bool Insurgency = true;
    /// <summary>
    /// Whether it means to rule the whole country rather than its own part of it: the Rapid
    /// Support Forces, the Houthis, the National Unity Government. Such a faction that takes the
    /// government's capital and most of its ground takes the state (Factions.TakeOver).
    /// </summary>
    public bool ClaimsState;
}

/// <summary>
/// The de facto powers the world of 2024 opened with: history/factions/2024.
///
/// The bookmark's map was the map of recognised states, and the wars that were actually being
/// fought on 1 January 2024 were mostly not between them. Sudan had been at war with the Rapid
/// Support Forces for nine months and the RSF held all of Darfur but one city; the Houthis had
/// governed Sanaa for nine years; Idlib, the north-east of Syria and the east of Libya were
/// run by somebody other than the government the map painted there; the Arakan Army and the
/// Kachin were taking towns from Myanmar's army every week. All of that was in the game only as
/// landless movements that had to rise from nothing - and a movement founded on a world that
/// does not know its war is already being fought spent its first years failing to start it.
///
/// A faction starts as what it was: a country with ground of its own, cut out of its parent by
/// region and, where a front ran through the middle of one, by province; with the part of the
/// parent's army that was really its army; and at war, in a truce or at peace with that parent
/// as it was that morning. It is a breakaway to the rules (<see cref="Reintegration"/>): the
/// state it holds ground of can write that ground back in when it wins, and does not hand it
/// over for a white peace while it is winning.
///
///   "factions": [{
///     "id": "rapid_support_forces", "tag": "RSF", "name": "Rapid Support Forces",
///     "parent": "SDN", "colour": "#8d6e63", "capital": "Nyala", "capital_at": [24.88, 12.05],
///     "government": "military_junta", "ideology": "nationalism", "peoples": ["arab_sudanese"],
///     "regions": ["sd_ds"], "areas": [[[lon, lat], ...]], "except": [...],
///     "holds_regions": ["sd_kh"], "holds": [...],
///     "relation": "war", "war_name": "Sudanese Civil War", "truce_days": 365,
///     "army_share": 0.4, "militia": 4, "stability": 0.4, "war_support": 0.8,
///     "overlord": "TUR", "subject_type": "client", "patrons": ["ARE"]
///   }],
///   "tensions": [...], "sponsorships": [...]   (as history/conflicts, applied once the factions exist)
///
/// Founded once, on a new 2024 world, when its map is attached - before stability is calibrated,
/// so every faction's institutions are measured from the world it actually opens in. A save, or a
/// world a multiplayer client adopts, already has them.
/// </summary>
public static class Factions
{
    private static List<FactionDefinition> _catalog;
    private static readonly List<string> _documents = new();

    public static IReadOnlyList<FactionDefinition> All => _catalog ??= Load();

    public static FactionDefinition Of(Nation nation) =>
        nation is null || string.IsNullOrEmpty(nation.FactionId) ? null : All.FirstOrDefault(f => f.Id == nation.FactionId);

    public static void ResetCatalog() { _catalog = null; _documents.Clear(); }

    /// <summary>A mod's common/factions document, layered over the bookmark's.</summary>
    public static void Merge(JsonElement root, List<string> errors)
    {
        var json = root.GetRawText();
        _catalog ??= Load();
        _documents.Add(json);
        Merge(_catalog, json, errors);
    }

    private static List<FactionDefinition> Load()
    {
        var result = new List<FactionDefinition>();
        _documents.Clear();
        foreach (var file in GameDataLoader.DataFiles("history", "factions", "2024"))
        {
            if (file is null || !File.Exists(file)) continue;
            try
            {
                var json = StructuredData.ReadJson(file);
                _documents.Add(json);
                Merge(result, json, null);
            }
            catch (Exception exception)
            {
                DiagnosticLogs.Write("simulation", $"HISTORY_FACTIONS_FAILED file={Path.GetFileName(file)} error={exception.Message}");
            }
        }
        return result;
    }

    /// <summary>Read one document's factions into the list; a later entry with the same id replaces the earlier.</summary>
    public static void Merge(List<FactionDefinition> into, string json, List<string> errors)
    {
        using var document = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("factions", out var rows) ||
            rows.ValueKind != JsonValueKind.Array) return;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            var definition = new FactionDefinition
            {
                Id = Text(row, "id"),
                Tag = Text(row, "tag").ToUpperInvariant(),
                Name = Text(row, "name"),
                Description = Text(row, "description"),
                Parent = Text(row, "parent").ToUpperInvariant(),
                Capital = Text(row, "capital"),
                Relation = Text(row, "relation", "war").ToLowerInvariant(),
                WarName = Text(row, "war_name"),
                Government = Text(row, "government", "provisional_government"),
                Ideology = Text(row, "ideology", "nationalism"),
                Overlord = Text(row, "overlord").ToUpperInvariant(),
                SubjectType = Text(row, "subject_type")
            };
            if (definition.Id.Length == 0) definition.Id = definition.Tag.ToLowerInvariant();
            if (definition.Tag.Length == 0 || definition.Parent.Length == 0)
            {
                errors?.Add($"history/factions: '{definition.Id}' needs a tag and a parent.");
                continue;
            }
            if (Text(row, "colour") is { Length: > 0 } colour) definition.Colour = colour;
            if (row.TryGetProperty("capital_at", out var at) && at.ValueKind == JsonValueKind.Array && at.GetArrayLength() == 2)
            {
                definition.CapitalLongitude = at[0].GetSingle();
                definition.CapitalLatitude = at[1].GetSingle();
            }
            definition.ParentCapital = Text(row, "parent_capital");
            if (row.TryGetProperty("parent_capital_at", out var seat) && seat.ValueKind == JsonValueKind.Array && seat.GetArrayLength() == 2)
            {
                definition.ParentCapitalLongitude = seat[0].GetSingle();
                definition.ParentCapitalLatitude = seat[1].GetSingle();
            }
            definition.Peoples = Strings(row, "peoples");
            definition.Regions = Strings(row, "regions");
            definition.HoldsRegions = Strings(row, "holds_regions");
            definition.Patrons = Strings(row, "patrons").Select(tag => tag.ToUpperInvariant()).ToList();
            definition.ListedBy = Strings(row, "listed_by").Select(tag => tag.ToUpperInvariant()).ToList();
            definition.ParentModifier = Text(row, "parent_modifier");
            if (row.TryGetProperty("insurgency", out var insurgency) && insurgency.ValueKind is JsonValueKind.True or JsonValueKind.False)
                definition.Insurgency = insurgency.GetBoolean();
            if (row.TryGetProperty("claims_state", out var claims) && claims.ValueKind is JsonValueKind.True or JsonValueKind.False)
                definition.ClaimsState = claims.GetBoolean();
            definition.Areas = Rings(row, "areas");
            definition.Except = Rings(row, "except");
            definition.Holds = Rings(row, "holds");
            if (row.TryGetProperty("truce_days", out var truce) && truce.TryGetInt32(out var days)) definition.TruceDays = Math.Max(1, days);
            if (row.TryGetProperty("militia", out var militia) && militia.TryGetInt32(out var count)) definition.Militia = Math.Max(0, count);
            if (Number(row, "army_share") is { } share) definition.ArmyShare = M.Clamp01(share);
            if (row.TryGetProperty("fighters", out var fighters) && fighters.TryGetInt32(out var men)) definition.Fighters = Math.Max(0, men);
            if (row.TryGetProperty("parent_fighters", out var parentFighters) && parentFighters.TryGetInt32(out var parentMen)) definition.ParentFighters = Math.Max(0, parentMen);
            definition.CoBelligerentAgainst = Strings(row, "co_belligerent_against").Select(tag => tag.ToUpperInvariant()).ToList();
            if (Number(row, "stability") is { } stability) definition.Stability = M.Clamp01(stability);
            if (Number(row, "war_support") is { } support) definition.WarSupport = M.Clamp01(support);
            into.RemoveAll(existing => existing.Id == definition.Id);
            into.Add(definition);
        }
    }

    /// <summary>
    /// Found every faction the world of 2024 has, on a world nobody has played yet. Order is
    /// the file's: a province goes to the first faction that asks for it, so a narrow strip
    /// one faction holds inside a region another holds whole is written first.
    /// </summary>
    public static int Found(Campaign campaign)
    {
        if (campaign?.ProvinceMap is null || campaign.ScenarioId != "2024" || campaign.ElapsedDays != 0 ||
            campaign.InitialDeploymentCompleted || !campaign.FeatureEnabled("de_facto_factions")) return 0;
        var founded = new List<Nation>();
        foreach (var definition in All)
        {
            if (campaign.NationByTag(definition.Tag) is not null) continue;
            var born = Found(campaign, definition);
            if (born is not null) founded.Add(born);
        }
        if (founded.Count == 0) return 0;

        campaign.LocateCapitals(preserveExisting: true);
        // The tensions and the backers that name a faction, which could not be read before it existed.
        foreach (var json in _documents)
        {
            var errors = new List<string>();
            ConflictHistory.Apply(campaign, json, errors);
            foreach (var error in errors) DiagnosticLogs.Write("simulation", $"HISTORY_FACTIONS {error}");
        }
        campaign.MarkStrategicChange();
        campaign.MarkMapChanged();
        return founded.Count;
    }

    // ------------------------------------------------------------------ the fall of a capital

    /// <summary>
    /// The week a faction that claims the state has the government's capital and most of its
    /// ground, the government falls: Kabul in August 2021, Damascus in December 2024. The faction
    /// takes every province the government still had, its name, its seat and its colour, and the
    /// civil war is over; the government that fell goes into exile or is gone the way any
    /// country that loses the last of its ground does (MovementFall). The state's other wars end
    /// with it - the side that won one war does not inherit the loser's others.
    /// </summary>
    public static void Tick(Campaign campaign)
    {
        if (campaign?.ProvinceMap is null) return;
        SettleDefenselessRebelRemnants(campaign);
        if (campaign.ElapsedDays % 7 != 4 || !campaign.FeatureEnabled("de_facto_factions")) return;
        foreach (var faction in campaign.Nations.ToArray())
        {
            if (campaign.Fallen.Contains(faction.Id)) continue;
            var parent = campaign.NationById(faction.SecededFromId);
            if (parent is null || campaign.Fallen.Contains(parent.Id) || parent.Movement is not null) continue;
            var war = campaign.Wars.FirstOrDefault(w => !w.Ended && w.Involves(faction.Id) && w.Involves(parent.Id) &&
                                                       w.Opposes(faction.Id, parent.Id));
            if (war is null) continue;
            if (Of(faction) is not { ClaimsState: true } && !Reintegration.ClaimsParentState(campaign, war, faction, parent)) continue;
            if (!HoldsTheState(campaign, faction, parent)) continue;
            TakeOver(campaign, faction, parent, war);
        }
    }

    /// <summary>Whether the faction stands in the government's capital and on most of the government's ground.</summary>
    public static bool HoldsTheState(Campaign campaign, Nation faction, Nation parent)
    {
        var map = campaign.ProvinceMap;
        var control = campaign.ProvinceController;
        if (map is null || control is null || parent.CapitalProvinceId <= 0 || parent.CapitalProvinceId > map.ProvinceCount) return false;
        if (control[parent.CapitalProvinceId] != (ushort)faction.MapIndex) return false;
        var owned = 0;
        var held = 0;
        for (var province = 1; province <= map.ProvinceCount; province++)
        {
            if (map.ProvinceToNation[province] != (ushort)parent.MapIndex || map.IsWater(province)) continue;
            owned++;
            if (control[province] == (ushort)faction.MapIndex) held++;
        }
        var shareNeeded = parent.Stability < 0.40f ? 0.22f : campaign.Defines.FactionTakeOverShare;
        return owned > 0 && held >= owned * shareNeeded;
    }

    public static void TakeOver(Campaign campaign, Nation faction, Nation parent, StrategicWar war)
    {
        var map = campaign.ProvinceMap;
        var whole = Reintegration.Everything(campaign, parent);
        foreach (var province in whole) campaign.SetProvinceController(province, (ushort)faction.MapIndex);
        StatePartition.Annex(campaign, whole, faction, asConquest: false);

        war.Ended = true;
        campaign.MarkWarsChanged();
        war.EndedDay = campaign.ElapsedDays;
        war.WinnerId = faction.Id;
        war.AnnexedProvinces = whole.Count;
        war.PeaceSummary = $"{faction.Name} took power in all of {parent.Name}.";

        // The fallen government's other wars end with it. What its enemies held of its ground they
        // give back to the new government, which never fought them.
        foreach (var other in campaign.Wars)
        {
            if (other.Ended || !other.Involves(parent.Id)) continue;
            foreach (var (province, was) in other.OriginalController)
                campaign.SetProvinceController(province, was == (ushort)parent.MapIndex ? (ushort)faction.MapIndex : was);
            other.Ended = true;
            other.EndedDay = campaign.ElapsedDays;
            other.WinnerId = -1;
            other.PeaceSummary = $"Ended with the fall of the government of {parent.Name}.";
            foreach (var enemyId in other.EnemiesOf(parent.Id))
                if (CampaignDiplomacy.Peek(campaign, faction.Id, enemyId) is { Stance: DiplomaticStance.AtWar } stance &&
                    !campaign.Wars.Any(w => !w.Ended && w.Opposes(faction.Id, enemyId)))
                    stance.Stance = DiplomaticStance.Neutral;
        }

        // It is the state now: its name, its seat, its colour, and the peoples it is for.
        var formerName = faction.Name;
        faction.CustomName = NationNaming.TakeoverName(campaign, faction, parent);
        faction.Name = parent.Name;
        faction.NameProclaimed = true;
        // It is that country now: its name, and its flag in whatever form its politics give it.
        faction.FoundNameKind = "tag";
        faction.FoundNameId = parent.Tag;
        faction.FoundNameBase = "";
        faction.NameSettledDay = campaign.ElapsedDays;
        faction.Color = parent.Color;
        faction.CapitalName = parent.CapitalName;
        faction.CapitalLatitude = parent.CapitalLatitude;
        faction.CapitalLongitude = parent.CapitalLongitude;
        faction.CapitalProvinceId = faction.CapitalStateMapIndex = faction.GovernmentSeatProvinceId = 0;
        faction.SecededFromId = -1;
        faction.FactionId = "";
        faction.PoliticsChangedDay = campaign.ElapsedDays;
        foreach (var people in parent.FoundedCultures)
            if (!faction.FoundedCultures.Contains(people, StringComparer.OrdinalIgnoreCase)) faction.FoundedCultures.Add(people);
        if (faction.FoundedCultures.Count > 0) DemographyCatalog.Declare(faction.Tag, faction.FoundedCultures);
        faction.Treasury += MathF.Max(0f, parent.Treasury) * 0.5f;
        parent.Treasury *= 0.5f;
        if (!campaign.HasOwnedLand(parent) && parent.Movement is null)
        {
            parent.Population = 0;
            parent.Gdp = 0f;
            parent.GrossOutput = 0f;
            parent.WeeklyTaxIncome = parent.WeeklyExpenses = parent.WeeklyBalance = 0f;
        }

        campaign.LocateCapitals(preserveExisting: true);
        Warfare.RebuildFronts(campaign);
        campaign.Chronicle.Record(campaign, "regime_change", faction.Id, parent.Id, place: formerName);
        campaign.MarkStrategicChange();
        campaign.MarkMapChanged();
        DiagnosticLogs.Write("simulation", $"TAKEOVER faction={faction.Tag} former={formerName} state={parent.Tag} provinces={whole.Count}");
    }

    /// <summary>
    /// When a civil-war opposition, de-facto faction, or breakaway wins or settles a war,
    /// consolidate its state identity if it completely replaced its parent, or shed its temporary
    /// "rising:..." front label and provisional status so it becomes a regular state.
    /// </summary>
    /// <summary>
    /// Whether a side that rose against a state rose on that state's own homeland - a civil war,
    /// whose winner becomes the state - rather than on its own, whose winner stays itself and
    /// only takes the ground. A faction of the state - the Houthis of Yemen - is the state's own
    /// civil war wherever it stands. With no homeland on record, the old answer: yes.
    /// </summary>
    internal static bool RoseOnHomelandOf(Campaign campaign, Nation participant, Nation parent)
    {
        if (participant.FactionId.Length > 0) return true;
        if (!campaign.Homelands.TryGetValue(participant.Id, out var home) || home is not { Count: > 0 }) return true;
        // Most of it, not any of it: a region the front cut in two is the homeland of both sides,
        // and one such piece of Donetsk would make all of Ukraine Russia's.
        var shared = 0;
        foreach (var index in home)
            if (campaign.States.Find(s => s.MapIndex == index) is { } region &&
                TerritorialClaims.TitleOf(campaign, parent, region) == TerritorialTitle.Homeland) shared++;
        return shared * 2 > home.Count;
    }

    public static void SettleCivilWarSurvivors(Campaign campaign, StrategicWar war)
    {
        if (campaign is null || war is null) return;
        foreach (var participantId in war.Side(true).Concat(war.Side(false)))
        {
            var participant = campaign.NationById(participantId);
            if (participant is null) continue;
            if (!campaign.HasOwnedLand(participant))
            {
                if (participant.Movement is null)
                {
                    participant.Gdp = 0f;
                    participant.GrossOutput = 0f;
                    participant.WeeklyTaxIncome = participant.WeeklyExpenses = participant.WeeklyBalance = 0f;
                }
                continue;
            }
            if (participant.Name.StartsWith("rising:", StringComparison.Ordinal))
            {
                var parts = participant.Name.Split(':', 4);
                var parentTag = parts.Length >= 3 ? parts[2] : "";
                var parent = campaign.NationByTag(parentTag) ?? campaign.NationById(participant.SecededFromId);
                if (war.WinnerId == participant.Id && parent is not null && !campaign.HasOwnedLand(parent) &&
                    RoseOnHomelandOf(campaign, participant, parent))
                {
                    var formerName = parts.Length >= 4 ? parts[3] : participant.Name;
                    participant.Name = parent.Name;
                    participant.NameProclaimed = true;
                    participant.CustomName = NationNaming.TakeoverName(campaign, participant, parent);
                    // It is that country now: its name, and its flag in whatever form its politics give it.
                    participant.FoundNameKind = "tag";
                    participant.FoundNameId = parent.Tag;
                    participant.FoundNameBase = "";
                    participant.NameSettledDay = campaign.ElapsedDays;
                    participant.Color = parent.Color;
                    participant.CapitalName = parent.CapitalName;
                    participant.CapitalLatitude = parent.CapitalLatitude;
                    participant.CapitalLongitude = parent.CapitalLongitude;
                    participant.CapitalProvinceId = participant.CapitalStateMapIndex = participant.GovernmentSeatProvinceId = 0;
                    participant.SecededFromId = -1;
                    participant.FactionId = "";
                    participant.PoliticsChangedDay = campaign.ElapsedDays;
                    if (string.IsNullOrEmpty(participant.FormedNationId))
                        participant.FormedNationId = FormableRestorations.Prefix + parent.Tag.ToLowerInvariant();
                    campaign.LocateCapitals(preserveExisting: true);
                    campaign.Chronicle.Record(campaign, "regime_change", participant.Id, parent.Id, place: formerName);
                }
                else if (parts.Length >= 4)
                {
                    participant.Name = parts[3];
                    participant.NameProclaimed = true;
                }
                if (participant.Government == "provisional_government")
                    participant.Government = participant.Ideology switch
                    {
                        "communism" or "socialism" => "one_party_state",
                        "nationalism" or "fascism" => "presidential_republic",
                        "theocratic_rule" => "theocracy",
                        _ => "parliamentary_republic"
                    };
            }
            else if (participant.SecededFromId >= 0 && war.WinnerId == participant.Id &&
                     campaign.NationById(participant.SecededFromId) is { } parent &&
                     !campaign.HasOwnedLand(parent) && RoseOnHomelandOf(campaign, participant, parent))
            {
                var formerName = participant.Name;
                participant.CustomName = NationNaming.TakeoverName(campaign, participant, parent);
                participant.Name = parent.Name;
                participant.NameProclaimed = true;
                // It is that country now: its name, and its flag in whatever form its politics give it.
                participant.FoundNameKind = "tag";
                participant.FoundNameId = parent.Tag;
                participant.FoundNameBase = "";
                participant.NameSettledDay = campaign.ElapsedDays;
                participant.Color = parent.Color;
                participant.CapitalName = parent.CapitalName;
                participant.CapitalLatitude = parent.CapitalLatitude;
                participant.CapitalLongitude = parent.CapitalLongitude;
                participant.CapitalProvinceId = participant.CapitalStateMapIndex = participant.GovernmentSeatProvinceId = 0;
                participant.SecededFromId = -1;
                participant.FactionId = "";
                participant.PoliticsChangedDay = campaign.ElapsedDays;
                foreach (var people in parent.FoundedCultures)
                    if (!participant.FoundedCultures.Contains(people, StringComparer.OrdinalIgnoreCase))
                        participant.FoundedCultures.Add(people);
                if (participant.FoundedCultures.Count > 0)
                    DemographyCatalog.Declare(participant.Tag, participant.FoundedCultures);
                if (participant.Government == "provisional_government")
                    participant.Government = participant.Ideology switch
                    {
                        "communism" or "socialism" => "one_party_state",
                        "nationalism" or "fascism" => "presidential_republic",
                        "theocratic_rule" => "theocracy",
                        _ => "parliamentary_republic"
                    };
                campaign.LocateCapitals(preserveExisting: true);
                campaign.Chronicle.Record(campaign, "regime_change", participant.Id, parent.Id, place: formerName);
            }
            else if (participant.SecededFromId >= 0 && war.WinnerId != participant.Id &&
                     campaign.NationById(participant.SecededFromId) is { } defeatedBy &&
                     (war.WinnerId == defeatedBy.Id || war.IsAttacking(defeatedBy.Id) == war.IsAttacking(war.WinnerId)))
            {
                var survivingArmies = campaign.Armies.Count(a => a.NationId == participant.Id && a.Strength > 0.05f);
                var remainingStates = campaign.StatesOf(participant);
                if (survivingArmies == 0 || remainingStates.Length <= 2 || !campaign.HasOwnedLand(participant))
                {
                    var whole = Reintegration.Everything(campaign, participant);
                    if (whole.Count > 0)
                    {
                        foreach (var p in whole) campaign.SetProvinceController(p, (ushort)defeatedBy.MapIndex);
                        StatePartition.Annex(campaign, whole, defeatedBy, asConquest: false);
                    }
                    participant.Population = 0;
                    participant.Gdp = 0f;
                    participant.GrossOutput = 0f;
                    participant.WeeklyTaxIncome = participant.WeeklyExpenses = participant.WeeklyBalance = 0f;
                    campaign.Chronicle.Record(campaign, "reintegration", defeatedBy.Id, participant.Id, place: participant.DisplayName);
                    DiagnosticLogs.Write("simulation", $"REBEL_REINTEGRATED rebel={participant.Tag} parent={defeatedBy.Tag} provinces={whole.Count}");
                }
            }
            else if (participant.Government == "provisional_government" && !string.IsNullOrEmpty(participant.FoundedInRevolt))
            {
                participant.Government = participant.Ideology switch
                {
                    "communism" or "socialism" => "one_party_state",
                    "theocratic_rule" => "theocracy",
                    _ => "parliamentary_republic"
                };
            }
        }
    }

    /// <summary>
    /// If an uprising, civil-war rebel, or breakaway has lost its army (0 divisions) and holds
    /// only a tiny rump actually occupied by its parent in their active war, settle that
    /// military defeat. Having no divisions is NOT consent to annexation: a peaceful,
    /// protected or newly founded country may legitimately have no standing army.
    /// </summary>
    internal static void SettleDefenselessRebelRemnants(Campaign campaign)
    {
        if (campaign?.ProvinceMap is null || campaign.ElapsedDays < 10) return;
        foreach (var rebel in campaign.Nations.ToArray())
        {
            if (rebel.IsHuman || !campaign.HasOwnedLand(rebel)) continue;
            var parent = campaign.NationById(rebel.SecededFromId);
            if (parent is null && !string.IsNullOrEmpty(rebel.FoundedInRevolt))
            {
                var states = campaign.StatesOf(rebel);
                if (states.Length > 0 && states[0].CoreTags.Count > 0)
                {
                    parent = campaign.NationByTag(states[0].CoreTags[0]);
                }
            }
            if (parent is null && rebel.Name.StartsWith("rising:", StringComparison.Ordinal))
            {
                var states = campaign.StatesOf(rebel);
                if (states.Length > 0 && states[0].CoreTags.Count > 0)
                {
                    foreach (var core in states[0].CoreTags)
                    {
                        if (campaign.NationByTag(core) is { } candidate && candidate.Id != rebel.Id && campaign.HasOwnedLand(candidate))
                        {
                            parent = candidate;
                            break;
                        }
                    }
                }
            }
            if (parent is null || parent.Id == rebel.Id || !campaign.HasOwnedLand(parent)) continue;
            var war = campaign.Wars.FirstOrDefault(w => !w.Ended && w.Opposes(parent.Id, rebel.Id));
            if (war is null) continue;

            var rebelStates = campaign.StatesOf(rebel);
            if (rebelStates.Length > 2) continue;
            // A country whose ground is its homeland and not the parent's is a
            // state that threw off an overlord, not the rump of a rising. Liechtenstein, Monaco
            // and San Marino, subjects for a few years, broke free and were "reabsorbed" the next
            // day because they have no army: twenty-five years on, every microstate in Europe was gone.
            if (rebelStates.Any(s => TerritorialClaims.TitleOf(campaign, rebel, s) == TerritorialTitle.Homeland) &&
                !rebelStates.Any(s => TerritorialClaims.TitleOf(campaign, parent, s) == TerritorialTitle.Homeland)) continue;

            var hasDivisions = campaign.Armies.Any(a => a.NationId == rebel.Id && a.Strength > 0.05f);
            if (hasDivisions) continue;

            var foreignWars = campaign.Wars.Any(w => !w.Ended && w.Involves(rebel.Id) && !w.Involves(parent.Id));
            if (foreignWars) continue;

            var isRebel = rebel.SecededFromId == parent.Id ||
                          !string.IsNullOrEmpty(rebel.FoundedInRevolt) ||
                          rebel.Name.StartsWith("rising:", StringComparison.Ordinal);
            if (!isRebel) continue;

            var whole = Reintegration.Everything(campaign, rebel);
            // Never manufacture victory by painting over the defender's own or a
            // protector's troops. An unopposed capture must still exist in the war ledger.
            if (whole.Count == 0 || campaign.ProvinceController is null ||
                !whole.All(p => campaign.ProvinceController[p] == parent.MapIndex) ||
                !whole.Any(p => war.OriginalController.TryGetValue(p, out var before) && before == rebel.MapIndex)) continue;
            if (whole.Count > 0)
            {
                foreach (var p in whole) campaign.SetProvinceController(p, (ushort)parent.MapIndex);
                StatePartition.Annex(campaign, whole, parent, asConquest: false);
            }
            rebel.Population = 0;
            rebel.Gdp = 0f;
            rebel.GrossOutput = 0f;
            rebel.WeeklyTaxIncome = rebel.WeeklyExpenses = rebel.WeeklyBalance = 0f;
            campaign.Chronicle.Record(campaign, "reintegration", parent.Id, rebel.Id, place: rebel.DisplayName);
            campaign.MarkStrategicChange();
            campaign.MarkMapChanged();
            Warfare.RebuildFronts(campaign);
            DiagnosticLogs.Write("simulation", $"REBEL_REMNANT_REABSORBED rebel={rebel.Tag} parent={parent.Tag} provinces={whole.Count}");
        }
    }

    /// <summary>Whether that state lists this faction as a terrorist or sanctioned organisation.</summary>
    public static bool ListedBy(Nation faction, Nation state) =>
        faction is not null && state is not null && Of(faction) is { } definition && definition.ListedBy.Contains(state.Tag);

    /// <summary>
    /// Whether a faction that has lost most of its ground goes on fighting: an insurgency with
    /// formations still in the field does, until they are gone or nobody believes in the war.
    /// The Kachin Independence Army has lost towns for sixty years without surrendering once.
    /// </summary>
    public static bool FightsOn(Campaign campaign, Nation faction, float held)
    {
        // Every rising and breakaway, not only the insurgencies the 2024 history names: a war
        // of the weak is not given up because the strong hold the towns (GuerrillaWar).
        if (campaign is null || faction is null) return false;
        if (Of(faction) is not { Insurgency: true } &&
            !(campaign.Defines.RisingsFightOn && (faction.SecededFromId >= 0 || !string.IsNullOrEmpty(faction.FoundedInRevolt) ||
                                                  faction.Name.StartsWith("rising:", StringComparison.Ordinal)))) return false;
        if (held >= 0.999f && faction.WarSupport <= campaign.Defines.FactionFightOnSupport * 2f) return false;
        if (faction.WarSupport <= campaign.Defines.FactionFightOnSupport) return false;
        var standing = 0;
        foreach (var army in campaign.Armies)
            if (army.NationId == faction.Id && army.Strength > 0f) standing++;
        return standing >= campaign.Defines.FactionFightOnFormations;
    }

    private static Nation Found(Campaign campaign, FactionDefinition definition)
    {
        var topology = campaign.ProvinceMap;
        var parent = campaign.NationByTag(definition.Parent);
        if (parent is null) { Log(definition, "no parent"); return null; }

        var ground = Ground(campaign, parent, definition.Regions, definition.Areas, definition.Except);
        if (ground.Count == 0) { Log(definition, "no ground"); return null; }
        var mapIndex = FreeMapIndex(campaign);
        if (mapIndex <= 0) { Log(definition, "no map index"); return null; }

        var touched = new HashSet<int>();
        foreach (var province in ground) touched.Add(topology.ProvinceToState[province]);
        var parentPopulation = Math.Max(1L, campaign.StatesOf(parent).Sum(state => Math.Max(0L, state.Population)));

        var born = campaign.AddNation(new Nation
        {
            Tag = definition.Tag,
            Iso = definition.Tag,
            Name = definition.Name.Length > 0 ? definition.Name : definition.Tag,
            Continent = parent.Continent,
            Region = parent.Region,
            Color = definition.Colour,
            MapIndex = mapIndex,
            CapitalName = definition.Capital,
            OutputScale = parent.OutputScale,
            StandardOfLiving = parent.StandardOfLiving,
            Literacy = parent.Literacy,
            LiteracyOffset = parent.LiteracyOffset,
            Productivity = parent.Productivity,
            Urbanisation = parent.Urbanisation,
            AgricultureShare = parent.AgricultureShare,
            TaxRate = parent.TaxRate,
            WelfareSpending = parent.WelfareSpending,
            EducationSpending = parent.EducationSpending,
            Stability = definition.Stability,
            WarSupport = definition.WarSupport,
            Government = definition.Government,
            Ideology = definition.Ideology,
            PoliticsChangedDay = -3650
        });
        born.FactionId = definition.Id;
        born.SecededFromId = parent.Id;
        born.RevoltParentId = parent.Id;
        if (definition.ParentCapital.Length > 0 && !float.IsNaN(definition.ParentCapitalLatitude))
        {
            parent.CapitalName = definition.ParentCapital;
            parent.CapitalLatitude = definition.ParentCapitalLatitude;
            parent.CapitalLongitude = definition.ParentCapitalLongitude;
            // This is an authored change of city (Marib / Port Sudan), not a saved
            // government's lost capital. The final locate pass must place the new city.
            parent.CapitalProvinceId = parent.CapitalStateMapIndex = parent.GovernmentSeatProvinceId = 0;
        }
        if (!float.IsNaN(definition.CapitalLatitude))
        {
            born.CapitalLatitude = definition.CapitalLatitude;
            born.CapitalLongitude = definition.CapitalLongitude;
        }

        StatePartition.Annex(campaign, ground, born, asConquest: false);
        // The faction's authored ground is its homeland from day one, so the fall flow can
        // see it if it ever loses all of it (a faction that falls is a faction that ended).
        var own = campaign.StatesOf(born);
        campaign.Homelands[born.Id] = own.Select(state => state.MapIndex).ToList();
        var people = own.Sum(state => Math.Max(0L, state.Population));
        var share = M.Clamp01(people / (float)parentPopulation);
        born.Population = people;
        born.Gdp = MathF.Max(0.01f, parent.Gdp * share);
        born.Treasury = MathF.Max(0f, parent.Treasury * share);
        born.ManpowerReserve = parent.ManpowerReserve * share;
        if (own.Length > 0) born.Urbanisation = own.Average(state => state.Urbanization);
        SecessionResearch.Inherit(campaign, parent, born, own, parentPopulation);

        var peoples = definition.Peoples.Count > 0 ? definition.Peoples : new List<string>(parent.FoundedCultures);
        born.FoundedCultures.Clear();
        born.FoundedCultures.AddRange(peoples);
        if (peoples.Count > 0) DemographyCatalog.Declare(born.Tag, peoples);

        var formations = TakeFormations(campaign, parent, born, definition.ArmyShare) +
                         StandUp(campaign, born, definition.Fighters);
        if (definition.ParentFighters > 0)
            StandUp(campaign, parent, definition.ParentFighters);
        var raised = 0;
        var regions = own.OrderByDescending(state => state.Population).ToList();
        for (var attempt = 0; attempt < definition.Militia && regions.Count > 0; attempt++)
            if (MilitiaCore.Found(campaign, born, regions[attempt % regions.Count])) raised++;

        Stand(campaign, definition, parent, born);
        // Years of fighting on its own ground are what it has instead of a general staff.
        if (definition.Insurgency && definition.Relation != "peace")
            ModifierSystem.Grant(campaign, born, "seasoned_insurgency", Math.Max(1, campaign.Defines.FactionSeasonedDays));
        if (definition.ParentModifier.Length > 0)
            ModifierSystem.Grant(campaign, parent, definition.ParentModifier, Math.Max(1, campaign.Defines.FactionSeasonedDays));
        if (definition.Overlord.Length > 0) Answer(campaign, definition, born);
        foreach (var tag in definition.Patrons)
            if (campaign.NationByTag(tag) is { } patron && patron.Id != born.Id)
            {
                OpinionSystem.Remember(campaign, CampaignDiplomacy.Get(campaign, patron.Id, born.Id), "armed_us");
                OpinionSystem.Remember(campaign, CampaignDiplomacy.Get(campaign, patron.Id, parent.Id), "armed_our_enemy");
            }

        DiagnosticLogs.Write("simulation",
            $"FACTION {born.Tag} parent={parent.Tag} provinces={ground.Count} regions={own.Length} touched={touched.Count} " +
            $"people={people} formations={formations} militia={raised} relation={definition.Relation}");
        return born;
    }

    /// <summary>How the faction and its parent stand on the first day.</summary>
    private static void Stand(Campaign campaign, FactionDefinition definition, Nation parent, Nation born)
    {
        var relation = CampaignDiplomacy.Get(campaign, parent.Id, born.Id);
        OpinionSystem.Remember(campaign, relation, "seceded_from_us");
        switch (definition.Relation)
        {
            case "war":
            {
                relation.Stance = DiplomaticStance.AtWar;
                OpinionSystem.Remember(campaign, relation, "fought_a_war");
                parent.WarSupport = MathF.Max(parent.WarSupport, definition.WarSupport * 0.9f);
                var claimed = campaign.States.FirstOrDefault(state => state.NationMapIndex == born.MapIndex &&
                                  string.Equals(state.Name, definition.Capital, StringComparison.OrdinalIgnoreCase)) ??
                              campaign.StatesOf(born).OrderByDescending(state => state.Population).FirstOrDefault();
                var war = new StrategicWar
                {
                    Id = campaign.Wars.Count == 0 ? 0 : campaign.Wars.Max(other => other.Id) + 1,
                    Name = definition.WarName.Length > 0 ? definition.WarName : $"{parent.DisplayName}–{born.DisplayName} War",
                    AttackerId = parent.Id,
                    DefenderId = born.Id,
                    StartedDay = campaign.ElapsedDays,
                    CivilConflict = true,
                    AiNegotiationUnlockDay = campaign.ElapsedDays + Math.Max(0, campaign.Defines.CivilWarNegotiationDays),
                    ClaimedStateMapIndex = claimed?.MapIndex ?? -1
                };
                campaign.Wars.Add(war);
                campaign.MarkWarsChanged();
                // What it holds of the parent's own ground besides: occupied, not owned, so that
                // a peace is what decides whose it is.
                var held = Ground(campaign, parent, definition.HoldsRegions, definition.Holds, new());
                foreach (var province in held) campaign.SetProvinceController(province, (ushort)born.MapIndex);
                break;
            }
            case "truce":
                relation.Stance = DiplomaticStance.Truce;
                relation.TruceUntil = campaign.ElapsedDays + definition.TruceDays;
                OpinionSystem.Remember(campaign, relation, "fought_a_war");
                break;
        }
        if (definition.CoBelligerentAgainst.Count > 0)
        {
            relation.Stance = DiplomaticStance.Allied;
            relation.AllianceSinceDay = campaign.ElapsedDays;
            foreach (var enemyTag in definition.CoBelligerentAgainst)
            {
                var enemy = campaign.NationByTag(enemyTag);
                if (enemy is null || enemy.Id == born.Id) continue;
                var war = campaign.Wars.FirstOrDefault(w => !w.Ended && w.Involves(parent.Id) && w.Involves(enemy.Id) && w.Opposes(parent.Id, enemy.Id));
                if (war is null) continue;
                if (war.IsAttacking(parent.Id)) war.AttackerAllies.Add(born.Id);
                else war.DefenderAllies.Add(born.Id);
                var enemyRelation = CampaignDiplomacy.Get(campaign, born.Id, enemy.Id);
                enemyRelation.Stance = DiplomaticStance.AtWar;
                OpinionSystem.Remember(campaign, enemyRelation, "fought_a_war");
                campaign.MarkWarsChanged();
            }
        }
    }

    private static void Answer(Campaign campaign, FactionDefinition definition, Nation born)
    {
        var overlord = campaign.NationByTag(definition.Overlord);
        if (overlord is null || overlord.Id == born.Id || !SubjectCatalog.Knows(definition.SubjectType)) return;
        born.OverlordId = overlord.Id;
        born.SubjectType = definition.SubjectType;
        born.SubjectSinceDay = campaign.ElapsedDays - 1500;
        born.LibertyDesire = 0f;
        OpinionSystem.Remember(campaign, CampaignDiplomacy.Get(campaign, overlord.Id, born.Id), "historic_friendship");
    }

    /// <summary>
    /// The part of the parent's army that was the faction's: every n-th formation by id, so it
    /// gets a cross-section of what the parent had rather than its best or its worst. The
    /// deployment pass that follows posts them on the faction's own ground.
    /// </summary>
    private static int TakeFormations(Campaign campaign, Nation parent, Nation born, float share)
    {
        if (share <= 0f) return 0;
        var theirs = campaign.Armies.Where(army => army.NationId == parent.Id && army.EmbarkedFleetId < 0)
            .OrderBy(army => army.Id).ToList();
        var wanted = (int)MathF.Round(theirs.Count * share);
        if (wanted <= 0) return 0;
        var step = theirs.Count / (float)wanted;
        var moved = 0;
        for (var index = 0; index < wanted; index++)
        {
            var army = theirs[Math.Min(theirs.Count - 1, (int)(index * step))];
            if (army.NationId == born.Id) continue;
            army.NationId = born.Id;
            army.AssignedWarId = -1;
            army.ArmyGroupId = -1;
            army.FrontLineId = -1;
            army.OrderKind = ArmyOrder.Manual;
            army.Route.Clear();
            army.PlannedRoute.Clear();
            army.DirectOrder = false;
            moved++;
        }
        return moved;
    }

    /// <summary>
    /// Its own fighters as formations. Battle-hardened and badly equipped - an insurgency's
    /// army - and posted with everybody else's by the deployment pass that follows.
    /// </summary>
    private static int StandUp(Campaign campaign, Nation born, int fighters)
    {
        if (fighters <= 0) return 0;
        var size = MilitaryBootstrap.DivisionSize;
        var count = Math.Max(1, (int)MathF.Round(fighters / size));
        var left = fighters;
        var id = campaign.Armies.Count == 0 ? 0 : campaign.Armies.Max(army => army.Id);
        for (var index = 0; index < count; index++)
        {
            var men = index == count - 1 ? Math.Max(1000, left) : (int)Math.Min(size, left);
            left -= men;
            var variation = (born.MapIndex * 97 + index * 37) & 255;
            campaign.Armies.Add(new Army
            {
                Id = ++id,
                NationId = born.Id,
                Divisions = 1f,
                Name = $"{index + 1}. {born.Tag} Brigade",
                TemplateId = "line_infantry",
                ArmyGroupId = -1,
                Personnel = men,
                MaxPersonnel = (int)size,
                Strength = MathF.Min(1f, men / size),
                Experience = 0.30f + variation % 21 / 100f,
                Equipment = 0.50f + variation % 15 / 100f,
                Cohesion = 0.72f + variation % 15 / 100f,
                Recon = 0.45f,
                Engineers = 0.30f,
                Artillery = 0.25f,
                Latitude = born.CapitalLatitude,
                Longitude = born.CapitalLongitude,
                OrderKind = ArmyOrder.Manual
            });
        }
        return count;
    }

    /// <summary>The parent's land provinces in these regions or inside these rings, less the ones inside an exception.</summary>
    private static List<int> Ground(Campaign campaign, Nation parent, List<string> regions,
        List<(float Longitude, float Latitude)[]> areas, List<(float Longitude, float Latitude)[]> except)
    {
        var topology = campaign.ProvinceMap;
        var wanted = new HashSet<int>();
        foreach (var id in regions)
            if (Movements.StateById(campaign, id) is { } state) wanted.Add(state.MapIndex);
        var result = new List<int>();
        if (wanted.Count == 0 && areas.Count == 0) return result;
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            if (topology.ProvinceToNation[province] != (ushort)parent.MapIndex || topology.IsWater(province)) continue;
            var centre = topology.CenterOf(province);
            var inside = wanted.Contains(topology.ProvinceToState[province]) ||
                         areas.Any(ring => Inside(ring, centre.Longitude, centre.Latitude));
            if (!inside || except.Any(ring => Inside(ring, centre.Longitude, centre.Latitude))) continue;
            result.Add(province);
        }
        return result;
    }

    /// <summary>Whether a point is inside a ring, by the even-odd rule.</summary>
    public static bool Inside((float Longitude, float Latitude)[] ring, float longitude, float latitude)
    {
        if (ring is null || ring.Length < 3) return false;
        var inside = false;
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
        {
            var (xi, yi) = ring[i];
            var (xj, yj) = ring[j];
            if ((yi > latitude) != (yj > latitude) &&
                longitude < (xj - xi) * (latitude - yi) / (yj - yi) + xi)
                inside = !inside;
        }
        return inside;
    }

    private static int FreeMapIndex(Campaign campaign)
    {
        var taken = new HashSet<int>();
        foreach (var nation in campaign.Nations) taken.Add(nation.MapIndex);
        for (var index = SecessionEvents.FirstDynamicMapIndex; index < 65535; index++)
            if (!taken.Contains(index)) return index;
        return 0;
    }

    private static void Log(FactionDefinition definition, string why) =>
        DiagnosticLogs.Write("simulation", $"FACTION_SKIPPED {definition.Tag} parent={definition.Parent} reason={why}");

    private static string Text(JsonElement row, string key, string fallback = "") =>
        row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;

    private static float? Number(JsonElement row, string key) =>
        row.TryGetProperty(key, out var value) && value.TryGetSingle(out var number) ? number : null;

    private static List<string> Strings(JsonElement row, string key)
    {
        var result = new List<string>();
        if (!row.TryGetProperty(key, out var array) || array.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in array.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                result.Add(item.GetString());
        return result;
    }

    private static List<(float Longitude, float Latitude)[]> Rings(JsonElement row, string key)
    {
        var result = new List<(float, float)[]>();
        if (!row.TryGetProperty(key, out var rings) || rings.ValueKind != JsonValueKind.Array) return result;
        foreach (var ring in rings.EnumerateArray())
        {
            if (ring.ValueKind != JsonValueKind.Array) continue;
            var points = new List<(float, float)>();
            foreach (var point in ring.EnumerateArray())
                if (point.ValueKind == JsonValueKind.Array && point.GetArrayLength() == 2)
                    points.Add((point[0].GetSingle(), point[1].GetSingle()));
            if (points.Count >= 3) result.Add(points.ToArray());
        }
        return result;
    }
}
