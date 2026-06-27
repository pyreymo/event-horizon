namespace EventHorizon.ObjectTable;

internal static class PlayerPreviewConstants
{
    public const float DefaultViewRange = 50f;
    public const float MinimumRange = 1f;
    public const float DisabledNearbyRange = 0f;
    public const float NearbyRangeMin = 1f;
    public const float NearbyRangeMax = DefaultViewRange;

    // FFXIVClientStructs GameObject._name: FieldOffset(0x30), FixedSizeArray64<byte>.
    public const int GameObjectNameOffset = 0x30;
    public const int GameObjectNameLength = 64;

    public const float CardContentRightPadding = 18f;
    public const float PreviewOuterPadding = 14f;
    public const float BorderRounding = 4f;
    public const int RangeCircleSegments = 64;
    public const int DotCircleSegments = 16;
    public const float RangeCircleThickness = 1.2f;
    public const float BudgetCutRingThickness = 1.4f;

    public const float LocalPlayerDotRadius = 4f;
    public const float PlayerDotRadius = 4f;
    public const float HoveredPlayerDotRadius = 6f;
    public const float HoverRadius = 7f;
    public const float BudgetCutRingPadding = 2f;
}
