using System;
using System.Collections.Generic;
using System.Numerics;

namespace EventHorizon.Culling;

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
        var alpha = 1 - Math.Exp(-Ln2 * elapsedSeconds / PlayerVisibilitySelectionParameters.LocalSpeedHalfLifeSeconds);

        previousTimestamp = timestamp;
        previousPosition = position;

        if (
            !float.IsFinite(distance)
            || !double.IsFinite(instantaneousSpeed)
            || instantaneousSpeed > PlayerVisibilitySelectionParameters.MaxTrustedLocalSpeed
        )
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

internal sealed class PlayerVelocityTracker(PlayerVisibilitySelectionParameters parameters)
{
    private static readonly double Ln2 = Math.Log(2);
    private readonly PlayerVisibilitySelectionParameters parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
    private readonly Dictionary<PlayerObjectIdentity, MotionState> states = [];

    public PlayerVelocityEstimate AddSample(TimeSpan timestamp, PlayerObjectIdentity identity, Vector3 position)
    {
        if (!IsFinite(position))
        {
            return GetEstimate(identity);
        }

        if (!states.TryGetValue(identity, out var state))
        {
            states[identity] = new MotionState(timestamp, position, Vector3.Zero, false);
            return default;
        }

        var elapsedSeconds = (timestamp - state.Timestamp).TotalSeconds;
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0)
        {
            return new PlayerVelocityEstimate(state.SmoothedVelocity, state.HasVelocityEstimate);
        }

        var displacement = position - state.Position;
        var distance = displacement.Length();
        var instantaneousSpeed = distance / elapsedSeconds;
        if (
            !float.IsFinite(distance)
            || !double.IsFinite(instantaneousSpeed)
            || instantaneousSpeed > PlayerVisibilitySelectionParameters.MaxTrustedLocalSpeed
        )
        {
            states[identity] = new MotionState(timestamp, position, Vector3.Zero, false);
            return default;
        }

        var instantaneousVelocity = displacement / (float)elapsedSeconds;
        var alpha = 1 - Math.Exp(-Ln2 * elapsedSeconds / PlayerVisibilitySelectionParameters.LocalSpeedHalfLifeSeconds);
        var smoothedVelocity = state.SmoothedVelocity + ((float)alpha * (instantaneousVelocity - state.SmoothedVelocity));
        if (!IsFinite(smoothedVelocity))
        {
            states[identity] = new MotionState(timestamp, position, Vector3.Zero, false);
            return default;
        }

        states[identity] = new MotionState(timestamp, position, smoothedVelocity, true);
        return new PlayerVelocityEstimate(smoothedVelocity, true);
    }

    public PlayerVelocityEstimate GetEstimate(PlayerObjectIdentity identity) =>
        states.TryGetValue(identity, out var state)
            ? new PlayerVelocityEstimate(state.SmoothedVelocity, state.HasVelocityEstimate)
            : default;

    public void PruneExcept(IReadOnlySet<PlayerObjectIdentity> activeIdentities)
    {
        ArgumentNullException.ThrowIfNull(activeIdentities);
        var staleIdentities = new List<PlayerObjectIdentity>();
        foreach (var identity in states.Keys)
        {
            if (!activeIdentities.Contains(identity))
            {
                staleIdentities.Add(identity);
            }
        }

        foreach (var identity in staleIdentities)
        {
            states.Remove(identity);
        }
    }

    public void Clear() => states.Clear();

    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private readonly record struct MotionState(TimeSpan Timestamp, Vector3 Position, Vector3 SmoothedVelocity, bool HasVelocityEstimate);
}

internal readonly record struct PlayerVelocityEstimate(Vector3 Velocity, bool HasVelocityEstimate);
