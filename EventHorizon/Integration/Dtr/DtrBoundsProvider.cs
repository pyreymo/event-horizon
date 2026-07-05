using EventHorizon.Integration.NativeUi;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace EventHorizon.Integration.Dtr;

internal static unsafe class DtrBoundsProvider
{
    public static bool TryGetBounds(AtkUnitBase* unit, AtkResNode* background, out NativeNodeBounds bounds)
    {
        if (unit == null || unit->RootNode == null)
        {
            bounds = default;
            return false;
        }

        var collector = new NativeNodeBoundsCollector();
        NativeNodeBoundsScanner.AddChildTreeBounds(unit->RootNode, background, ShouldUseDtrContentNode, ref collector);
        NativeNodeBoundsScanner.AddNodeListBounds(unit, background, ShouldUseDtrContentNode, ref collector);
        return collector.TryBuild(out bounds);
    }

    private static bool ShouldUseDtrContentNode(AtkResNode* node, AtkResNode* background)
    {
        return node != null
            && node != background
            && node->NodeFlags.HasFlag(NodeFlags.Visible)
            && node->Type != NodeType.Collision
            && node->Width > 0
            && node->Height > 0;
    }
}
