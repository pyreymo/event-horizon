using System;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace EventHorizon.Integration.NativeUi;

internal static unsafe class NativeAtkGeometry
{
    public static (float X, float Y) ConvertLocalPoint(AtkResNode* fromNode, AtkResNode* toAncestor, float x, float y)
    {
        const int MaxParents = 16;
        var node = fromNode;
        for (var index = 0; node != null && node != toAncestor && index < MaxParents; index++)
        {
            var scaleX = SafeScale(node->ScaleX);
            var scaleY = SafeScale(node->ScaleY);
            x = node->X + node->OriginX + ((x - node->OriginX) * scaleX);
            y = node->Y + node->OriginY + ((y - node->OriginY) * scaleY);
            node = node->ParentNode;
        }

        return (x, y);
    }

    public static (float ScaleX, float ScaleY) GetInheritedScale(AtkResNode* node)
    {
        const int MaxParents = 16;
        var scaleX = 1f;
        var scaleY = 1f;
        for (var index = 0; node != null && index < MaxParents; index++)
        {
            scaleX *= SafeScale(node->ScaleX);
            scaleY *= SafeScale(node->ScaleY);
            node = node->ParentNode;
        }

        return (SafeScale(scaleX), SafeScale(scaleY));
    }

    public static float SafeScale(float scale)
    {
        return MathF.Abs(scale) < 0.001f ? 1f : scale;
    }

    public static float DivideByScale(float value, float scale)
    {
        return value / SafeScale(scale);
    }
}
