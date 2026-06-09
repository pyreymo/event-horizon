using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace EventHorizon;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool HideAllOtherPlayers { get; set; } = true;
    public bool KeepFriends { get; set; } = true;
    public bool KeepPartyAndAllianceMembers { get; set; } = true;
    public bool KeepNearbyPlayers { get; set; }
    public float KeepNearbyPlayersRange { get; set; } = 5f;
    public bool PreviewNearbyPlayerRange { get; set; }
    public bool KeepSelectedRaces { get; set; }
    public HashSet<byte> KeptRaceSex { get; set; } = [];

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
