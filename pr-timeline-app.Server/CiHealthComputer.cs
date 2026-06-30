// Pure CI-health math over normalized WorkflowRun records. No I/O, no clock — `now` is passed in
// so the logic is fully testable. A run is "decided" only when completed with a pass/fail
// conclusion; queued/in-progress/cancelled/skipped/neutral runs do not count toward pass rate.
static class CiHealthComputer
{
    private static bool IsPass(WorkflowRun run) =>
        string.Equals(run.Conclusion, "success", StringComparison.OrdinalIgnoreCase);

    // Cap on the per-lane run-by-run history stored on the weekly snapshot — enough to classify a
    // pattern for sparse lanes without bloating the snapshot for high-frequency ones.
    private const int WeeklyRecentRunsCap = 20;

    private static bool IsFail(WorkflowRun run) =>
        run.Conclusion is "failure" or "timed_out" or "startup_failure";

    private static bool IsDecided(WorkflowRun run) =>
        string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase)
        && (IsPass(run) || IsFail(run));

    public static (IReadOnlyList<WorkflowPulse> Workflows, IReadOnlyList<FailingWorkflow> FailingNow) ComputePulse(
        IReadOnlyList<WorkflowRun> runs,
        DateTimeOffset now,
        TimeSpan window,
        int streakThreshold)
    {
        var cutoff = now - window;
        var pulses = new List<WorkflowPulse>();
        var failing = new List<FailingWorkflow>();

        foreach (var group in GroupByLane(runs))
        {
            // Newest-first, decided runs within the window.
            var decided = group.Runs
                .Where(IsDecided)
                .Where(run => run.CreatedAt >= cutoff)
                .OrderByDescending(run => run.CreatedAt)
                .ToList();

            if (decided.Count == 0)
            {
                continue;
            }

            var passes = decided.Count(IsPass);
            pulses.Add(new WorkflowPulse(
                group.Repository,
                group.Workflow,
                group.Lane,
                group.Section,
                group.AlwaysShow,
                decided.Count,
                passes,
                (double)passes / decided.Count,
                GreenAtTip: IsPass(decided[0]),
                decided.Select(run => new RunRef(IsPass(run), run.RunId, run.HtmlUrl)).ToList()));

            // Failing-now: only when the most recent decided run failed.
            if (IsFail(decided[0]))
            {
                var streakRuns = decided.TakeWhile(IsFail).ToList();
                failing.Add(new FailingWorkflow(
                    group.Repository,
                    group.Workflow,
                    group.Lane,
                    group.Section,
                    FailingSince: streakRuns[^1].CreatedAt,
                    Streak: streakRuns.Count,
                    LastRunId: decided[0].RunId,
                    LastRunUrl: decided[0].HtmlUrl,
                    LikelyReal: streakRuns.Count >= streakThreshold,
                    LinkedIssue: null));
            }
        }

        return (pulses, failing);
    }

    public static IReadOnlyList<WorkflowWeekly> ComputeWeekly(
        IReadOnlyList<WorkflowRun> runs,
        DateTimeOffset now,
        int windowDays)
    {
        var window = TimeSpan.FromDays(windowDays);
        var currentCutoff = now - window;
        var priorCutoff = now - window - window;
        var weekly = new List<WorkflowWeekly>();

        foreach (var group in GroupByLane(runs))
        {
            var decided = group.Runs.Where(IsDecided).ToList();

            var current = decided.Where(run => run.CreatedAt >= currentCutoff).ToList();
            var prior = decided
                .Where(run => run.CreatedAt >= priorCutoff && run.CreatedAt < currentCutoff)
                .ToList();

            if (current.Count == 0 && prior.Count == 0)
            {
                continue;
            }

            var passRate = PassRate(current);
            var priorPassRate = PassRate(prior);

            var dailyPassRates = new double[windowDays];
            for (var day = 0; day < windowDays; day++)
            {
                // Bucket 0 = oldest day in the window, windowDays-1 = most recent.
                var dayStart = now - TimeSpan.FromDays(windowDays - day);
                var dayEnd = dayStart + TimeSpan.FromDays(1);
                var dayRuns = current.Where(run => run.CreatedAt >= dayStart && run.CreatedAt < dayEnd).ToList();
                // -1 marks a day with no decided runs so the UI can render it as empty rather than red.
                dailyPassRates[day] = dayRuns.Count == 0 ? -1d : PassRate(dayRuns);
            }

            weekly.Add(new WorkflowWeekly(
                group.Repository,
                group.Workflow,
                group.Lane,
                group.Section,
                group.AlwaysShow,
                passRate,
                priorPassRate,
                passRate - priorPassRate,
                dailyPassRates,
                // Newest-first run-by-run history over the wider weekly fetch, capped so frequent lanes
                // don't bloat the snapshot. Gives sparse lanes enough samples to classify a pattern.
                decided
                    .OrderByDescending(run => run.CreatedAt)
                    .Take(WeeklyRecentRunsCap)
                    .Select(run => new RunRef(IsPass(run), run.RunId, run.HtmlUrl))
                    .ToList()));
        }

        return weekly;
    }

    private static double PassRate(IReadOnlyCollection<WorkflowRun> runs) =>
        runs.Count == 0 ? 0d : (double)runs.Count(IsPass) / runs.Count;

    // Group by lane. Each lane carries a representative cleaned workflow name, section, and always-show
    // flag, all already set on the runs by the producer.
    private static IEnumerable<(string Repository, string Workflow, string Lane, string Section, bool AlwaysShow, List<WorkflowRun> Runs)> GroupByLane(
        IReadOnlyList<WorkflowRun> runs) =>
        runs
            .GroupBy(run => (run.Repository, run.Lane))
            .Select(group => (group.Key.Repository, group.First().Workflow, group.Key.Lane, group.First().Section, group.First().AlwaysShow, group.ToList()));
}
