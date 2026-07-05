using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace EventHorizon.Integration.Dtr;

internal sealed class ChatLogBackgroundSkinProvider(IGameGui gameGui)
{
    private const string ChatLogAddonName = "ChatLog";

    public unsafe bool TryGetChatLogBackgroundSkin(out DtrBackgroundSkin skin)
    {
        var chatLogPointer = gameGui.GetAddonByName(ChatLogAddonName);
        if (chatLogPointer == nint.Zero)
        {
            skin = default;
            return false;
        }

        var source = ((AddonChatLog*)chatLogPointer.Address)->BackgroundNode;
        if (source == null || source->PartsList == null)
        {
            skin = default;
            return false;
        }

        skin = new DtrBackgroundSkin(
            source->PartsList,
            source->PartId,
            source->TopOffset,
            source->RightOffset,
            source->BottomOffset,
            source->LeftOffset,
            source->BlendMode,
            source->PartsTypeRenderType
        );
        return true;
    }
}
