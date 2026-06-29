using System.Text.Json.Serialization;

// Normalized GitHub Actions run (one workflow execution) used by the pure computer.
record WorkflowRun(
    string Repository,
    string Workflow,
    string Status,        // queued | in_progress | completed
    string Conclusion,    // success | failure | cancelled | timed_out | startup_failure | ...
    DateTimeOffset CreatedAt,
    long RunId,
    string HtmlUrl,
    string HeadBranch,
    string Event);        // push | pull_request | schedule | ...

// 36h pulse for one workflow: pass rate + the most-recent run-by-run sequence (true = pass).
record WorkflowPulse(
    string Repository,
    string Workflow,
    int Runs,
    int Passes,
    double PassRate,
    IReadOnlyList<bool> Sequence);

// A workflow currently red. LinkedIssue is reserved for the future reaction engine (null in v1).
record FailingWorkflow(
    string Repository,
    string Workflow,
    DateTimeOffset FailingSince,
    int Streak,
    long LastRunId,
    string LastRunUrl,
    bool LikelyReal,
    string? LinkedIssue);

// 7d trend for one workflow, with delta vs the prior 7d and per-day pass-rate buckets.
record WorkflowWeekly(
    string Repository,
    string Workflow,
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
