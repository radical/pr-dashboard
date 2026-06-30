sealed class CiHealthOptions
{
    public const string SectionName = "CiHealth";

    public bool Enabled { get; init; }

    public bool EnabledInDevelopment { get; init; }

    // Defaults to the public-cache warmup repos when empty (resolved at runtime).
    public string[] Repositories { get; init; } = [];

    // Per-repo lane definition (main CI workflows + branches + scheduled skip list). When a repo has no
    // entry, lanes default to all workflows on its default branch plus every scheduled workflow.
    public Dictionary<string, RepoLaneConfig> Lanes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    // Bot author logins to treat as bot/automation PRs and issues (the "floor"; see aspire-bot-shepherd).
    public string[] BotLogins { get; init; } =
        ["dependabot", "dotnet-maestro", "github-actions", "aspire-repo-bot", "aspire-winget-bot", "aspire-homebrew-bot"];

    // Also surface open issues opened by the tracked bots (not just PRs).
    public bool TrackBotIssues { get; init; }

    public int PulseWindowHours { get; init; } = 36;

    public int WeeklyWindowDays { get; init; } = 7;

    public int PulseRefreshMinutes { get; init; } = 60;

    public int WeeklyRefreshHours { get; init; } = 24;

    // Consecutive failures at/above this are classified "likely real" (vs a single maybe-flaky failure).
    public int StreakThreshold { get; init; } = 3;
}

sealed class RepoLaneConfig
{
    // The repo's "main" CI workflow(s) by (cleaned) name, e.g. ["CI"] for microsoft/aspire's ci.yml.
    // Empty => every workflow that runs on the tracked branches counts as a main lane.
    public string[] MainWorkflows { get; init; } = [];

    // Push branches that count as main rolling lanes. Exact names or a trailing-'*' glob ("release/*").
    // Empty => the repository's default branch.
    public string[] Branches { get; init; } = [];

    // Scheduled workflows (by cleaned name) to hide from the scheduled section.
    public string[] SkipScheduled { get; init; } = [];
}
