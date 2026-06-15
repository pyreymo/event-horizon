using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace EventHorizon.Rendering;

internal sealed unsafe class CharacterAlphaController(IObjectTable objectTable) : IDisposable
{
    public const float OpaqueAlpha = 1f;

    public bool TrySetLocalPlayerAlpha(float alpha)
    {
        var character = GetLocalCharacter();
        if (character == null)
        {
            return false;
        }

        character->Alpha = Math.Clamp(alpha, 0f, 1f);
        return true;
    }

    public void ResetLocalPlayerAlpha()
    {
        TrySetLocalPlayerAlpha(OpaqueAlpha);
    }

    public void Dispose()
    {
        ResetLocalPlayerAlpha();
    }

    private Character* GetLocalCharacter()
    {
        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null || localPlayer.Address == nint.Zero)
        {
            return null;
        }

        return (Character*)localPlayer.Address;
    }
}
