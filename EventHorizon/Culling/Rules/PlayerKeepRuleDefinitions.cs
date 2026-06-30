using System;
using System.Collections.Generic;
using EventHorizon.Settings;

namespace EventHorizon.Culling.Rules;

public enum PlayerKeepBudgetPolicy
{
    Exempt,
    Counted,
}

public enum PlayerKeepRuleId
{
    TargetFocus,
    PartyAlliance,
    Friends,
    TargetingMe,
    RecentChat,
    Recruiting,
    Nearby,
    Race,
}

[Flags]
internal enum PlayerKeepRuleMask
{
    None = 0,
    TargetFocus = 1 << 0,
    PartyAlliance = 1 << 1,
    Friends = 1 << 2,
    TargetingMe = 1 << 3,
    RecentChat = 1 << 4,
    Recruiting = 1 << 5,
    Nearby = 1 << 6,
    Race = 1 << 7,
}

internal static class PlayerKeepRuleBudgetDefaults
{
    public static Dictionary<PlayerKeepRuleId, PlayerKeepBudgetPolicy> Create() =>
        new()
        {
            [PlayerKeepRuleId.TargetFocus] = PlayerKeepBudgetPolicy.Exempt,
            [PlayerKeepRuleId.PartyAlliance] = PlayerKeepBudgetPolicy.Exempt,
            [PlayerKeepRuleId.Friends] = PlayerKeepBudgetPolicy.Exempt,
            [PlayerKeepRuleId.TargetingMe] = PlayerKeepBudgetPolicy.Counted,
            [PlayerKeepRuleId.RecentChat] = PlayerKeepBudgetPolicy.Counted,
            [PlayerKeepRuleId.Recruiting] = PlayerKeepBudgetPolicy.Counted,
            [PlayerKeepRuleId.Nearby] = PlayerKeepBudgetPolicy.Counted,
            [PlayerKeepRuleId.Race] = PlayerKeepBudgetPolicy.Counted,
        };

    public static PlayerKeepBudgetPolicy GetPolicy(Configuration configuration, PlayerKeepRuleId ruleId)
    {
        if (configuration.KeepRuleBudgetPolicies?.TryGetValue(ruleId, out var policy) == true)
        {
            return policy;
        }

        var defaultPolicies = Create();
        return defaultPolicies[ruleId];
    }

    public static void SetPolicy(Configuration configuration, PlayerKeepRuleId ruleId, PlayerKeepBudgetPolicy policy)
    {
        configuration.KeepRuleBudgetPolicies ??= [];
        configuration.KeepRuleBudgetPolicies[ruleId] = policy;
    }
}

internal static class PlayerKeepRuleOrder
{
    private static readonly PlayerKeepRuleId[] DefaultOrder =
    [
        PlayerKeepRuleId.TargetFocus,
        PlayerKeepRuleId.PartyAlliance,
        PlayerKeepRuleId.Friends,
        PlayerKeepRuleId.TargetingMe,
        PlayerKeepRuleId.Nearby,
        PlayerKeepRuleId.RecentChat,
        PlayerKeepRuleId.Race,
        PlayerKeepRuleId.Recruiting,
    ];

    public static List<PlayerKeepRuleId> CreateDefaultOrder() => [.. DefaultOrder];

    public static IReadOnlyList<PlayerKeepRuleId> GetEffectiveOrder(Configuration configuration)
    {
        var effectiveOrder = new List<PlayerKeepRuleId>();
        var seenRules = new HashSet<PlayerKeepRuleId>();
        var configuredOrder = configuration.KeepRuleOrder;

        if (configuredOrder != null)
        {
            foreach (var rule in configuredOrder)
            {
                if (Array.IndexOf(DefaultOrder, rule) < 0 || !seenRules.Add(rule))
                {
                    continue;
                }

                effectiveOrder.Add(rule);
            }
        }

        foreach (var rule in DefaultOrder)
        {
            if (seenRules.Add(rule))
            {
                effectiveOrder.Add(rule);
            }
        }

        return effectiveOrder;
    }

    public static int GetRank(Configuration configuration, PlayerKeepRuleId ruleId)
    {
        var effectiveOrder = GetEffectiveOrder(configuration);
        for (var index = 0; index < effectiveOrder.Count; index++)
        {
            if (effectiveOrder[index] == ruleId)
            {
                return index;
            }
        }

        return effectiveOrder.Count;
    }

    public static void Reset(Configuration configuration)
    {
        configuration.KeepRuleOrder = CreateDefaultOrder();
    }
}
