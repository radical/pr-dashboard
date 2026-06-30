using System.Text.Json.Serialization;

// Normalized GitHub Actions run (one workflow execution) used by the pure computer. Lane is filled by
// the producer once it knows the repo's lane config + default branch; until then it defaults to "".
record WorkflowRun(
    string Repository,
    string Workflow,
    string Status,        // queued | in_progress | completed
    string Conclusion,    // success | failure | cancelled | timed_out | startup_failure | ...
    DateTimeOffset CreatedAt,
    long RunId,
    string HtmlUrl,
    string HeadBranch,
    string Event)         // push | pull_request | schedule | ...
{
    public string Lane { get; init; } = "";
}

// A workflow definition (id + display name) from the repo's /actions/workflows list.
record WorkflowDefinition(long Id, string Name);

// 36h pulse for one lane: pass rate, the most-recent run-by-run sequence (true = pass), and whether the
// latest decided run passed (green-at-tip, distinct from the window pass rate).
record WorkflowPulse(
    string Repository,
    string Workflow,
    string Lane,
    int Runs,
    int Passes,
    double PassRate,
    bool GreenAtTip,
    IReadOnlyList<bool> Sequence);

// A lane currently red at tip. LinkedIssue is reserved for the future reaction engine (null in v1).
record FailingWorkflow(
    string Repository,
    string Workflow,
    string Lane,
    DateTimeOffset FailingSince,
    int Streak,
    long LastRunId,
    string LastRunUrl,
    bool LikelyReal,
    string? LinkedIssue);

// 7d trend for one lane, with delta vs the prior 7d and per-day pass-rate buckets.
record WorkflowWeekly(
    string Repository,
    string Workflow,
    string Lane,
    double PassRate,
    double PriorPassRate,
    double Delta,
    IReadOnlyList<double> DailyPassRates);

// An open bot/automated PR with its CI status ("passing" | "failing" | "pending" | "unknown").
record BotPullRequest(
    string Repository,
    int Number,
    string Title,
    string Author,
    string HtmlUrl,
    string CiStatus,
    IReadOnlyList<string> Labels);

// An open issue opened by a tracked bot/automation account.
record BotIssue(
    string Repository,
    int Number,
    string Title,
    string Author,
    string HtmlUrl,
    IReadOnlyList<string> Labels);

// Candidate PR fed to the classifier (decoupled from PullRequestSummary so the classifier is pure).
record CandidatePullRequest(
    string Repository,
    int Number,
    string Title,
    string AuthorLogin,
    bool AuthorIsBot,
    string HtmlUrl,
    string CiStatus,
    IReadOnlyList<string> Labels);

record CiHealthPulseSnapshot(
    IReadOnlyList<WorkflowPulse> Workflows,
    IReadOnlyList<FailingWorkflow> FailingNow,
    IReadOnlyList<BotPullRequest> BotPrs,
    IReadOnlyList<BotIssue> BotIssues,
    DateTimeOffset UpdatedAt);

record CiHealthWeeklySnapshot(
    IReadOnlyList<WorkflowWeekly> Workflows,
    DateTimeOffset UpdatedAt);

// API response; either snapshot may be null before its first cycle has run.
record CiHealthResponse(
    CiHealthPulseSnapshot? Pulse,
    CiHealthWeeklySnapshot? Weekly);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CiHealthPulseSnapshot))]
[JsonSerializable(typeof(CiHealthWeeklySnapshot))]
[JsonSerializable(typeof(CiHealthResponse))]
partial class CiHealthJsonContext : JsonSerializerContext;
