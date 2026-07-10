using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Google.OrTools.Sat;

namespace EventHorizon.Culling.Visibility;

internal static class PlayerVisibilityCpSatOptimizer
{
    private const int HorizonSteps = 8;
    private const float PredictionStepSeconds = 0.2f;
    private const double Gamma = 0.85;
    private const double Epsilon = 0.03;
    private const int UtilityScale = 10_000;
    private const string SolverParameters = "num_search_workers:1 max_time_in_seconds:0.004";

    public static PlayerVisibilityCpSatStats Solve(PlayerVisibilitySolverSnapshot snapshot)
    {
        var modelStart = Stopwatch.GetTimestamp();
        var playerCount = snapshot.CompetitivePlayers.Count;
        var budget = Math.Clamp(snapshot.CompetitiveBudget, 0, playerCount);
        if (playerCount == 0 || budget == 0)
        {
            return new PlayerVisibilityCpSatStats("Empty", 0, 0, 0, 0, 0, 0, Stopwatch.GetTimestamp() - modelStart, 0);
        }

        var utilities = BuildUtilities(snapshot);
        var idealVisibility = new bool[playerCount, HorizonSteps];
        var jStar = CalculateJStar(utilities, playerCount, budget, idealVisibility);
        var jThreshold = (long)Math.Ceiling((1.0 - Epsilon) * jStar);
        if (budget == playerCount)
        {
            var inspectionSwitchCount = 0;
            foreach (var player in snapshot.CompetitivePlayers)
            {
                if (!player.PreviousTargetVisible)
                {
                    inspectionSwitchCount++;
                }
            }

            return new PlayerVisibilityCpSatStats(
                "OptimalByInspection",
                0,
                0,
                jStar,
                jThreshold,
                jStar,
                inspectionSwitchCount,
                Stopwatch.GetTimestamp() - modelStart,
                0,
                inspectionSwitchCount
            );
        }

        var switchWeight = jStar - jThreshold + 1;

        var model = new CpModel();
        var visible = new BoolVar[playerCount, HorizonSteps];
        var switches = new List<BoolVar>(playerCount * HorizonSteps);
        var utilityVariables = new List<IntVar>(playerCount * HorizonSteps);
        var utilityCoefficients = new List<long>(playerCount * HorizonSteps);
        var constraintCount = 0;

        for (var playerIndex = 0; playerIndex < playerCount; playerIndex++)
        {
            for (var step = 0; step < HorizonSteps; step++)
            {
                var variable = model.NewBoolVar($"x_{playerIndex}_{step}");
                visible[playerIndex, step] = variable;
                model.AddHint(variable, idealVisibility[playerIndex, step] ? 1 : 0);
                utilityVariables.Add(variable);
                utilityCoefficients.Add(utilities[playerIndex, step]);

                var switchVariable = model.NewBoolVar($"d_{playerIndex}_{step}");
                switches.Add(switchVariable);
                if (step == 0)
                {
                    var previous = snapshot.CompetitivePlayers[playerIndex].PreviousTargetVisible ? 1 : 0;
                    model.AddAbsEquality(switchVariable, variable - previous);
                    model.AddHint(switchVariable, idealVisibility[playerIndex, step] != (previous != 0) ? 1 : 0);
                }
                else
                {
                    model.AddAbsEquality(switchVariable, variable - visible[playerIndex, step - 1]);
                    model.AddHint(switchVariable, idealVisibility[playerIndex, step] != idealVisibility[playerIndex, step - 1] ? 1 : 0);
                }

                constraintCount++;
            }
        }

        for (var step = 0; step < HorizonSteps; step++)
        {
            var stepVariables = new IntVar[playerCount];
            for (var playerIndex = 0; playerIndex < playerCount; playerIndex++)
            {
                stepVariables[playerIndex] = visible[playerIndex, step];
            }

            model.Add(LinearExpr.Sum(stepVariables) <= budget);
            constraintCount++;
        }

        var utilityExpression = LinearExpr.WeightedSum(utilityVariables, utilityCoefficients);
        var switchExpression = LinearExpr.Sum(switches);
        model.Add(utilityExpression >= jThreshold);
        constraintCount++;
        model.Minimize(switchWeight * switchExpression - utilityExpression);
        var modelTicks = Stopwatch.GetTimestamp() - modelStart;

        var solveStart = Stopwatch.GetTimestamp();
        var solver = new CpSolver { StringParameters = SolverParameters };
        var status = solver.Solve(model, null);
        var solveTicks = Stopwatch.GetTimestamp() - solveStart;
        var hasSolution = status is CpSolverStatus.Optimal or CpSolverStatus.Feasible;
        var finalJ = hasSolution ? solver.Value(utilityExpression) : 0;
        var finalD = hasSolution ? (int)solver.Value(switchExpression) : 0;
        var currentStepSwitchCount = 0;
        if (hasSolution)
        {
            for (var playerIndex = 0; playerIndex < playerCount; playerIndex++)
            {
                var previous = snapshot.CompetitivePlayers[playerIndex].PreviousTargetVisible;
                if (solver.BooleanValue(visible[playerIndex, 0]) != previous)
                {
                    currentStepSwitchCount++;
                }
            }
        }

        return new PlayerVisibilityCpSatStats(
            status.ToString(),
            playerCount * HorizonSteps * 2,
            constraintCount,
            jStar,
            jThreshold,
            finalJ,
            finalD,
            modelTicks,
            solveTicks,
            currentStepSwitchCount
        );
    }

    private static long[,] BuildUtilities(PlayerVisibilitySolverSnapshot snapshot)
    {
        var playerCount = snapshot.CompetitivePlayers.Count;
        var result = new long[playerCount, HorizonSteps];
        var maxRank = 0;
        foreach (var player in snapshot.CompetitivePlayers)
        {
            maxRank = Math.Max(maxRank, player.Decision.Rank);
        }

        for (var playerIndex = 0; playerIndex < playerCount; playerIndex++)
        {
            var player = snapshot.CompetitivePlayers[playerIndex];
            var rankUtility = 1.0 - 0.2 * player.Decision.Rank / Math.Max(1, maxRank + 1);
            for (var step = 0; step < HorizonSteps; step++)
            {
                var distanceUtility = 0.0;
                if (snapshot.HasLocalPlayerPosition && player.HasPosition)
                {
                    var predictedPosition = player.Position + player.VelocityPerSecond * (step * PredictionStepSeconds);
                    var distance = Vector3.Distance(snapshot.LocalPlayerPosition, predictedPosition);
                    distanceUtility = 0.2 / (1.0 + distance / 30.0);
                }

                var utility = Math.Clamp(rankUtility + distanceUtility, 0.0, 1.0);
                result[playerIndex, step] = (long)Math.Round(UtilityScale * Math.Pow(Gamma, step) * utility);
            }
        }

        return result;
    }

    private static long CalculateJStar(long[,] utilities, int playerCount, int budget, bool[,] idealVisibility)
    {
        var rankedPlayers = new PlayerUtility[playerCount];
        long result = 0;
        for (var step = 0; step < HorizonSteps; step++)
        {
            for (var playerIndex = 0; playerIndex < playerCount; playerIndex++)
            {
                rankedPlayers[playerIndex] = new PlayerUtility(playerIndex, utilities[playerIndex, step]);
            }

            Array.Sort(rankedPlayers, static (left, right) => right.Utility.CompareTo(left.Utility));
            for (var index = 0; index < budget; index++)
            {
                var player = rankedPlayers[index];
                idealVisibility[player.PlayerIndex, step] = true;
                result += player.Utility;
            }
        }

        return result;
    }

    private readonly record struct PlayerUtility(int PlayerIndex, long Utility);
}

internal readonly record struct PlayerVisibilityCpSatStats(
    string Status,
    int VariableCount,
    int ConstraintCount,
    long JStar,
    long JThreshold,
    long FinalJ,
    int FinalD,
    long ModelTicks,
    long SolveTicks,
    int CurrentStepSwitchCount = 0
);
