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

    /// <summary>
    /// Relieving a commander (Generals.Relieve): the political credit it costs, how long a new one
    /// must hold the group before they can be relieved in turn, and how many officers are put
    /// forward for the post.
    /// </summary>
    public float CommanderReliefPoliticalCost { get; set; } = 10f;
    public int CommanderReliefCooldownDays { get; set; } = 90;
    public int CommanderCandidates { get; set; } = 3;
    /// <summary>
    /// What relieving a victorious commander in wartime costs in war support: this much for each
    /// battle won beyond those lost, up to the maximum - Truman and MacArthur, 1951.
    /// </summary>
    public float CommanderReliefWarSupportPerVictory { get; set; } = 0.01f;
    public float CommanderReliefWarSupportMax { get; set; } = 0.06f;
    /// <summary>
    /// Whether governments the computer runs relieve the commander who keeps losing: defeats at
    /// least this many, and at least twice the victories plus one.
    /// </summary>
    public bool AiRelievesFailedCommanders { get; set; } = false;
    public int AiCommanderReliefDefeats { get; set; } = 3;
}

/// <summary>An officer who could take a group: name, skill and temperament.</summary>
public sealed record CommanderCandidate(string Name, float Skill, string Trait);

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

    public static ArmyGroup Group(Campaign campaign, int id)
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
        Escalation.Roll(campaign.Seed, group.Id, group.NationId, Generation(group, 0), salt);

    /// <summary>
    /// Which generation of commanders the draws are for: the fallen count, and - once a government
    /// has relieved one - the relieved too, so the same officers are not put forward again. The
    /// candidate index separates the officers put forward at one time.
    /// </summary>
    private static int Generation(ArmyGroup group, int candidate) =>
        group.CommandersRelieved == 0 && candidate == 0
            ? group.CommandersLost
            : group.CommandersLost + 1000 * (group.CommandersRelieved * 8 + candidate + 1);

    /// <summary>The group's commander, appointed now if it has none.</summary>
    public static void EnsureCommander(Campaign campaign, ArmyGroup group)
    {
        if (campaign is null || group is null) return;
        if (!string.IsNullOrWhiteSpace(group.Commander) && group.Commander != Unassigned && group.CommanderTrait.Length > 0) return;
        Appoint(campaign, group);
    }

    /// <summary>A new commander for the group: a name from the country's pool, a skill and a temperament from the seed.</summary>
    public static void Appoint(Campaign campaign, ArmyGroup group) => Install(campaign, group, Draw(campaign, group, 0));

    /// <summary>An officer drawn from the seed for this group's generation; candidate 0 is who would be appointed.</summary>
    private static CommanderCandidate Draw(Campaign campaign, ArmyGroup group, int candidate)
    {
        var nation = campaign.NationById(group.NationId);
        var generation = Generation(group, candidate);
        float Draw(int salt) => Escalation.Roll(campaign.Seed, group.Id, group.NationId, generation, salt);
        var name = Rulers.GenerateName(nation, 1000 + group.Id * 13 + generation * 7, false, campaign.Seed);
        // Triangular around the ordinary, with the odd genius and the odd fool.
        var skill = M.Clamp(0.2f + 0.3f * (Draw(0x6E1) + Draw(0x6E2)), 0.2f, 0.8f);
        var pick = Draw(0x6E3) * _weights;
        var temperament = Traits[^1].Id;
        foreach (var trait in Traits)
        {
            pick -= MathF.Max(0f, trait.Weight);
            if (pick < 0f) { temperament = trait.Id; break; }
        }
        return new CommanderCandidate(name, skill, temperament);
    }

    private static void Install(Campaign campaign, ArmyGroup group, CommanderCandidate officer)
    {
        group.Commander = officer.Name;
        group.StaffSkill = officer.Skill;
        group.CommanderTrait = officer.Trait;
        group.Victories = 0;
        group.Defeats = 0;
        group.CommanderSinceDay = campaign.ElapsedDays;
    }

    /// <summary>The officers put forward to take the group if its commander is relieved now.</summary>
    public static CommanderCandidate[] Candidates(Campaign campaign, ArmyGroup group)
    {
        if (campaign is null || group is null || Traits.Count == 0) return Array.Empty<CommanderCandidate>();
        var count = Math.Clamp(campaign.Defines.CommanderCandidates, 1, 8);
        var result = new CommanderCandidate[count];
        for (var i = 0; i < count; i++) result[i] = Draw(campaign, group, i + 1);
        return result;
    }

    /// <summary>What relieving this group's commander would cost the country in war support, now.</summary>
    public static float ReliefWarSupportCost(Campaign campaign, ArmyGroup group)
    {
        if (campaign is null || group is null) return 0f;
        var atWar = false;
        foreach (var war in campaign.Wars)
            if (!war.Ended && war.Involves(group.NationId)) { atWar = true; break; }
        if (!atWar) return 0f;
        var d = campaign.Defines;
        return M.Clamp(Math.Max(0, group.Victories - group.Defeats) * d.CommanderReliefWarSupportPerVictory,
            0f, MathF.Max(0f, d.CommanderReliefWarSupportMax));
    }

    /// <summary>Whether the country may relieve this group's commander now, and why not.</summary>
    public static bool MayRelieve(Campaign campaign, Nation nation, ArmyGroup group, out string refusal)
    {
        refusal = "";
        if (campaign is null || nation is null || group is null || group.NationId != nation.Id)
        { refusal = Loc.Get("This command is not yours to change."); return false; }
        if (nation.Population <= 0 || campaign.Fallen.Contains(nation.Id)) { refusal = Loc.Get("A country that no longer exists appoints nobody."); return false; }
        var d = campaign.Defines;
        if (group.CommanderSinceDay >= 0 && campaign.ElapsedDays - group.CommanderSinceDay < d.CommanderReliefCooldownDays)
        {
            refusal = Loc.Format("The commander took the group {0} days ago; relieving them is possible after {1}.",
                campaign.ElapsedDays - group.CommanderSinceDay, d.CommanderReliefCooldownDays);
            return false;
        }
        if (nation.PoliticalPoints < d.CommanderReliefPoliticalCost)
        {
            refusal = Loc.Format("Relieving a commander costs {0} political credit; there is {1}.",
                Fmt.Number(d.CommanderReliefPoliticalCost), Fmt.Number(nation.PoliticalPoints));
            return false;
        }
        return true;
    }

    /// <summary>
    /// Relieves the group's commander and gives the group to one of the officers put forward: the
    /// credit is spent, a victorious commander's removal in wartime costs war support, and the
    /// world hears of it.
    /// </summary>
    public static bool Relieve(Campaign campaign, Nation nation, ArmyGroup group, int candidate, out string refusal)
    {
        if (!MayRelieve(campaign, nation, group, out refusal)) return false;
        var candidates = Candidates(campaign, group);
        if (candidate < 0 || candidate >= candidates.Length) { refusal = Loc.Get("No such officer was put forward."); return false; }
        var officer = candidates[candidate];
        var old = group.Commander;
        nation.PoliticalPoints -= campaign.Defines.CommanderReliefPoliticalCost;
        nation.WarSupport = M.Clamp01(nation.WarSupport - ReliefWarSupportCost(campaign, group));
        group.CommandersRelieved++;
        Install(campaign, group, officer);
        campaign.Chronicle.Record(campaign, "general_relieved", nation.Id, -1, old, officer.Name);
        return true;
    }

    /// <summary>
    /// Governments the computer runs relieve the commander who keeps losing, by the same rule and
    /// at the same price as the player, and give the group to the ablest officer put forward.
    /// </summary>
    public static void AiMonth(Campaign campaign)
    {
        var d = campaign.Defines;
        if (!d.AiRelievesFailedCommanders || campaign.ElapsedDays % 28 != 19) return;
        foreach (var group in campaign.ArmyGroups.ToArray())
        {
            if (group.Defeats < d.AiCommanderReliefDefeats || group.Defeats < 2 * group.Victories + 1) continue;
            var nation = campaign.NationById(group.NationId);
            if (nation is null || nation.IsHuman || !MayRelieve(campaign, nation, group, out _)) continue;
            var candidates = Candidates(campaign, group);
            var best = 0;
            for (var i = 1; i < candidates.Length; i++)
                if (candidates[i].Skill > candidates[best].Skill) best = i;
            if (candidates.Length > 0 && candidates[best].Skill > group.StaffSkill) Relieve(campaign, nation, group, best, out _);
        }
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

/// <summary>Relieve an army group's commander and give the group to one of the officers put forward (Generals.Relieve).</summary>
public sealed class RelieveCommanderCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int GroupId { get; init; }
    public int Candidate { get; init; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        var group = Generals.Group(campaign, GroupId);
        if (!Generals.Relieve(campaign, nation, group, Candidate, out var refusal))
            DiagnosticLogs.Write("orders", $"COMMANDER_RELIEF_REFUSED nation={IssuerId} group={GroupId} reason={refusal}");
    }
}
