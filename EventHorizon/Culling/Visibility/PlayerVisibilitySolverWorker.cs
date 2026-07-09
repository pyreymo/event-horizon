using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace EventHorizon.Culling.Visibility;

internal sealed class PlayerVisibilitySolverWorker : IDisposable
{
    private readonly object gate = new();
    private readonly AutoResetEvent snapshotAvailable = new(false);
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private readonly Task workerTask;

    private PlayerVisibilitySolverSnapshot? pendingSnapshot;
    private PlayerVisibilitySolverWorkerStats latestStats;
    private bool hasPendingSnapshot;
    private bool disposed;
    private int epoch;
    private int submittedCount;
    private int completedCount;
    private int pendingSnapshotReplacedCount;
    private int exceptionCount;

    public PlayerVisibilitySolverWorker()
    {
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
        while (!cancellationTokenSource.IsCancellationRequested)
        {
            snapshotAvailable.WaitOne();
            while (TryTakePendingSnapshot(out var snapshot, out var snapshotEpoch))
            {
                ProcessSnapshot(snapshot, snapshotEpoch);
            }
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
            var workerTicks = Stopwatch.GetTimestamp() - start;
            PublishStats(snapshot, snapshotEpoch, velocitySampleCount, workerTicks, succeeded: true);
        }
        catch
        {
            var workerTicks = Stopwatch.GetTimestamp() - start;
            PublishStats(snapshot, snapshotEpoch, velocitySampleCount: 0, workerTicks, succeeded: false);
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
        bool succeeded
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
                succeeded
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
    bool LastSucceeded
)
{
    public bool HasValue => SubmittedCount > 0 || CompletedCount > 0 || PendingSnapshotReplacedCount > 0 || ExceptionCount > 0;
}
