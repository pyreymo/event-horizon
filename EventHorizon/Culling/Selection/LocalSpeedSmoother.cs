using System;
using System.Numerics;

namespace EventHorizon.Culling.Selection;

internal sealed class LocalSpeedSmoother(PlayerVisibilitySelectionParameters parameters)
{
    private static readonly double Ln2 = Math.Log(2);
    private readonly PlayerVisibilitySelectionParameters parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
    private TimeSpan previousTimestamp;
    private Vector3 previousPosition;
    private bool hasBaseline;

    public double SmoothedSpeed { get; private set; }
    public Vector3 SmoothedVelocity { get; private set; }
    public bool HasVelocityEstimate { get; private set; }

    public double AddSample(TimeSpan timestamp, Vector3 position)
    {
        if (!IsFinite(position))
        {
            return SmoothedSpeed;
        }

        if (!hasBaseline)
        {
            previousTimestamp = timestamp;
            previousPosition = position;
            hasBaseline = true;
            SmoothedSpeed = 0;
            SmoothedVelocity = Vector3.Zero;
            HasVelocityEstimate = false;
            return SmoothedSpeed;
        }

        var elapsedSeconds = (timestamp - previousTimestamp).TotalSeconds;
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0)
        {
            return SmoothedSpeed;
        }

        var displacement = position - previousPosition;
        var distance = displacement.Length();
        var instantaneousSpeed = distance / elapsedSeconds;
        var instantaneousVelocity = displacement / (float)elapsedSeconds;
        var alpha = 1 - Math.Exp(-Ln2 * elapsedSeconds / parameters.LocalSpeedHalfLifeSeconds);

        previousTimestamp = timestamp;
        previousPosition = position;

        if (!float.IsFinite(distance) || !double.IsFinite(instantaneousSpeed) || instantaneousSpeed > parameters.MaxTrustedLocalSpeed)
        {
            instantaneousSpeed = 0;
            instantaneousVelocity = Vector3.Zero;
            HasVelocityEstimate = false;
        }
        else
        {
            HasVelocityEstimate = true;
        }

        SmoothedSpeed += alpha * (instantaneousSpeed - SmoothedSpeed);
        SmoothedVelocity += (float)alpha * (instantaneousVelocity - SmoothedVelocity);
        if (!double.IsFinite(SmoothedSpeed) || SmoothedSpeed < 0)
        {
            SmoothedSpeed = 0;
        }

        if (!IsFinite(SmoothedVelocity))
        {
            SmoothedVelocity = Vector3.Zero;
            HasVelocityEstimate = false;
        }

        return SmoothedSpeed;
    }

    public void Reset()
    {
        previousTimestamp = default;
        previousPosition = default;
        hasBaseline = false;
        SmoothedSpeed = 0;
        SmoothedVelocity = Vector3.Zero;
        HasVelocityEstimate = false;
    }

    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
