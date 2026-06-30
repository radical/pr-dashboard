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

    // Pulse cadence is two-tier: main lanes are checked on the fast cadence, scheduled/other lanes on
    // the slow one. The background timer ticks at the fast cadence; each lane carries its own cadence so
    // the UI can show it and (future) notifications can pace per pipeline.
    public int MainPulseRefreshMinutes { get; init; } = 10;

    public int PulseRefreshMinutes { get; init; } = 60;

    public int WeeklyRefreshHours { get; init; } = 24;

    // Per-pipeline cadence override by cleaned workflow name (e.g. a 30-minute scheduled job), in
    // minutes. Falls back to the main/scheduled tier when a lane has no entry.
    public Dictionary<string, int> RefreshCadenceOverrides { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    // The cadence (minutes) for a lane, given its section ("main" => fast tier) and cleaned workflow name.
    public int CadenceMinutesFor(string section, string workflow) =>
        RefreshCadenceOverrides.TryGetValue(workflow, out var minutes)
            ? minutes
            : (string.Equals(section, "main", StringComparison.OrdinalIgnoreCase) ? MainPulseRefreshMinutes : PulseRefreshMinutes);

    // Consecutive failures at/above this are classified "likely real" (vs a single maybe-flaky failure).
    public int StreakThreshold { get; init; } = 3;

    // LLM triage of the failing lanes (shells out to the Copilot CLI for now; see CiTriageRunner).
    public CiTriageOptions Triage { get; init; } = new();
}

sealed class CiTriageOptions
{
    // Off by default; the deployed environment has no Copilot CLI. Enabled in development so the
    // dashboard's "Run triage" action works locally where `copilot` and `gh` are installed + signed in.
    public bool Enabled { get; init; }

    // Run triage automatically after each pulse cycle (cost-gated by last run id). Dev-only in practice
    // since prod has no CLI; the page degrades to the numbers-only failing-now list when off.
    public bool AutoTriage { get; init; }

    // The Copilot CLI executable (resolved on PATH unless an absolute path is given).
    public string Command { get; init; } = "copilot";

    // Lighter model for the cheap "is this real?" triage pass (copilot --model). "auto" lets Copilot pick.
    public string Model { get; init; } = "auto";

    // Cap on how many NEW failing lanes a single triage pass sends to the LLM (reused verdicts for
    // unchanged runs are free and uncapped). Bounds the per-cycle credit/request budget.
    public int MaxLanes { get; init; } = 8;

    // How many past verdicts to keep per lane for recurrence ("same failure for N builds") + issue context.
    public int HistoryPerLane { get; init; } = 12;

    // Hard wall-clock limit for the agentic CLI run; on timeout the process is killed and an error is surfaced.
    public int TimeoutSeconds { get; init; } = 300;
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
