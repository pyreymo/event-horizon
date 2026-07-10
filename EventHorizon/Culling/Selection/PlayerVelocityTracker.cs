using System;
using System.Collections.Generic;
using System.Numerics;

namespace EventHorizon.Culling.Selection;

internal sealed class PlayerVelocityTracker<TKey>(PlayerVisibilitySelectionParameters parameters)
    where TKey : notnull
{
    private static readonly double Ln2 = Math.Log(2);
    private readonly PlayerVisibilitySelectionParameters parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
    private readonly Dictionary<TKey, MotionState> states = [];

    public int Count => states.Count;

    public PlayerVelocityEstimate AddSample(TimeSpan timestamp, TKey identity, Vector3 position)
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
        if (!float.IsFinite(distance) || !double.IsFinite(instantaneousSpeed) || instantaneousSpeed > parameters.MaxTrustedLocalSpeed)
        {
            states[identity] = new MotionState(timestamp, position, Vector3.Zero, false);
            return default;
        }

        var instantaneousVelocity = displacement / (float)elapsedSeconds;
        var alpha = 1 - Math.Exp(-Ln2 * elapsedSeconds / parameters.LocalSpeedHalfLifeSeconds);
        var smoothedVelocity = state.SmoothedVelocity + ((float)alpha * (instantaneousVelocity - state.SmoothedVelocity));
        if (!IsFinite(smoothedVelocity))
        {
            states[identity] = new MotionState(timestamp, position, Vector3.Zero, false);
            return default;
        }

        states[identity] = new MotionState(timestamp, position, smoothedVelocity, true);
        return new PlayerVelocityEstimate(smoothedVelocity, true);
    }

    public PlayerVelocityEstimate GetEstimate(TKey identity) =>
        states.TryGetValue(identity, out var state)
            ? new PlayerVelocityEstimate(state.SmoothedVelocity, state.HasVelocityEstimate)
            : default;

    public void PruneExcept(IReadOnlySet<TKey> activeIdentities)
    {
        ArgumentNullException.ThrowIfNull(activeIdentities);
        var staleIdentities = new List<TKey>();
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
