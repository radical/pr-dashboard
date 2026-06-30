// LLM triage of the currently-failing workflows. "For now" this is produced by shelling out to the
// Copilot CLI (CiTriageRunner) with a lighter model; a later iteration moves to the Copilot SDK. The
// stored verdict is the contract the dashboard renders and (future) notifications + the reaction engine
// consume, so it stays stable across that swap.
//
//   NeedsAction : true when a human/agent should act (real failure / evicted cache), false for
//                 transient infra blips, external dependency bumps, or noise.
//   Category    : "real-failure" | "flaky" | "infra" | "external-dependency" | "noise"
//   Confidence  : "high" | "medium" | "low"

// What the model returns per failing lane. Repository/Workflow/RunId are echoed back so the runner can
// join the verdict to the FailingWorkflow it was computed for; everything else is the judgment.
record CiTriageItemPayload(
    string Repository,
    string Workflow,
    long RunId,
    bool NeedsAction,
    string Category,
    string Confidence,
    string Summary,
    string SuggestedAction,
    bool SameRootCauseAsPrevious);

record CiTriagePayload(IReadOnlyList<CiTriageItemPayload> Items);

// The stored verdict: the model's judgment joined with the failing-lane identity + computed recurrence.
//   FailingSince/Streak     : carried from the FailingWorkflow (the red-at-tip episode).
//   SameRootCauseAsPrevious : the model's call on whether this is the same failure as the prior verdict.
//   RecurringBuilds         : computed consecutive count of the same root cause (1 = first seen). Feeds
//                             the "failing the same way for N builds" line in the issue body + notifications.
record CiTriageItem(
    string Repository,
    string Workflow,
    string Lane,
    long RunId,
    string RunUrl,
    DateTimeOffset FailingSince,
    int Streak,
    bool NeedsAction,
    string Category,
    string Confidence,
    string Summary,
    string SuggestedAction,
    bool SameRootCauseAsPrevious,
    int RecurringBuilds,
    DateTimeOffset TriagedAt,
    string Model);

// One triage pass over the failing set. Error is non-null when the run could not produce items (CLI
// missing, timed out, or unparseable output) so the page can show why instead of silently empty.
record CiTriageSnapshot(
    IReadOnlyList<CiTriageItem> Items,
    DateTimeOffset UpdatedAt,
    string? Error);

// One past verdict for a lane, kept so a later triage can correlate ("same network issue as last time")
// and so the issue body can say how long a lane has been failing the same way. RunUrl links back to the
// failing run for that analysis.
record CiTriageHistoryEntry(
    long RunId,
    string RunUrl,
    bool NeedsAction,
    string Category,
    string Summary,
    int RecurringBuilds,
    DateTimeOffset At);

// Per-lane bounded history, keyed by "repository\nlane". Persisted alongside the triage snapshot.
record CiTriageHistory(
    Dictionary<string, List<CiTriageHistoryEntry>> ByLane,
    DateTimeOffset UpdatedAt);
