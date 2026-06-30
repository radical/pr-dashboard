using Xunit;

namespace pr_timeline_app.Tests;

public sealed class CiHealthComputerTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 29, 12, 0, 0, TimeSpan.Zero);

    private static WorkflowRun Run(string conclusion, double hoursAgo, long id = 0, string lane = "ci · main") =>
        new(
            Repository: "microsoft/aspire",
            Workflow: "ci",
            Status: "completed",
            Conclusion: conclusion,
            CreatedAt: Now.AddHours(-hoursAgo),
            RunId: id,
            HtmlUrl: $"https://github.com/microsoft/aspire/actions/runs/{id}",
            HeadBranch: "main",
            Event: "push")
        {
            Lane = lane,
            Section = "main",
        };

    [Fact]
    public void Pulse_CountsOnlyCompletedRunsInWindow_AndComputesPassRate()
    {
        var runs = new[]
        {
            Run("success", 1, 1),
            Run("failure", 2, 2),
            Run("success", 3, 3),
            Run("success", 40, 4),                 // outside 36h window -> excluded
            Run("success", 5, 5) with { Status = "in_progress" }, // not completed -> excluded
        };

        var (pulse, _) = CiHealthComputer.ComputePulse(runs, Now, TimeSpan.FromHours(36), streakThreshold: 3);

        var ci = Assert.Single(pulse);
        Assert.Equal(3, ci.Runs);
        Assert.Equal(2, ci.Passes);
        Assert.Equal(2d / 3d, ci.PassRate, 3);
        // Sequence is newest-first: success(1h), failure(2h), success(3h)
        Assert.Equal(new[] { true, false, true }, ci.Sequence);
        Assert.Equal("ci · main", ci.Lane);
        Assert.True(ci.GreenAtTip);                 // newest decided run (1h) passed
    }

    [Fact]
    public void Pulse_GreenAtTipReflectsLatestRun_NotWindowRate()
    {
        // Window has a failure, but the most recent decided run passed -> green at tip.
        var runs = new[] { Run("success", 1, 3), Run("failure", 2, 2), Run("failure", 4, 1) };

        var (pulse, failing) = CiHealthComputer.ComputePulse(runs, Now, TimeSpan.FromHours(36), streakThreshold: 3);

        Assert.True(Assert.Single(pulse).GreenAtTip);
        Assert.Empty(failing);                      // tip is green, so nothing failing-now
    }

    [Fact]
    public void Pulse_SplitsSameWorkflowIntoSeparateLanes()
    {
        // ci.yml on push-to-main and on PR are distinct lanes and must not be blended.
        var runs = new[]
        {
            Run("success", 1, 1, lane: "ci · main"),
            Run("success", 2, 2, lane: "ci · main"),
            Run("failure", 1, 3, lane: "ci · PR"),
            Run("success", 3, 4, lane: "ci · PR"),
        };

        var (pulse, _) = CiHealthComputer.ComputePulse(runs, Now, TimeSpan.FromHours(36), streakThreshold: 3);

        Assert.Equal(2, pulse.Count);
        var main = pulse.Single(p => p.Lane == "ci · main");
        var pr = pulse.Single(p => p.Lane == "ci · PR");
        Assert.Equal(1.0, main.PassRate, 3);
        Assert.Equal(0.5, pr.PassRate, 3);
    }

    [Fact]
    public void FailingNow_ReportsStreakAndRealWhenLatestRunFailed()
    {
        var runs = new[]
        {
            Run("failure", 1, 10),
            Run("failure", 3, 9),
            Run("failure", 5, 8),
            Run("success", 7, 7),
        };

        var (_, failing) = CiHealthComputer.ComputePulse(runs, Now, TimeSpan.FromHours(36), streakThreshold: 3);

        var f = Assert.Single(failing);
        Assert.Equal("ci", f.Workflow);
        Assert.Equal(3, f.Streak);
        Assert.True(f.LikelyReal);                 // 3 >= streakThreshold
        Assert.Equal(10, f.LastRunId);
        Assert.Equal(Now.AddHours(-5), f.FailingSince); // oldest run in the current failing streak
        Assert.Null(f.LinkedIssue);
    }

    [Fact]
    public void FailingNow_SingleFailureIsNotLikelyReal()
    {
        var runs = new[] { Run("failure", 1, 2), Run("success", 3, 1) };

        var (_, failing) = CiHealthComputer.ComputePulse(runs, Now, TimeSpan.FromHours(36), streakThreshold: 3);

        var f = Assert.Single(failing);
        Assert.Equal(1, f.Streak);
        Assert.False(f.LikelyReal);
    }

    [Fact]
    public void FailingNow_EmptyWhenLatestRunPassed()
    {
        var runs = new[] { Run("success", 1, 2), Run("failure", 3, 1) };

        var (_, failing) = CiHealthComputer.ComputePulse(runs, Now, TimeSpan.FromHours(36), streakThreshold: 3);

        Assert.Empty(failing);
    }

    [Fact]
    public void Weekly_ComputesPassRateDeltaVsPriorWeek()
    {
        // Current 7d: 1 pass, 1 fail = 50%. Prior 7d (8-14 days ago): 2 pass = 100%.
        var runs = new[]
        {
            Run("success", 24, 1),
            Run("failure", 48, 2),
            Run("success", 24 + 7 * 24, 3),
            Run("success", 48 + 7 * 24, 4),
        };

        var weekly = CiHealthComputer.ComputeWeekly(runs, Now, windowDays: 7);

        var ci = Assert.Single(weekly);
        Assert.Equal(0.5, ci.PassRate, 3);
        Assert.Equal(1.0, ci.PriorPassRate, 3);
        Assert.Equal(-0.5, ci.Delta, 3);
        Assert.Equal(7, ci.DailyPassRates.Count);
    }
}
