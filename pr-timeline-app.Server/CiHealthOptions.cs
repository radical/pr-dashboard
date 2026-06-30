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
    // Workflows (by cleaned name) that belong to the Main CI section, regardless of trigger — e.g.
    // ["CI", "Outerloop Tests"] for microsoft/aspire (ci.yml on push + the scheduled outerloop run).
    // Empty => every workflow that runs on the tracked branches counts as a main lane.
    public string[] MainWorkflows { get; init; } = [];

    // Push branches that count as main rolling lanes. Exact names or a trailing-'*' glob ("release/*").
    // Empty => the repository's default branch.
    public string[] Branches { get; init; } = [];

    // Scheduled workflows (by cleaned name) that the UI always shows first (not collapsed behind the
    // "show all" toggle), e.g. ["Quarantined Tests", "Deployment E2E Tests"].
    public string[] AlwaysShowScheduled { get; init; } = [];

    // Scheduled workflows (by cleaned name) to hide entirely from the scheduled section.
    public string[] SkipScheduled { get; init; } = [];
}
