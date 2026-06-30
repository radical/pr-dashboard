sealed class CiHealthOptions
{
    public const string SectionName = "CiHealth";

    public bool Enabled { get; init; }

    public bool EnabledInDevelopment { get; init; }

    // Defaults to the public-cache warmup repos when empty (resolved at runtime).
    public string[] Repositories { get; init; } = [];

    // Per-repo lane definition: which push branches are "rolling" lanes and whether to include the PR
    // validation lane. When a repo has no entry, lanes default to push-to-default-branch + PR.
    public Dictionary<string, RepoLaneConfig> Lanes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    // Optional per-repo allowlist of workflow names to follow (applied on top of the lane filter).
    public Dictionary<string, string[]> Workflows { get; init; } = new(StringComparer.OrdinalIgnoreCase);

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
    // Push branches that count as rolling lanes. Exact names or a trailing-'*' glob ("release/*").
    // Empty => the repository's default branch.
    public string[] Branches { get; init; } = [];

    // Whether to include the pull_request validation lane.
    public bool PullRequests { get; init; } = true;
}
