using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using EventHorizon.Culling.Optimization;

namespace EventHorizon.Culling.Visibility;

internal sealed class PlayerVisibilitySolverWorker : IDisposable
{
    private readonly object gate = new();
    private readonly AutoResetEvent snapshotAvailable = new(false);
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private readonly Task workerTask;
    private readonly string pluginDirectory;

    private PlayerVisibilitySolverSnapshot? pendingSnapshot;
    private PlayerVisibilitySolverWorkerStats latestStats;
    private bool hasPendingSnapshot;
    private bool disposed;
    private int epoch;
    private int submittedCount;
    private int completedCount;
    private int pendingSnapshotReplacedCount;
    private int exceptionCount;
    private int optimalCount;
    private int feasibleCount;
    private int unknownCount;
    private int inspectedCount;
    private long initializationTicks;

    public PlayerVisibilitySolverWorker(string pluginDirectory)
    {
        this.pluginDirectory = pluginDirectory;
        workerTask = Task.Run(WorkerLoop);
    }

    public void Submit(PlayerVisibilitySolverSnapshot snapshot)
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            submittedCount++;
            if (hasPendingSnapshot)
            {
                pendingSnapshotReplacedCount++;
            }

            pendingSnapshot = snapshot;
            hasPendingSnapshot = true;
            latestStats = latestStats with { SubmittedCount = submittedCount, PendingSnapshotReplacedCount = pendingSnapshotReplacedCount };
        }

        snapshotAvailable.Set();
    }

    public PlayerVisibilitySolverWorkerStats GetStats()
    {
        lock (gate)
        {
            return latestStats;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            epoch++;
            pendingSnapshot = null;
            hasPendingSnapshot = false;
            submittedCount = 0;
            completedCount = 0;
            pendingSnapshotReplacedCount = 0;
            exceptionCount = 0;
            optimalCount = 0;
            feasibleCount = 0;
            unknownCount = 0;
            inspectedCount = 0;
            latestStats = default;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            pendingSnapshot = null;
            hasPendingSnapshot = false;
            epoch++;
        }

        cancellationTokenSource.Cancel();
        snapshotAvailable.Set();
        try
        {
            workerTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException) { }

        snapshotAvailable.Dispose();
        cancellationTokenSource.Dispose();
    }

    private void WorkerLoop()
    {
        InitializeSolver();
        while (!cancellationTokenSource.IsCancellationRequested)
        {
            snapshotAvailable.WaitOne();
            while (TryTakePendingSnapshot(out var snapshot, out var snapshotEpoch))
            {
                ProcessSnapshot(snapshot, snapshotEpoch);
            }
        }
    }

    private void InitializeSolver()
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            OrToolsNativeDependencyLoader.EnsureLoaded(pluginDirectory, cancellationTokenSource.Token);
            PlayerVisibilityCpSatOptimizer.WarmUp();
        }
        catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested) { }
        catch
        {
            // ProcessSnapshot retries initialization and exposes any failure through LastError.
        }
        finally
        {
            initializationTicks = Stopwatch.GetTimestamp() - start;
        }
    }

    private bool TryTakePendingSnapshot(out PlayerVisibilitySolverSnapshot snapshot, out int snapshotEpoch)
    {
        lock (gate)
        {
            if (!hasPendingSnapshot || pendingSnapshot == null)
            {
                snapshot = null!;
                snapshotEpoch = 0;
                return false;
            }

            snapshot = pendingSnapshot;
            snapshotEpoch = epoch;
            pendingSnapshot = null;
            hasPendingSnapshot = false;
            return true;
        }
    }

    private void ProcessSnapshot(PlayerVisibilitySolverSnapshot snapshot, int snapshotEpoch)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            var velocitySampleCount = CountVelocitySamples(snapshot);
            OrToolsNativeDependencyLoader.EnsureLoaded(pluginDirectory);
            var cpSatStats = PlayerVisibilityCpSatOptimizer.Solve(snapshot);
            var workerTicks = Stopwatch.GetTimestamp() - start;
            PublishStats(snapshot, snapshotEpoch, velocitySampleCount, workerTicks, cpSatStats, succeeded: true);
        }
        catch (Exception ex)
        {
            var workerTicks = Stopwatch.GetTimestamp() - start;
            PublishStats(
                snapshot,
                snapshotEpoch,
                velocitySampleCount: 0,
                workerTicks,
                default,
                succeeded: false,
                lastError: $"{ex.GetType().Name}: {ex.Message}"
            );
        }
    }

    private static int CountVelocitySamples(PlayerVisibilitySolverSnapshot snapshot)
    {
        var count = 0;
        foreach (var player in snapshot.CompetitivePlayers)
        {
            if (player.HasVelocitySample)
            {
                count++;
            }
        }

        return count;
    }

    private void PublishStats(
        PlayerVisibilitySolverSnapshot snapshot,
        int snapshotEpoch,
        int velocitySampleCount,
        long workerTicks,
        PlayerVisibilityCpSatStats cpSatStats,
        bool succeeded,
        string? lastError = null
    )
    {
        lock (gate)
        {
            if (disposed || snapshotEpoch != epoch)
            {
                return;
            }

            if (succeeded)
            {
                completedCount++;
                switch (cpSatStats.Status)
                {
                    case "Optimal":
                        optimalCount++;
                        break;
                    case "OptimalByInspection":
                        inspectedCount++;
                        break;
                    case "Feasible":
                        feasibleCount++;
                        break;
                    case "Unknown":
                        unknownCount++;
                        break;
                }
            }
            else
            {
                exceptionCount++;
            }

            latestStats = new PlayerVisibilitySolverWorkerStats(
                submittedCount,
                completedCount,
                pendingSnapshotReplacedCount,
                exceptionCount,
                snapshot.Generation,
                snapshot.CompetitivePlayers.Count,
                snapshot.CompetitiveBudget,
                snapshot.PositionSampleCount,
                velocitySampleCount,
                workerTicks,
                snapshot.GetAgeMilliseconds(Environment.TickCount64),
                succeeded,
                cpSatStats,
                lastError,
                optimalCount,
                feasibleCount,
                unknownCount,
                inspectedCount,
                initializationTicks
            );
        }
    }
}

internal readonly record struct PlayerVisibilitySolverWorkerStats(
    int SubmittedCount,
    int CompletedCount,
    int PendingSnapshotReplacedCount,
    int ExceptionCount,
    int LastGeneration,
    int LastInputCount,
    int LastBudget,
    int LastPositionSampleCount,
    int LastVelocitySampleCount,
    long LastWorkerTicks,
    long LastResultAgeMs,
    bool LastSucceeded,
    PlayerVisibilityCpSatStats CpSat = default,
    string? LastError = null,
    int OptimalCount = 0,
    int FeasibleCount = 0,
    int UnknownCount = 0,
    int InspectedCount = 0,
    long InitializationTicks = 0
)
{
    public bool HasValue => SubmittedCount > 0 || CompletedCount > 0 || PendingSnapshotReplacedCount > 0 || ExceptionCount > 0;
}
