using System.Numerics;

namespace EventHorizon.Preview;

internal static class PlayerPreviewConstants
{
    public const float DefaultViewRange = 50f;
    public const float MinimumViewRange = 10f;
    public const float MaximumViewRange = 120f;
    public const float MouseWheelZoomStep = 1.15f;
    public const float MinimumRange = 1f;
    public const float DisabledNearbyRange = 0f;
    public const float NearbyRangeMin = 1f;
    public const float NearbyRangeMax = DefaultViewRange;
    public const int FastRefreshIntervalMs = 33;
    public const int SelectionVisibilityLeaseMs = 500;

    // FFXIVClientStructs GameObject._name: FieldOffset(0x30), FixedSizeArray64<byte>.
    public const int GameObjectNameOffset = 0x30;
    public const int GameObjectNameLength = 64;

    public const float CardContentRightPadding = 18f;
    public const float MinimumPreviewSide = 180f;
    public const float FloatingWindowDefaultSide = 300f;
    public const float FloatingWindowGearIconOffsetX = 1.5f;
    public const float PreviewOuterPadding = 14f;
    public const int RangeCircleSegments = 64;
    public const int DotCircleSegments = 16;
    public const float RangeCircleThickness = 1.2f;
    public const float BudgetCutRingThickness = 1.4f;
    public const float SelectedPlayerRingThickness = 2f;

    public const float LocalPlayerDotRadius = 4f;
    public const float PlayerDotRadius = 4f;
    public const float HoveredPlayerDotRadius = 6f;
    public const float HoverRadius = 7f;
    public const float BudgetCutRingPadding = 2f;
    public const float SelectedPlayerRingPadding = 4f;

    public static readonly Vector4 WorldArrowColor = new(1f, 0.5f, 0f, 1f); // Orange
    public const float WorldArrowLineThickness = 2f;
    public const float WorldArrowHeadLength = 12f;
    public const float WorldArrowHeadHalfWidth = 5f;
    public const float WorldArrowTargetRadius = 3f;
    public const float WorldArrowScreenEdgePadding = 24f;
}
