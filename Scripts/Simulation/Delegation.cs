using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// Handing a portfolio to your own civil service.
///
/// A country already has a staff that runs its budget and its trade - two hundred and sixty
/// of them do it every month, competently enough to develop, arm and feed themselves without
/// anybody watching. The player's country is the only one in the world that does not have
/// one, and the result is that a player who wants to fight a war has to keep coming back to
/// a tax slider.
///
/// So the same staff can be put to work for the player, one portfolio at a time. This is the
/// economic half of what the army already has: you draw a front and give divisions a duty
/// rather than walking each of them to a province, and here you hand over the budget or the
/// trade desk rather than setting each line yourself.
///
/// Three rules it keeps, and they are what make it a delegation rather than an autopilot:
///
///   - it is PER PORTFOLIO. Delegate the trade desk and keep the budget, or the reverse.
///     An all-or-nothing switch is a switch nobody dares touch;
///   - it is REVERSIBLE and leaves the lever where it left it. Take a portfolio back and
///     the numbers are whatever the staff last set, not whatever you had before - because
///     that is what taking a department back actually gets you;
///   - it WRITES DOWN WHAT IT DID. Every instruction it gives is an ordinary instruction,
///     visible on the trade screen beside your own, with its reason attached. A delegate
///     you cannot audit is a delegate you cannot trust.
/// </summary>
public static class Delegation
{
    /// <summary>Tax and spending: the levers the budget screen shows.</summary>
    public const string Budget = "budget";

    /// <summary>The trade desk: what to buy first, what to keep back, what to pay.</summary>
    public const string Trade = "trade";

    /// <summary>
    /// The admiralty: where the fleets go and what they do, when they come home to refit, and
    /// what the yards lay down - everything the AI's own navies are run by.
    /// </summary>
    public const string Navy = "navy";

    /// <summary>
    /// The general staff: keeping the army at the size the budget pays for - raising a
    /// formation to replace one lost, standing one down when a peace leaves too many.
    ///
    /// The one portfolio a player's country has always had delegated, without being told: the
    /// same rebuild every other country runs ran for the player too, so an army the player had
    /// raised for a war they were planning was quietly stood down, one division a month, while
    /// they planned it. It stays delegated by default - a player who never opens this keeps the
    /// army they had - and it can now be taken back.
    /// </summary>
    public const string Army = "army";

    /// <summary>
    /// The general staff in the field: the formations at liberty. At peace it lays them out -
    /// the capital, the largest regions, the borders worth fearing (ArmyDisposition,
    /// ThreatPosture); at war it fills the holes in the line from the free reserve
    /// (FrontManager) and moves a starving formation to ground that can feed it. Fronts the
    /// player drew and orders the player gave are never touched. Off unless handed over:
    /// a player's army does not walk anywhere on its own.
    /// </summary>
    public const string Command = "command";

    /// <summary>The foreign ministry: the weekly round every other government makes of its
    /// neighbours - drawing closer to friends, cooler to rivals, alliances and guarantees asked
    /// and given. It never declares a war and never accepts a peace; those stay the player's.</summary>
    public const string Diplomacy = "diplomacy";

    /// <summary>The cabinet: the decisions the computer takes for its own countries, when they
    /// are worth taking.</summary>
    public const string Cabinet = "cabinet";

    public const string Construction = "construction";
    public const string Research = "research";
    public const string Personnel = "personnel";

    /// <summary>The colonial office: the subject management every other overlord runs —
    /// granting land to a fitting existing subject rather than founding micro-vassals, and
    /// releasing an unruly, culturally foreign periphery as an autonomous satellite when the
    /// empire is overstretched. It acts on the same monthly review and the same thresholds
    /// the computer's overlords use. Founding a subject, annexing one or granting land by
    /// hand stays the player's, and the office only ever acts on the same monthly day it
    /// would for anybody else.</summary>
    public const string Subjects = "subjects";

    private const string ManualConstruction = "construction_manual";
    private const string ManualResearch = "research_manual";

    public static readonly IReadOnlyList<string> Portfolios = new[] { Budget, Trade, Army, Command, Navy, Diplomacy, Cabinet, Construction, Research, Personnel, Subjects };

    /// <summary>Whether this country has handed that portfolio over.</summary>
    public static bool Holds(Nation nation, string portfolio) =>
        nation is not null && (portfolio switch
        {
            Army => !nation.ArmyByHand,
            // Preserve old saves: research was automatic and the delegated treasury also built.
            Research => !nation.Delegated.Contains(ManualResearch),
            Construction => nation.Delegated.Contains(Construction) ||
                nation.Delegated.Contains(Budget) && !nation.Delegated.Contains(ManualConstruction),
            _ => nation.Delegated.Contains(portfolio)
        });

    public static void Set(Nation nation, string portfolio, bool delegated)
    {
        if (nation is null || !((IList<string>)Portfolios).Contains(portfolio)) return;
        if (portfolio == Army) { nation.ArmyByHand = !delegated; return; }
        if (portfolio is Research or Construction)
        {
            var marker = portfolio == Research ? ManualResearch : ManualConstruction;
            if (delegated) nation.Delegated.Remove(marker); else nation.Delegated.Add(marker);
        }
        if (delegated) nation.Delegated.Add(portfolio);
        else nation.Delegated.Remove(portfolio);
    }

    /// <summary>
    /// Whether the AI runs this government's cabinet: a country that is not a human's seat at
    /// all, or one whose cabinet the player handed over. Read by the promise layer, so a
    /// delegated cabinet pledges in public on exactly the terms an AI one does - the cabinet
    /// already takes decisions through the same door (Delegation.Tick), and a government that
    /// spends the budget but never speaks would be half a delegation.
    /// </summary>
    public static bool RunByAi(Nation nation) =>
        nation is not null && (!(nation.IsHuman || nation.IsPlayer) || Holds(nation, Cabinet));

    /// <summary>
    /// A month of whatever the player has handed over.
    ///
    /// Only the player's country passes through here. Everybody else is governed by
    /// <see cref="AiStatecraft"/> in the ordinary way, and the budget half of this IS that
    /// code - the same reasoning, reached through a different door, so a delegated budget is
    /// exactly as good as the one every other country gets and gets better when that does.
    /// </summary>
    public static void Tick(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null || nation.Delegated.Count == 0) return;
        if (Holds(nation, Budget)) AiStatecraft.GovernTheBudget(campaign, nation);
        else if (Holds(nation, Construction)) AiStatecraft.PlanConstruction(campaign, nation);
        if (Holds(nation, Trade)) RunTheTradeDesk(campaign, nation);
        if (Holds(nation, Cabinet)) AiStatecraft.RunTheCabinet(campaign, nation);
    }

    /// <summary>The instruction key a delegated desk writes, so its work can be told apart.</summary>
    public const string DeskReason = "desk";

    /// <summary>
    /// What a trade desk does with a month.
    ///
    /// Deliberately the four moves a real one makes, and no more. It is not trying to be
    /// clever - it is trying to be the thing a player would have done if they had remembered
    /// to open the screen:
    ///
    ///   - a shortage of something people need gets bought first and without a ceiling. That
    ///     is what import priority and the import ceiling are for, and leaving them at their
    ///     resting values during a famine is the mistake this exists to stop;
    ///   - a surplus that is not needed at home is sold, so the export reserve comes off;
    ///   - something we are short of ourselves is NOT sold abroad, so the reserve goes on;
    ///   - and when a good stops being a problem, the instruction is withdrawn. A desk that
    ///     only ever adds rules is a desk that ends the decade with forty of them.
    /// </summary>
    private static void RunTheTradeDesk(Campaign campaign, Nation nation)
    {
        var atWar = campaign.Wars.Any(w => !w.Ended && w.Involves(nation.Id));
        var fiscalStress = nation.DebtCondition >= DebtCondition.Stressed || nation.Treasury < 0.5f;

        foreach (var (commodity, quote) in nation.Market)
        {
            var definition = CommodityCatalog.Get(commodity);
            if (definition is null) continue;

            var essential = definition.EssentialWeight >= 0.5f;
            var military = CommodityCatalog.IdOf(commodity) is "ammunition" or "fuel" or "oil" or "iron_ore" or "rare_earths";
            var short_ = quote.IsShortage;
            var covered = quote.Demand <= 0.001f ? 1f : quote.Production / quote.Demand;

            if (short_)
            {
                // Buy it first, and do not haggle while people are going without.
                var priority = (essential || (atWar && military)) ? 2.5f : 1.6f;
                // If treasury is in severe distress, don't overspend on non-essentials
                if (fiscalStress && !essential && !military) priority = 1.0f;

                Instruct(nation, commodity, TradeDesk.ImportPriority, priority);
                Instruct(nation, commodity, TradeDesk.ImportCeiling, 1f);
                // And stop selling what we do not have.
                Instruct(nation, commodity, TradeDesk.ExportReserve,
                    essential || (atWar && military) ? 1f : M.Clamp(1f - covered, 0f, 1f));
                continue;
            }

            // In wartime, reserve military and fuel stocks even if slightly above demand
            if (atWar && military && covered < 1.6f)
            {
                Instruct(nation, commodity, TradeDesk.ExportReserve, 0.40f);
                Instruct(nation, commodity, TradeDesk.ImportPriority, 1.8f);
                Instruct(nation, commodity, TradeDesk.ImportCeiling, 1f);
                continue;
            }

            if (covered > 1.25f)
            {
                // A real surplus. Sell it, and keep nothing back beyond a prudent margin on
                // the things people cannot do without.
                Instruct(nation, commodity, TradeDesk.ExportReserve, essential ? 0.15f : 0f);
                Withdraw(nation, commodity, TradeDesk.ImportPriority);
                Withdraw(nation, commodity, TradeDesk.ImportCeiling);
                continue;
            }

            // Neither short nor spare: no opinion, and say so by having none.
            Withdraw(nation, commodity, TradeDesk.ImportPriority);
            Withdraw(nation, commodity, TradeDesk.ImportCeiling);
            Withdraw(nation, commodity, TradeDesk.ExportReserve);
        }
    }

    private static void Instruct(Nation nation, Commodity commodity, string setting, float value)
    {
        var definition = TradeDesk.Of(setting);
        if (definition is null) return;
        TradeDesk.Set(nation, TradeDesk.GoodKey(commodity, setting), setting,
            M.Clamp(value, definition.Minimum, definition.Maximum));
    }

    private static void Withdraw(Nation nation, Commodity commodity, string setting) =>
        nation.TradeSettings.Remove(TradeDesk.GoodKey(commodity, setting));
}

public sealed partial class Nation
{
    /// <summary>
    /// Whether the player has taken the size of the army into their own hands, so the general
    /// staff neither raises nor stands down formations for it. Stored this way round so that a
    /// save from before the choice existed keeps the army that was being kept for it.
    /// </summary>
    public bool ArmyByHand;
}

/// <summary>
/// Hand a portfolio to the civil service, or take it back.
/// </summary>
public sealed class SetDelegationCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public string Portfolio { get; init; } = "";
    public bool Delegated { get; init; }

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign?.NationById(IssuerId);
        if (nation is null) return;
        Delegation.Set(nation, Portfolio, Delegated);
        DiagnosticLogs.Write("orders",
            $"DELEGATE nation={nation.Tag} portfolio={Portfolio} on={Delegated}");
    }
}
