namespace EventHorizon.Culling;

internal static class CharacterObjectSlots
{
    public const int FirstRemoteSlot = 2;
    public const int LastEvenSlot = 198;
    public const int LastSlot = 199;

    public static bool IsEvenSlot(int index) => index is >= FirstRemoteSlot and <= LastEvenSlot && index % 2 == 0;

    public static bool IsOddSlot(int index) => index is >= 0 and <= LastSlot && index % 2 == 1;

    public static bool IsLocalReservedSlot(int index) => index is 0 or 1;
}
