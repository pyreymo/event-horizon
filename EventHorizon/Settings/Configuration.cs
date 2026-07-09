using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using EventHorizon.Culling.Rules;
using Newtonsoft.Json;

namespace EventHorizon.Settings;

[Serializable]
internal class Configuration : IPluginConfiguration
{
    public const float DefaultTargetingMeMarkerOffsetX = 0;
    public const float DefaultTargetingMeMarkerOffsetY = 0;
    public const float DefaultTargetingMeMarkerScale = 1f;
    public const byte DefaultTargetingMeMarkerOpacity = 255;
    public const byte DefaultTargetingMeMarkerGlowOpacity = 255;
    public const byte DefaultTargetingMeMarkerColorRed = 255;
    public const byte DefaultTargetingMeMarkerColorGreen = 120;
    public const byte DefaultTargetingMeMarkerColorBlue = 40;

    public int Version { get; set; } = 0;

    public bool HideAllOtherPlayers { get; set; } = true;
    public bool DisableInDuty { get; set; } = true;
    public bool DisableCullingBelowPlayerCount { get; set; } = true;
    public int DisableCullingPlayerCountThreshold { get; set; } = 25;
    public bool LimitVisiblePlayerCount { get; set; }
    public int VisiblePlayerCountLimit { get; set; } = 30;
    public bool HideOtherPlayerCompanions { get; set; } = true;
    public bool HideOtherPlayerOrnaments { get; set; } = false;
    public bool HideOtherPlayerBattlePets { get; set; } = true;
    public bool HideUnattachedEventNpcs { get; set; } = true;
    public bool ShowDtrBar { get; set; } = true;
    public bool ShowFrameRateInDtrBar { get; set; } = true;
    public bool EnableDtrBackground { get; set; } = false;
    public bool EnableHiddenPlayerGroundMarker { get; set; } = false;
    public bool HideBgPartGraphicsObjects { get; set; } = false;
    public bool HideTerrainGraphicsObjects { get; set; } = false;
    public bool EnableTargetingMeMarker { get; set; } = false;

    [JsonProperty(nameof(EnableTargetingMeNamePlateMarker))]
    private bool? enableTargetingMeNamePlateMarker;

    [JsonProperty(nameof(EnableTargetingMeVfxMarker))]
    private bool? enableTargetingMeVfxMarker;

    public bool EnableTargetingMeMarkerCurrentTargetTest { get; set; } = false;
    public bool DisableTargetingMeMarkerVfxInDuty { get; set; } = true;
    public TargetingMeMarkerVisualStyle TargetingMeMarkerVisualStyle { get; set; } = TargetingMeMarkerVisualStyle.GazeMarker;
    public float TargetingMeMarkerOffsetX { get; set; } = DefaultTargetingMeMarkerOffsetX;
    public float TargetingMeMarkerOffsetY { get; set; } = DefaultTargetingMeMarkerOffsetY;
    public float TargetingMeMarkerScale { get; set; } = DefaultTargetingMeMarkerScale;
    public byte TargetingMeMarkerOpacity { get; set; } = DefaultTargetingMeMarkerOpacity;
    public byte TargetingMeMarkerGlowOpacity { get; set; } = DefaultTargetingMeMarkerGlowOpacity;
    public bool UseCustomTargetingMeMarkerColor { get; set; } = false;
    public byte TargetingMeMarkerColorRed { get; set; } = DefaultTargetingMeMarkerColorRed;
    public byte TargetingMeMarkerColorGreen { get; set; } = DefaultTargetingMeMarkerColorGreen;
    public byte TargetingMeMarkerColorBlue { get; set; } = DefaultTargetingMeMarkerColorBlue;
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

    [JsonIgnore]
    public bool EnableTargetingMeNamePlateMarker
    {
        get => enableTargetingMeNamePlateMarker ?? TargetingMeMarkerVisualStyle == TargetingMeMarkerVisualStyle.GazeMarker;
        set => enableTargetingMeNamePlateMarker = value;
    }

    [JsonIgnore]
    public bool EnableTargetingMeVfxMarker
    {
        get => enableTargetingMeVfxMarker ?? TargetingMeMarkerVisualStyle == TargetingMeMarkerVisualStyle.Vfx;
        set => enableTargetingMeVfxMarker = value;
    }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
