using Cove.Api.Services;

namespace Cove.Tests;

/// <summary>
/// Regression tests for the job ETA estimator. These reproduce the reported failure mode — a long
/// AI/scan job where many items are near-instant no-ops (already processed) interleaved with slow real
/// work, completing in bursts — and assert the estimate stays stable and close to the true remaining
/// time instead of swinging wildly (the old estimator showed 5h then 12h on a job that was ~2.5h out).
/// </summary>
public class JobEtaEstimatorTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Eta_matches_cumulative_pace_for_mixed_noop_and_real_workload()
    {
        var est = new JobService.JobEtaEstimator();
        est.Start(T0);

        // 3000 total items; 1000 completed over 75 minutes (4500s). 40% are real work (~10s each),
        // 60% are near-instant no-ops (~0.05s). This mirrors the reported case whose true remaining
        // time was ~2.5h.
        const int total = 3000;
        const int completed = 1000;
        const double wallSeconds = 4500d;
        for (var i = 0; i < completed; i++)
        {
            var now = T0.AddSeconds(wallSeconds * (i + 1) / completed);
            var isReal = i % 5 < 2; // 2 of every 5 => 400 real, 600 no-op
            est.ObserveUnitCompletion(isReal ? 10d : 0.05d, now);
        }

        var eta = est.EstimateSeconds(progress: (double)completed / total, unitsTotal: total, unitsCompleted: completed, nowUtc: T0.AddSeconds(wallSeconds));

        Assert.NotNull(eta);
        // True remaining ~2.5h (9000s). Assert a tight, stable band — never the 5h-12h swings.
        Assert.InRange(eta!.Value, 6300d, 12600d); // 1.75h .. 3.5h
    }

    [Fact]
    public void Eta_is_not_cratered_by_a_burst_of_noop_completions()
    {
        var est = new JobService.JobEtaEstimator();
        est.Start(T0);

        const int total = 3000;
        var completed = 0;
        for (var i = 0; i < 1000; i++)
        {
            var now = T0.AddSeconds(4500d * (i + 1) / 1000);
            est.ObserveUnitCompletion(i % 5 < 2 ? 10d : 0.05d, now);
            completed++;
        }

        var before = est.EstimateSeconds((double)completed / total, total, completed, T0.AddSeconds(4500));

        // A sudden burst of 40 no-ops (already-processed entities) all finishing within a second.
        for (var i = 0; i < 40; i++)
        {
            completed++;
            est.ObserveUnitCompletion(0.02d, T0.AddSeconds(4500 + i * 0.02));
        }

        var after = est.EstimateSeconds((double)completed / total, total, completed, T0.AddSeconds(4501));

        Assert.NotNull(before);
        Assert.NotNull(after);
        // The no-op burst must not collapse the ETA toward zero; it stays in the same ballpark.
        Assert.True(after!.Value > before!.Value * 0.5,
            $"ETA cratered after no-op burst: {before} -> {after}");
    }

    [Fact]
    public void Eta_is_not_exploded_by_a_burst_of_slow_real_completions()
    {
        var est = new JobService.JobEtaEstimator();
        est.Start(T0);

        const int total = 3000;
        var completed = 0;
        for (var i = 0; i < 1000; i++)
        {
            var now = T0.AddSeconds(4500d * (i + 1) / 1000);
            est.ObserveUnitCompletion(i % 5 < 2 ? 10d : 0.05d, now);
            completed++;
        }

        var before = est.EstimateSeconds((double)completed / total, total, completed, T0.AddSeconds(4500));

        // A burst of unusually slow real items completing close together (out-of-order/async arrival).
        for (var i = 0; i < 8; i++)
        {
            completed++;
            est.ObserveUnitCompletion(60d, T0.AddSeconds(4500 + i * 0.5));
        }

        var after = est.EstimateSeconds((double)completed / total, total, completed, T0.AddSeconds(4504));

        Assert.NotNull(before);
        Assert.NotNull(after);
        // A short slow burst must not blow the ETA up by multiples — it stays within ~2x.
        Assert.True(after!.Value < before!.Value * 2d,
            $"ETA exploded after slow burst: {before} -> {after}");
    }

    [Fact]
    public void Eta_is_null_during_warmup()
    {
        var est = new JobService.JobEtaEstimator();
        est.Start(T0);
        Assert.Null(est.EstimateSeconds(0.01, 3000, 30, T0.AddSeconds(2)));
    }

    [Fact]
    public void Eta_is_zero_when_complete()
    {
        var est = new JobService.JobEtaEstimator();
        est.Start(T0);
        Assert.Equal(0d, est.EstimateSeconds(1.0, 3000, 3000, T0.AddSeconds(100)));
    }
}
