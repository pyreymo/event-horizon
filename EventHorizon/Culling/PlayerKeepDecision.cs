using System;
using EventHorizon.Settings;

namespace EventHorizon.Culling;

[Flags]
internal enum PlayerKeepRuleMask
{
    None = 0,
    TargetFocus = 1 << (int)PlayerKeepRuleId.TargetFocus,
    PartyAlliance = 1 << (int)PlayerKeepRuleId.PartyAlliance,
    Friends = 1 << (int)PlayerKeepRuleId.Friends,
    TargetingMe = 1 << (int)PlayerKeepRuleId.TargetingMe,
    RecentChat = 1 << (int)PlayerKeepRuleId.RecentChat,
    Recruiting = 1 << (int)PlayerKeepRuleId.Recruiting,
    Nearby = 1 << (int)PlayerKeepRuleId.Nearby,
    Race = 1 << (int)PlayerKeepRuleId.Race,
}

internal static class RaceSexFilter
{
    public const byte MinRace = 1;
    public const byte MaxRace = 8;
    public const byte MaleSex = 0;
    public const byte FemaleSex = 1;

    public static byte Pack(byte race, byte sex)
    {
        return (byte)(race | (sex << 4));
    }
}

internal readonly record struct PlayerKeepTieBreaker(bool InViewport, float DistanceSq)
{
    public static readonly PlayerKeepTieBreaker None = new(false, float.MaxValue);

    public PlayerKeepTieBreaker WithViewport(bool inViewport) => this with { InViewport = inViewport };

    public static int Compare(PlayerKeepTieBreaker left, PlayerKeepTieBreaker right)
    {
        if (left.InViewport != right.InViewport)
        {
            return left.InViewport ? -1 : 1;
        }

        return left.DistanceSq.CompareTo(right.DistanceSq);
    }
}

internal readonly record struct PlayerKeepDecision(
    PlayerKeepRuleId? RuleId,
    int Rank,
    PlayerKeepBudgetPolicy BudgetPolicy,
    PlayerKeepTieBreaker TieBreaker,
    PlayerKeepRuleMask MatchedRules
)
{
    public bool HasMatchingRule => RuleId.HasValue;

    public static readonly PlayerKeepDecision None = new(
        null,
        int.MaxValue,
        PlayerKeepBudgetPolicy.Exempt,
        PlayerKeepTieBreaker.None,
        PlayerKeepRuleMask.None
    );

    public static PlayerKeepDecision Keep(
        PlayerKeepRuleId ruleId,
        int rank,
        PlayerKeepBudgetPolicy budgetPolicy,
        PlayerKeepTieBreaker tieBreaker,
        PlayerKeepRuleMask matchedRules
    ) => new(ruleId, rank, budgetPolicy, tieBreaker, matchedRules);

    public PlayerKeepDecision WithViewport(bool inViewport) => this with { TieBreaker = TieBreaker.WithViewport(inViewport) };
}
