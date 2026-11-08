using System;

namespace DealOrForce.Simulation;

/// <summary>A detached, read-only quote of the actual withdrawal rule.</summary>
public readonly record struct BattleWithdrawalQuote(bool Available, bool Attacking, int Destination,
    float Leaving, float Remaining, string Refusal)
{
    internal static BattleWithdrawalQuote Refuse(string reason) => new(false, false, -1, 0, 0, reason);
}

public static class BattleWithdrawalPreview
{
    public static BattleWithdrawalQuote Read(Campaign c, int issuer, int armyId, float share = 1,
        int targetProvinceId = -1)
    {
        var army = c?.Armies.Find(a => a.Id == armyId);
        if (army is null || army.NationId != issuer)
            return BattleWithdrawalQuote.Refuse("Order not authorised");
        if (c.Defines.FeatureCommands.TryGetValue(nameof(WithdrawArmyFromBattleCommand), out var feature) &&
            !string.IsNullOrWhiteSpace(feature) && !c.FeatureEnabled(feature))
            return BattleWithdrawalQuote.Refuse("This command is disabled in this campaign.");
        if (!float.IsFinite(share) || share <= 0 || share > 1)
            return BattleWithdrawalQuote.Refuse("Invalid withdrawal share");
        return Warfare.AssessWithdrawal(c, army, share < .999f ? share : 1, targetProvinceId);
    }
}

public static partial class Warfare
{
    internal static BattleWithdrawalQuote AssessWithdrawal(Campaign c, Army army, float share, int target)
    {
        var partial = share < 1;
        var battle = c?.Battles.Find(b => b.Id == army?.BattleId && !b.Ended);
        if (battle is null || army is null || !float.IsFinite(army.Divisions) ||
            army.Divisions < (partial ? 2 : .1f))
            return BattleWithdrawalQuote.Refuse(partial ? "Too small to split" : "Not in battle");
        if (!float.IsFinite(share) || share <= 0 || share > 1)
            return BattleWithdrawalQuote.Refuse("Invalid withdrawal share");
        var attacking = battle.Attackers.Contains(army.Id);
        if (!attacking && !battle.Defenders.Contains(army.Id))
            return BattleWithdrawalQuote.Refuse("Not in battle");
        var war = c.Wars.Find(w => w.Id == battle.WarId && !w.Ended);
        if (war is null) return BattleWithdrawalQuote.Refuse("War ended");
        var leaving = partial ? M.Clamp(MathF.Floor(army.Divisions * share + .0001f), 1, army.Divisions - 1) : army.Divisions;
        var remaining = army.Divisions - leaving;
        if (partial && (leaving < 1 || remaining < 1)) return BattleWithdrawalQuote.Refuse("Too small to split");
        var retreat = attacking ? -1 : WithdrawalProvince(c, war, army, target);
        if (!attacking && retreat <= 0) return BattleWithdrawalQuote.Refuse("No friendly line of retreat");
        return new(true, attacking, retreat, leaving, remaining, "");
    }
}

