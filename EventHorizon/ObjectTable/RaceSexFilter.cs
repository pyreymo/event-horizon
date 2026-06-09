namespace EventHorizon.ObjectTable;

internal static class RaceSexFilter
{
    public const byte MinRace = 1;
    public const byte MaxRace = 8;
    public const byte MaleSex = 0;
    public const byte FemaleSex = 1;

    public static byte Pack(byte race, byte sex)
    {
        return (byte)(race | (sex << 4));
    }
}
