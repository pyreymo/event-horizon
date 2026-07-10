using System;
using System.Collections.Generic;

namespace EventHorizon.Culling.Visibility;

internal sealed record PlayerVisibilityActiveTargetResolution(
    PlayerVisibilityTargetSet ActiveTarget,
    PlayerVisibilitySelectionEvaluation Evaluation,
    Exception? FailureException = null
);

internal static class PlayerVisibilityActiveTargetResolver
{
    public static PlayerVisibilityActiveTargetResolution Resolve(
        PlayerVisibilityPlan plan,
        PlayerVisibilityTargetSet legacyTarget,
        PlayerVisibilitySelectionEvaluation evaluation,
        List<PlayerVisibilityTarget> stableTargetBuffer
    )
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(legacyTarget);
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(stableTargetBuffer);

        var activeTarget = legacyTarget;
        Exception? failureException = null;
        if (evaluation.Status == PlayerVisibilitySelectionStatus.Ready)
        {
            try
            {
                activeTarget = PlayerVisibilityStableTargetBuilder.Build(plan, evaluation.SelectedKeys, stableTargetBuffer);
            }
            catch (Exception exception)
            {
                failureException = exception;
                evaluation = evaluation with { Status = PlayerVisibilitySelectionStatus.Failed };
                activeTarget = legacyTarget;
            }
        }
        return new PlayerVisibilityActiveTargetResolution(activeTarget, evaluation, failureException);
    }
}
