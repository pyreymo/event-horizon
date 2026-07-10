using System;

namespace EventHorizon.Culling.Selection;

internal sealed class PlayerVisibilitySelectionParameters
{
    public const int DefaultRankCount = 8;
    public const long DefaultRankStep = 3_000;
    public const long DefaultSoftScoreScale = 1_000;
    public const long DefaultRestRetentionBonus = 500;
    public const long DefaultMoveRetentionBonus = 23_000;
    public const int DefaultPredictionSteps = 4;
    public const double DefaultPredictionStepSeconds = 0.2;
    public const double DefaultPredictionGamma = 0.85;
    public const double DefaultDistanceSigma = 30.0;
    public const double DefaultMotionStartSpeed = 0.5;
    public const double DefaultMotionFullSpeed = 4.0;
    public const double DefaultLocalSpeedHalfLifeSeconds = 0.35;
    public const double DefaultMaxTrustedLocalSpeed = 50.0;

    public static PlayerVisibilitySelectionParameters Default { get; } = new();

    public PlayerVisibilitySelectionParameters(
        int rankCount = DefaultRankCount,
        long rankStep = DefaultRankStep,
        long softScoreScale = DefaultSoftScoreScale,
        long restRetentionBonus = DefaultRestRetentionBonus,
        long moveRetentionBonus = DefaultMoveRetentionBonus,
        int predictionSteps = DefaultPredictionSteps,
        double predictionStepSeconds = DefaultPredictionStepSeconds,
        double predictionGamma = DefaultPredictionGamma,
        double distanceSigma = DefaultDistanceSigma,
        double motionStartSpeed = DefaultMotionStartSpeed,
        double motionFullSpeed = DefaultMotionFullSpeed,
        double localSpeedHalfLifeSeconds = DefaultLocalSpeedHalfLifeSeconds,
        double maxTrustedLocalSpeed = DefaultMaxTrustedLocalSpeed
    )
    {
        if (rankCount < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(rankCount), rankCount, "RankCount must be at least 2.");
        }

        if (softScoreScale < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(softScoreScale), softScoreScale, "SoftScoreScale cannot be negative.");
        }

        if (restRetentionBonus < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(restRetentionBonus), restRetentionBonus, "RestRetentionBonus cannot be negative.");
        }

        if (rankStep <= (decimal)softScoreScale + restRetentionBonus)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rankStep),
                rankStep,
                "RankStep must be greater than SoftScoreScale + RestRetentionBonus."
            );
        }

        var maxBaseScore = ((decimal)(rankCount - 1) * rankStep) + softScoreScale;
        if (moveRetentionBonus <= maxBaseScore)
        {
            throw new ArgumentOutOfRangeException(
                nameof(moveRetentionBonus),
                moveRetentionBonus,
                "MoveRetentionBonus must be greater than (RankCount - 1) * RankStep + SoftScoreScale."
            );
        }

        if (maxBaseScore + moveRetentionBonus > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(moveRetentionBonus),
                moveRetentionBonus,
                "MaxBaseScore + MoveRetentionBonus must not exceed Int64.MaxValue."
            );
        }

        if (predictionSteps < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(predictionSteps), predictionSteps, "PredictionSteps must be at least 1.");
        }

        RequireFinitePositive(predictionStepSeconds, nameof(predictionStepSeconds));
        if (!double.IsFinite(predictionGamma) || predictionGamma <= 0 || predictionGamma > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(predictionGamma), predictionGamma, "PredictionGamma must be in (0, 1].");
        }

        RequireFinitePositive(distanceSigma, nameof(distanceSigma));
        if (!double.IsFinite(motionStartSpeed) || motionStartSpeed < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(motionStartSpeed),
                motionStartSpeed,
                "MotionStartSpeed must be finite and non-negative."
            );
        }

        if (!double.IsFinite(motionFullSpeed) || motionFullSpeed <= motionStartSpeed)
        {
            throw new ArgumentOutOfRangeException(
                nameof(motionFullSpeed),
                motionFullSpeed,
                "MotionFullSpeed must be finite and greater than MotionStartSpeed."
            );
        }

        RequireFinitePositive(localSpeedHalfLifeSeconds, nameof(localSpeedHalfLifeSeconds));
        RequireFinitePositive(maxTrustedLocalSpeed, nameof(maxTrustedLocalSpeed));

        RankCount = rankCount;
        RankStep = rankStep;
        SoftScoreScale = softScoreScale;
        RestRetentionBonus = restRetentionBonus;
        MoveRetentionBonus = moveRetentionBonus;
        PredictionSteps = predictionSteps;
        PredictionStepSeconds = predictionStepSeconds;
        PredictionGamma = predictionGamma;
        DistanceSigma = distanceSigma;
        MotionStartSpeed = motionStartSpeed;
        MotionFullSpeed = motionFullSpeed;
        LocalSpeedHalfLifeSeconds = localSpeedHalfLifeSeconds;
        MaxTrustedLocalSpeed = maxTrustedLocalSpeed;
    }

    public int RankCount { get; }
    public long RankStep { get; }
    public long SoftScoreScale { get; }
    public long RestRetentionBonus { get; }
    public long MoveRetentionBonus { get; }
    public int PredictionSteps { get; }
    public double PredictionStepSeconds { get; }
    public double PredictionGamma { get; }
    public double DistanceSigma { get; }
    public double MotionStartSpeed { get; }
    public double MotionFullSpeed { get; }
    public double LocalSpeedHalfLifeSeconds { get; }
    public double MaxTrustedLocalSpeed { get; }

    private static void RequireFinitePositive(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"{parameterName} must be finite and positive.");
        }
    }
}
