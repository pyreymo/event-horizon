using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Google.OrTools.Sat;

namespace EventHorizon.Culling.Optimization;

internal sealed class CpSatPhase0Probe(IPluginLog log, string pluginDirectory) : IDisposable
{
    private const string SolverParameters = "num_search_workers:1 max_time_in_seconds:1.0";

    private readonly IPluginLog log = log;
    private readonly string pluginDirectory = pluginDirectory;
    private readonly object gate = new();
    private CancellationTokenSource? cancellationTokenSource;
    private Task? runningTask;
    private bool disposed;

    public bool TryStart()
    {
        lock (gate)
        {
            if (disposed)
            {
                return false;
            }

            if (runningTask is { IsCompleted: false })
            {
                log.Information("[CP-SAT Phase 0] Probe already running.");
                return false;
            }

            cancellationTokenSource?.Dispose();
            cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = cancellationTokenSource.Token;
            runningTask = Task.Factory.StartNew(
                () => Run(cancellationToken),
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            );
            log.Information("[CP-SAT Phase 0] Probe started on a background worker.");
            return true;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            cancellationTokenSource?.Cancel();
            cancellationTokenSource?.Dispose();
            cancellationTokenSource = null;
        }
    }

    private void Run(CancellationToken cancellationToken)
    {
        var totalStart = Stopwatch.GetTimestamp();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var modelStart = Stopwatch.GetTimestamp();
            var model = new CpModel();
            var playerA = model.NewBoolVar("player_a");
            var playerB = model.NewBoolVar("player_b");
            var playerC = model.NewBoolVar("player_c");
            var players = new[] { playerA, playerB, playerC };
            model.Add(LinearExpr.Sum(players) <= 2);
            model.Maximize(LinearExpr.WeightedSum(players, new long[] { 100, 80, 10 }));
            var modelTicks = Stopwatch.GetTimestamp() - modelStart;

            cancellationToken.ThrowIfCancellationRequested();

            EnsureNativeLibrariesLoaded(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var solveStart = Stopwatch.GetTimestamp();
            var solver = new CpSolver { StringParameters = SolverParameters };
            var status = solver.Solve(model, null);
            var solveTicks = Stopwatch.GetTimestamp() - solveStart;

            cancellationToken.ThrowIfCancellationRequested();
            if (IsDisposed())
            {
                return;
            }

            var hasSolution = status is CpSolverStatus.Optimal or CpSolverStatus.Feasible;
            var selectedCount = hasSolution
                ? (solver.BooleanValue(playerA) ? 1 : 0) + (solver.BooleanValue(playerB) ? 1 : 0) + (solver.BooleanValue(playerC) ? 1 : 0)
                : 0;
            var objectiveValue = hasSolution ? solver.ObjectiveValue : double.NaN;
            log.Information(
                "[CP-SAT Phase 0] Probe finished status={Status} objective={Objective} selected={SelectedCount}/3 model={ModelMs:F3}ms solve={SolveMs:F3}ms total={TotalMs:F3}ms wall={WallMs:F3}ms parameters={Parameters}",
                status,
                objectiveValue,
                selectedCount,
                ToMilliseconds(modelTicks),
                ToMilliseconds(solveTicks),
                ToMilliseconds(Stopwatch.GetTimestamp() - totalStart),
                solver.WallTime() * 1000.0,
                SolverParameters
            );
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed())
            {
                log.Information("[CP-SAT Phase 0] Probe cancelled.");
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed())
            {
                log.Error(ex, "[CP-SAT Phase 0] Probe failed.");
            }
        }
    }

    private void EnsureNativeLibrariesLoaded(CancellationToken cancellationToken)
    {
        if (OrToolsNativeDependencyLoader.EnsureLoaded(pluginDirectory, cancellationToken))
        {
            log.Information("[CP-SAT Phase 0] Loaded OR-Tools native dependencies from {PluginDirectory}.", pluginDirectory);
        }
    }

    private bool IsDisposed()
    {
        lock (gate)
        {
            return disposed;
        }
    }

    private static double ToMilliseconds(long stopwatchTicks) => stopwatchTicks * 1000.0 / Stopwatch.Frequency;
}
