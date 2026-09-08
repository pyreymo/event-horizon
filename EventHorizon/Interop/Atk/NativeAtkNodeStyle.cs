using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace EventHorizon.Interop.Atk;

internal static unsafe class NativeAtkNodeStyle
{
    public static void InitializeVisualDefaults(AtkResNode* node)
    {
        node->Color = new ByteColor
        {
            R = 255,
            G = 255,
            B = 255,
            A = 255,
        };
        node->AddRed = 0;
        node->AddGreen = 0;
        node->AddBlue = 0;
        node->MultiplyRed = 100;
        node->MultiplyGreen = 100;
        node->MultiplyBlue = 100;
        node->ScaleX = 1f;
        node->ScaleY = 1f;
    }

    public static void ApplyAlpha(AtkResNode* node, byte alpha)
    {
        node->Color.A = alpha;
        node->SetAlpha(alpha);
        node->Alpha_2 = alpha;
    }
}
