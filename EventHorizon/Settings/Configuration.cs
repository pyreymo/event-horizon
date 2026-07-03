using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using EventHorizon.Culling.Rules;
using Newtonsoft.Json;

namespace EventHorizon.Settings;

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
    public bool ShowFrameRateInDtrBar { get; set; } = true;
    public bool EnableDtrBackground { get; set; } = false;
    public float DtrBackgroundHorizontalPadding { get; set; } = 24f;
    public float DtrBackgroundPaddingTop { get; set; } = 10f;
    public float DtrBackgroundPaddingBottom { get; set; } = 1f;
    public byte DtrBackgroundAlpha { get; set; } = 128;
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
    public List<PlayerKeepRuleId> KeepRuleOrder { get; set; } = PlayerKeepRuleOrder.CreateDefaultOrder();

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<PlayerKeepRuleId, PlayerKeepBudgetPolicy> KeepRuleBudgetPolicies { get; set; } =
        PlayerKeepRuleBudgetDefaults.Create();

    public HashSet<byte> KeptRaceSex { get; set; } = [];

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
