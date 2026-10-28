using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>How much a commander's skill (0 to 1, 0.5 ordinary) moves the weight of the formations under them: 1 + this × (skill − 0.5).</summary>
    public float GeneralSkillWeight { get; set; } = 0.5f;
    /// <summary>What a won battle teaches a commander, and what a lost one costs their standing.</summary>
    public float GeneralVictorySkill { get; set; } = 0.012f;
    public float GeneralDefeatSkill { get; set; } = 0.006f;
    /// <summary>The chance a commander whose side broke is killed, captured or sacked for it.</summary>
    public float GeneralFallChance { get; set; } = 0.03f;
    /// <summary>The men lost on both sides that make a battle one the world hears of by name.</summary>
    public float GreatBattleLosses { get; set; } = 15000f;
}

/// <summary>A commander's temperament: what it does to an attack and to a defence, and how common it is.</summary>
public sealed record GeneralTrait(string Id, string Name, string Description, float Attack, float Defence, float Weight);

/// <summary>
/// Generals: the people a war is remembered by.
///
/// Every army group had a commander's name field reading "Unassigned staff" and a staff skill
/// nothing read. Battles were won by numbers and luck alone, and nobody won them. Now each
/// group has a commander - named from the country's own name pool, with a skill and a
/// temperament (common/generals) - and the formations under them fight at 1 ± half the
/// distance of that skill from the ordinary, by their temperament's attack and defence.
/// Commanders learn from victories and lose standing in defeats; one whose side broke may
/// fall - killed, captured or sacked - and somebody new takes the group. A battle that cost
/// fifteen thousand men is a battle the world hears of by its town's name and by the name
/// of the commander who won it.
///
/// Drawn from the seed and the group, so a campaign replays its generals; another seed has
/// others. The same for every country, the player's included.
/// </summary>
public static class Generals
{
    public const string Unassigned = "Unassigned staff";

    /// <summary>This session's count of named battles and fallen commanders, for the bench. Not campaign state.</summary>
    public static int GreatBattles, CommandersFallen;

    private static readonly List<GeneralTrait> Traits = new();
    private static float _weights;

    static Generals() => ResetCatalog();

    public static IReadOnlyList<GeneralTrait> All => Traits;

    public static void ResetCatalog(List<string> errors = null)
    {
        Traits.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "generals"))
            using (document)
                Merge(document.RootElement, errors);
        if (Traits.Count == 0) Traits.Add(new GeneralTrait("steady", "Steady", "", 1f, 1f, 1f));
        _weights = Traits.Sum(trait => MathF.Max(0f, trait.Weight));
    }

    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                errors?.Add($"common/generals: '{entry.Name}' is not a temperament.");
                continue;
            }
            string Text(string key) => entry.Value.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : "";
            float Number(string key, float fallback) => entry.Value.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetSingle() : fallback;
            var trait = new GeneralTrait(entry.Name, Text("name"), Text("description"),
                Math.Clamp(Number("attack", 1f), 0.5f, 1.5f), Math.Clamp(Number("defence", 1f), 0.5f, 1.5f),
                MathF.Max(0f, Number("weight", 1f)));
            Traits.RemoveAll(existing => existing.Id == trait.Id);
            Traits.Add(trait);
        }
    }

    public static GeneralTrait Trait(string id) =>
        Traits.Find(trait => string.Equals(trait.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Traits[0];

    private static readonly Dictionary<int, ArmyGroup> ById = new();
    private static Campaign _indexed;
    private static int _indexedCount = -1;

    private static ArmyGroup Group(Campaign campaign, int id)
    {
        if (!ReferenceEquals(_indexed, campaign) || _indexedCount != campaign.ArmyGroups.Count)
        {
            ById.Clear();
            foreach (var group in campaign.ArmyGroups) ById.TryAdd(group.Id, group);
            _indexed = campaign;
            _indexedCount = campaign.ArmyGroups.Count;
        }
        return ById.TryGetValue(id, out var found) && found is not null ? found : null;
    }

    /// <summary>The group a formation answers to - joining the smallest of its country's if it answers to none, and founding one if the country has none.</summary>
    public static ArmyGroup GroupOf(Campaign campaign, Army army)
    {
        if (campaign is null || army is null) return null;
        var group = army.ArmyGroupId >= 0 ? Group(campaign, army.ArmyGroupId) : null;
        if (group is not null && group.NationId == army.NationId) return group;
        ArmyGroup smallest = null;
        var fewest = int.MaxValue;
        foreach (var candidate in campaign.ArmyGroups)
        {
            if (candidate.NationId != army.NationId) continue;
            var members = 0;
            foreach (var other in campaign.Armies)
                if (other.ArmyGroupId == candidate.Id) members++;
            if (members < fewest || (members == fewest && candidate.Id < smallest.Id)) { smallest = candidate; fewest = members; }
        }
        if (smallest is null)
        {
            var nation = campaign.NationById(army.NationId);
            if (nation is null) return null;
            smallest = new ArmyGroup
            {
                Id = campaign.ArmyGroups.Count == 0 ? 1 : campaign.ArmyGroups.Max(existing => existing.Id) + 1,
                NationId = nation.Id,
                Name = $"{nation.Tag} Army 1"
            };
            campaign.ArmyGroups.Add(smallest);
        }
        army.ArmyGroupId = smallest.Id;
        return smallest;
    }

    private static float Roll(Campaign campaign, ArmyGroup group, int salt) =>
        Escalation.Roll(campaign.Seed, group.Id, group.NationId, group.CommandersLost, salt);

    /// <summary>The group's commander, appointed now if it has none.</summary>
    public static void EnsureCommander(Campaign campaign, ArmyGroup group)
    {
        if (campaign is null || group is null) return;
        if (!string.IsNullOrWhiteSpace(group.Commander) && group.Commander != Unassigned && group.CommanderTrait.Length > 0) return;
        Appoint(campaign, group);
    }

    /// <summary>A new commander for the group: a name from the country's pool, a skill and a temperament from the seed.</summary>
    public static void Appoint(Campaign campaign, ArmyGroup group)
    {
        var nation = campaign.NationById(group.NationId);
        group.Commander = Rulers.GenerateName(nation, 1000 + group.Id * 13 + group.CommandersLost * 7, false, campaign.Seed);
        // Triangular around the ordinary, with the odd genius and the odd fool.
        group.StaffSkill = M.Clamp(0.2f + 0.3f * (Roll(campaign, group, 0x6E1) + Roll(campaign, group, 0x6E2)), 0.2f, 0.8f);
        var pick = Roll(campaign, group, 0x6E3) * _weights;
        group.CommanderTrait = Traits[^1].Id;
        foreach (var trait in Traits)
        {
            pick -= MathF.Max(0f, trait.Weight);
            if (pick < 0f) { group.CommanderTrait = trait.Id; break; }
        }
        group.Victories = 0;
        group.Defeats = 0;
        group.CommanderSinceDay = campaign.ElapsedDays;
    }

    /// <summary>What a formation's commander does to its attack and to its defence, as factors.</summary>
    public static (float Attack, float Defence) Factors(Campaign campaign, Army army)
    {
        var group = GroupOf(campaign, army);
        if (group is null) return (1f, 1f);
        EnsureCommander(campaign, group);
        var skill = 1f + campaign.Defines.GeneralSkillWeight * (M.Clamp01(group.StaffSkill) - 0.5f);
        var trait = Trait(group.CommanderTrait);
        return (skill * trait.Attack, skill * trait.Defence);
    }

    /// <summary>
    /// After a battle: the winners' commanders learn, the losers' lose standing, a commander
    /// whose side broke may fall, and a battle that cost enough men goes into the chronicle
    /// under its town's name.
    /// </summary>
    public static void AfterBattle(Campaign campaign, LandBattle battle, int winnerId, bool broken)
    {
        if (campaign is null || battle is null || winnerId < 0) return;
        var defines = campaign.Defines;
        var attackerWon = winnerId == battle.AttackerId;
        var winners = new HashSet<int>(attackerWon ? battle.Attackers : battle.Defenders);
        var losers = new HashSet<int>(attackerWon ? battle.Defenders : battle.Attackers);
        var winningGroups = new List<ArmyGroup>();
        var losingGroups = new List<ArmyGroup>();
        foreach (var army in campaign.Armies)
        {
            var list = winners.Contains(army.Id) ? winningGroups : losers.Contains(army.Id) ? losingGroups : null;
            if (list is null) continue;
            var group = GroupOf(campaign, army);
            if (group is not null && !list.Contains(group)) list.Add(group);
        }
        foreach (var group in winningGroups)
        {
            EnsureCommander(campaign, group);
            group.Victories++;
            group.StaffSkill = MathF.Min(0.95f, group.StaffSkill + defines.GeneralVictorySkill);
        }
        var place = PlaceOf(campaign, battle.ProvinceId);
        foreach (var group in losingGroups)
        {
            EnsureCommander(campaign, group);
            group.Defeats++;
            group.StaffSkill = MathF.Max(0.15f, group.StaffSkill - defines.GeneralDefeatSkill);
            if (!broken || Escalation.Roll(campaign.Seed, battle.Id, group.Id, campaign.ElapsedDays, 0x6E4) >= defines.GeneralFallChance)
                continue;
            campaign.Chronicle.Record(campaign, "general_fallen", group.NationId, winnerId, group.Commander, place);
            CommandersFallen++;
            group.CommandersLost++;
            Appoint(campaign, group);
        }
        if (battle.AttackerLosses + battle.DefenderLosses < defines.GreatBattleLosses) return;
        var victor = winningGroups.OrderByDescending(group => group.Victories).ThenBy(group => group.Id).FirstOrDefault();
        var loserId = attackerWon ? battle.DefenderId : battle.AttackerId;
        campaign.Chronicle.Record(campaign, "great_battle", winnerId, loserId, victor?.Commander ?? "", place);
        GreatBattles++;
    }

    /// <summary>The town a battle is named for, or its region.</summary>
    public static string PlaceOf(Campaign campaign, int province)
    {
        var map = campaign?.ProvinceMap;
        if (map is null || province <= 0 || province >= map.ProvinceToState.Length) return "";
        if (CityCatalog.NearestTown(map, province) is { } town) return town.DisplayName;
        var index = map.ProvinceToState[province];
        return campaign.States.Find(state => state.MapIndex == index)?.DisplayName ?? "";
    }
}
