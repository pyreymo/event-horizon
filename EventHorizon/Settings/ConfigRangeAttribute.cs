namespace EventHorizon.Settings;

[AttributeUsage(AttributeTargets.Property)]
internal sealed class ConfigRangeAttribute(double minimum, double maximum) : Attribute
{
    public bool IsValid(object? value) =>
        value switch
        {
            int number => number >= minimum && number <= maximum,
            float number => float.IsFinite(number) && number >= minimum && number <= maximum,
            _ => false,
        };
}
