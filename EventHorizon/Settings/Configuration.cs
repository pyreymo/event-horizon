using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Newtonsoft.Json;

namespace EventHorizon.Settings;

[Serializable]
internal class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool HideAllOtherPlayers { get; set; } = true;
    public bool EnableTemporaryShowAllPlayersShortcut { get; set; } = true;

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<int> TemporarilyShowAllPlayersKeys { get; set; } = [0x11, 0x12];
    public bool DisableInDuty { get; set; } = true;
    public bool DisableCullingBelowPlayerCount { get; set; } = true;
    public int DisableCullingPlayerCountThreshold { get; set; } = 25;
    public bool LimitVisiblePlayerCount { get; set; }
    public int VisiblePlayerCountLimit { get; set; } = 30;
    public bool HideOtherPlayerCompanions { get; set; } = true;
    public bool HideOtherPlayerOrnaments { get; set; } = false;
    public bool HideOtherPlayerBattlePets { get; set; } = false;
    public bool HideUnattachedEventNpcs { get; set; } = false;
    public bool ShowDtrBar { get; set; } = true;
    public bool ShowFrameRateInDtrBar { get; set; } = true;
    public bool EnableDtrBackground { get; set; } = false;
    public bool EnableHiddenPlayerGroundMarker { get; set; } = true;
    public bool UseHiddenPlayerMarkerDot { get; set; } = false;
    public byte HiddenPlayerMarkerDotColorRed { get; set; } = 124;
    public byte HiddenPlayerMarkerDotColorGreen { get; set; } = 89;
    public byte HiddenPlayerMarkerDotColorBlue { get; set; } = 158;
    public byte HiddenPlayerMarkerDotColorAlpha { get; set; } = 180;
    public float HiddenPlayerMarkerDotRadius { get; set; } = 5f;
    public bool EnableTargetingMeMarker { get; set; } = true;
    public bool EnableTargetingMeNamePlateMarker { get; set; } = true;
    public bool EnableTargetingMeDotMarker { get; set; } = false;
    public bool EnableTargetingMeVfxMarker { get; set; } = false;
    public bool EnableTargetingMeMarkerCurrentTargetTest { get; set; } = false;
    public bool DisableTargetingMeMarkerVfxInDuty { get; set; } = true;
    public float TargetingMeMarkerOffsetX { get; set; } = 0;
    public float TargetingMeMarkerOffsetY { get; set; } = -45;
    public float TargetingMeMarkerScale { get; set; } = 1.33f;
    public byte TargetingMeMarkerOpacity { get; set; } = 255;
    public byte TargetingMeMarkerGlowOpacity { get; set; } = 255;
    public bool UseCustomTargetingMeMarkerColor { get; set; } = false;
    public byte TargetingMeMarkerColorRed { get; set; } = 255;
    public byte TargetingMeMarkerColorGreen { get; set; } = 120;
    public byte TargetingMeMarkerColorBlue { get; set; } = 40;
    public bool UseCustomTargetingMeDotColor { get; set; } = false;
    public byte TargetingMeDotColorRed { get; set; } = 255;
    public byte TargetingMeDotColorGreen { get; set; } = 0;
    public byte TargetingMeDotColorBlue { get; set; } = 0;
    public byte TargetingMeDotColorAlpha { get; set; } = 255;
    public float TargetingMeDotRadius { get; set; } = 5f;
    public float DtrBackgroundHorizontalPadding { get; set; } = 24f;
    public float DtrBackgroundPaddingTop { get; set; } = 10f;
    public float DtrBackgroundPaddingBottom { get; set; } = 4f;
    public byte DtrBackgroundAlpha { get; set; } = 128;
    public bool KeepFriends { get; set; } = true;
    public bool KeepPartyAndAllianceMembers { get; set; } = true;
    public bool KeepRecruitingPlayers { get; set; } = true;
    public bool KeepRecentChatPlayers { get; set; } = true;
    public bool KeepNearbyPlayers { get; set; }
    public float KeepNearbyPlayersRange { get; set; } = 5f;
    public bool KeepTargetAndFocusPlayers { get; set; } = true;
    public bool KeepPlayersTargetingMe { get; set; } = true;
    public bool KeepSelectedRaces { get; set; }
    public bool HideBgParts { get; set; } = false;
    public bool HideTerrain { get; set; } = false;
    public bool HideWater { get; set; } = false;
    public bool HideGrass { get; set; } = false;
    public bool HideAll3DScene { get; set; } = false;

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<PlayerKeepRuleId> KeepRuleOrder { get; set; } = PlayerKeepRuleOrder.CreateDefaultOrder();

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<PlayerKeepRuleId, PlayerKeepBudgetPolicy> KeepRuleBudgetPolicies { get; set; } = PlayerKeepRulePolicies.Create();

    public HashSet<byte> KeptRaceSex { get; set; } = [];

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
