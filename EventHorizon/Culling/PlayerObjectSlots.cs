namespace EventHorizon.Culling;

internal static class PlayerObjectSlots
{
    public const int FirstPlayer = 2;
    public const int LastPlayer = 198;
    public const int LastPlayerRelated = 199;

    public static bool IsPlayer(int index) => index is >= FirstPlayer and <= LastPlayer && index % 2 == 0;

    public static bool IsAttached(int index) => index is >= 0 and <= LastPlayerRelated && index % 2 == 1;

    public static bool IsLocalReserved(int index) => index is 0 or 1;
}
