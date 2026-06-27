using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Newtonsoft.Json;

namespace EventHorizon;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool HideAllOtherPlayers { get; set; } = true;
    public bool DisableInDuty { get; set; } = true;
    public bool DisableCullingBelowPlayerCount { get; set; } = true;
    public int DisableCullingPlayerCountThreshold { get; set; } = 25;
    public bool LimitVisiblePlayerCount { get; set; }
    public int VisiblePlayerCountLimit { get; set; } = 30;
    public bool HideOtherPlayerCompanions { get; set; } = true;
    public bool HideOtherPlayerOrnaments { get; set; } = false;
    public bool ShowDtrBar { get; set; } = true;
    public bool EnableFadeTransitions { get; set; } = true;
    public bool KeepFriends { get; set; } = true;
    public bool KeepPartyAndAllianceMembers { get; set; } = true;
    public bool KeepRecruitingPlayers { get; set; } = true;
    public bool KeepRecentChatPlayers { get; set; } = true;
    public bool KeepNearbyPlayers { get; set; }
    public float KeepNearbyPlayersRange { get; set; } = 5f;
    public bool KeepTargetAndFocusPlayers { get; set; } = true;
    public bool KeepPlayersTargetingMe { get; set; } = true;
    public bool KeepSelectedRaces { get; set; }

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<CompetitiveKeepRule> CompetitiveKeepRuleOrder { get; set; } = CompetitiveKeepOrder.CreateDefaultOrder();
    public HashSet<byte> KeptRaceSex { get; set; } = [];

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}

internal static class CompetitiveKeepOrder
{
    private static readonly CompetitiveKeepRule[] DefaultOrder =
    [
        CompetitiveKeepRule.TargetingMe,
        CompetitiveKeepRule.Nearby,
        CompetitiveKeepRule.RecentChat,
        CompetitiveKeepRule.Race,
        CompetitiveKeepRule.Recruiting,
    ];

    public static List<CompetitiveKeepRule> CreateDefaultOrder() => [.. DefaultOrder];

    public static IReadOnlyList<CompetitiveKeepRule> GetEffectiveOrder(Configuration configuration)
    {
        var effectiveOrder = new List<CompetitiveKeepRule>();
        var seenRules = new HashSet<CompetitiveKeepRule>();
        var configuredOrder = configuration.CompetitiveKeepRuleOrder;

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

    public static int GetRank(Configuration configuration, CompetitiveKeepRule rule)
    {
        var effectiveOrder = GetEffectiveOrder(configuration);
        for (var index = 0; index < effectiveOrder.Count; index++)
        {
            if (effectiveOrder[index] == rule)
            {
                return index;
            }
        }

        return effectiveOrder.Count;
    }

    public static void Reset(Configuration configuration)
    {
        configuration.CompetitiveKeepRuleOrder = CreateDefaultOrder();
    }
}

public enum CompetitiveKeepRule
{
    TargetingMe,
    RecentChat,
    Recruiting,
    Nearby,
    Race,
}
