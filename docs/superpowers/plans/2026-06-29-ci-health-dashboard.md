# CI Health Dashboard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a read-only **CI Health** tab to the PR dashboard that shows GitHub Actions health (daily 36h pulse, weekly 7d trend, failing-now workflows, bot/automated PRs) for the watched repos, computed by an in-process background producer and served to everyone from shared Blob snapshots.

**Architecture:** An in-process `BackgroundService` (`CiHealthProducer`) iterates the configured repos on a cadence (pulse hourly, weekly daily), fetches GitHub Actions runs via the existing `GitHubClient` (server/public-cache token), computes health with pure functions, and writes two shared JSON snapshots to the existing `github-cache` Blob container. A public `GET /api/ci-health` endpoint serves the snapshots. A new React `CiHealthView` renders Layout A's five stacked blocks. Read-only; the reaction engine is deferred with a documented seam. Mirrors the existing `GitHubPublicCacheWarmupService` + `NotificationDetectorService` patterns.

**Tech Stack:** ASP.NET Core minimal APIs, C# `BackgroundService`, `System.Text.Json` source-gen, Azure Blob (Azurite locally), xUnit; React + TypeScript + Vite.

**Spec:** `docs/superpowers/specs/2026-06-29-ci-health-dashboard-design.md`

---

## File Structure

**Backend (`pr-timeline-app.Server/`)**
- Create `CiHealthOptions.cs` — config section `CiHealth` (enabled, repos, per-repo workflow allowlist, bot logins, windows, cadences, streak threshold).
- Create `CiHealthModels.cs` — domain records (runs, pulse, weekly, failing, bot PR, snapshots, API response) + `CiHealthJsonContext` source-gen context.
- Create `CiHealthComputer.cs` — pure functions: pulse + failing-now + weekly from runs.
- Create `BotPrClassifier.cs` — pure shepherd-rule classifier.
- Create `CiHealthSnapshotStore.cs` — Blob read/write of the two snapshots.
- Create `CiHealthProducer.cs` — `BackgroundService` cadence + cycle orchestration.
- Create `CiHealthRoutes.cs` — `GET /api/ci-health`.
- Create `CiHealthServiceCollectionExtensions.cs` — DI registration.
- Modify `GitHubModels.cs` — add workflow-run DTOs + register in `GitHubJsonSerializerContext`.
- Modify `GitHubClient.cs` — add `GetWorkflowRunsAsync`.
- Modify `Program.cs` — bind options, register services, map routes.
- Modify `appsettings.json` / `appsettings.Development.json` — `CiHealth` section.

**Frontend (`frontend/src/`)**
- Modify `types.ts` — extend `DashboardMode`; add CI-health response types.
- Create `utils/ciHealth.ts` — fetch helper.
- Create `components/ci/CiHealthView.tsx` — the five blocks.
- Modify `utils/routing.ts` — `parseDashboardMode` accepts `ci-health`.
- Modify `components/MobileNav.tsx` — add nav entry.
- Modify `App.tsx` — mode button, hero copy, conditional render.

**Tests (`pr-timeline-app.Tests/`)**
- Create `CiHealthComputerTests.cs`
- Create `BotPrClassifierTests.cs`

---

## Phase 1 — Backend pure logic (TDD, no I/O)

### Task 1: Config options

**Files:**
- Create: `pr-timeline-app.Server/CiHealthOptions.cs`

- [ ] **Step 1: Create the options class**

```csharp
sealed class CiHealthOptions
{
    public const string SectionName = "CiHealth";

    public bool Enabled { get; init; }

    public bool EnabledInDevelopment { get; init; }

    // Defaults to the public-cache warmup repos when empty (resolved at runtime).
    public string[] Repositories { get; init; } = [];

    // Optional per-repo allowlist of workflow names to follow ("owner/repo" -> ["ci", "build"]).
    // When a repo has no entry, all push/pull_request workflows on its default branch count.
    public Dictionary<string, string[]> Workflows { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    // Bot author logins to treat as bot/automation PRs (the "floor"; see aspire-bot-shepherd).
    public string[] BotLogins { get; init; } =
        ["dependabot", "dotnet-maestro", "github-actions", "aspire-repo-bot", "aspire-winget-bot", "aspire-homebrew-bot"];

    public int PulseWindowHours { get; init; } = 36;

    public int WeeklyWindowDays { get; init; } = 7;

    public int PulseRefreshMinutes { get; init; } = 60;

    public int WeeklyRefreshHours { get; init; } = 24;

    // Consecutive failures at/above this are classified "likely real" (vs a single maybe-flaky failure).
    public int StreakThreshold { get; init; } = 3;
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build pr-timeline-app.slnx --no-restore`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add pr-timeline-app.Server/CiHealthOptions.cs
git commit -m "feat(ci-health): add CiHealth options"
```

---

### Task 2: Domain models + JSON context

**Files:**
- Create: `pr-timeline-app.Server/CiHealthModels.cs`

- [ ] **Step 1: Create the models and source-gen context**

```csharp
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
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build pr-timeline-app.slnx --no-restore`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add pr-timeline-app.Server/CiHealthModels.cs
git commit -m "feat(ci-health): add domain models and JSON context"
```

---

### Task 3: Pulse + failing-now computation (TDD)

**Files:**
- Create: `pr-timeline-app.Server/CiHealthComputer.cs`
- Test: `pr-timeline-app.Tests/CiHealthComputerTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Xunit;

namespace pr_timeline_app.Tests;

public sealed class CiHealthComputerTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 29, 12, 0, 0, TimeSpan.Zero);

    private static WorkflowRun Run(string conclusion, double hoursAgo, long id = 0) =>
        new(
            Repository: "microsoft/aspire",
            Workflow: "ci",
            Status: "completed",
            Conclusion: conclusion,
            CreatedAt: Now.AddHours(-hoursAgo),
            RunId: id,
            HtmlUrl: $"https://github.com/microsoft/aspire/actions/runs/{id}",
            HeadBranch: "main",
            Event: "push");

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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test pr-timeline-app.slnx --filter FullyQualifiedName~CiHealthComputerTests`
Expected: FAIL — `CiHealthComputer` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
// Pure CI-health math over normalized WorkflowRun records. No I/O, no clock — `now` is passed in
// so the logic is fully testable. A run is "decided" only when completed with a pass/fail
// conclusion; queued/in-progress/cancelled/skipped/neutral runs do not count toward pass rate.
static class CiHealthComputer
{
    private static bool IsPass(WorkflowRun run) =>
        string.Equals(run.Conclusion, "success", StringComparison.OrdinalIgnoreCase);

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

        foreach (var group in GroupByWorkflow(runs))
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
                decided.Count,
                passes,
                (double)passes / decided.Count,
                decided.Select(IsPass).ToList()));

            // Failing-now: only when the most recent decided run failed.
            if (IsFail(decided[0]))
            {
                var streakRuns = decided.TakeWhile(IsFail).ToList();
                failing.Add(new FailingWorkflow(
                    group.Repository,
                    group.Workflow,
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

        foreach (var group in GroupByWorkflow(runs))
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
                dailyPassRates[day] = PassRate(dayRuns);
            }

            weekly.Add(new WorkflowWeekly(
                group.Repository,
                group.Workflow,
                passRate,
                priorPassRate,
                passRate - priorPassRate,
                dailyPassRates));
        }

        return weekly;
    }

    private static double PassRate(IReadOnlyCollection<WorkflowRun> runs) =>
        runs.Count == 0 ? 0d : (double)runs.Count(IsPass) / runs.Count;

    private static IEnumerable<(string Repository, string Workflow, List<WorkflowRun> Runs)> GroupByWorkflow(
        IReadOnlyList<WorkflowRun> runs) =>
        runs
            .GroupBy(run => (run.Repository, run.Workflow))
            .Select(group => (group.Key.Repository, group.Key.Workflow, group.ToList()));
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test pr-timeline-app.slnx --filter FullyQualifiedName~CiHealthComputerTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add pr-timeline-app.Server/CiHealthComputer.cs pr-timeline-app.Tests/CiHealthComputerTests.cs
git commit -m "feat(ci-health): compute pulse, failing-now, and weekly trend"
```

---

### Task 4: Bot/automated PR classifier (TDD)

**Files:**
- Create: `pr-timeline-app.Server/BotPrClassifier.cs`
- Test: `pr-timeline-app.Tests/BotPrClassifierTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Xunit;

namespace pr_timeline_app.Tests;

public sealed class BotPrClassifierTests
{
    private static readonly string[] Allowlist =
        ["dependabot", "dotnet-maestro", "github-actions", "aspire-repo-bot"];

    private static CandidatePullRequest Pr(
        int number, string login, bool isBot = false, string[]? labels = null) =>
        new(
            Repository: "microsoft/aspire",
            Number: number,
            Title: $"PR {number}",
            AuthorLogin: login,
            AuthorIsBot: isBot,
            HtmlUrl: $"https://github.com/microsoft/aspire/pull/{number}",
            CiStatus: "passing",
            Labels: labels ?? []);

    [Fact]
    public void IncludesAllowlistedAuthors_StrippingAppPrefix()
    {
        var prs = new[] { Pr(1, "app/dependabot"), Pr(2, "octocat") };

        var result = BotPrClassifier.Classify(prs, Allowlist);

        var bot = Assert.Single(result);
        Assert.Equal(1, bot.Number);
        Assert.Equal("dependabot", bot.Author); // app/ prefix stripped
    }

    [Fact]
    public void IncludesIsBotAuthorsNotInAllowlist()
    {
        var prs = new[] { Pr(1, "some-random-bot", isBot: true) };

        var result = BotPrClassifier.Classify(prs, Allowlist);

        Assert.Single(result);
    }

    [Fact]
    public void AlwaysDropsCopilotApps_EvenWhenIsBot()
    {
        var prs = new[]
        {
            Pr(1, "app/copilot-swe-agent", isBot: true),
            Pr(2, "app/copilot-pull-request-reviewer", isBot: true),
        };

        Assert.Empty(BotPrClassifier.Classify(prs, Allowlist));
    }

    [Fact]
    public void DropsNoMergeAndAutomatedLabels()
    {
        var prs = new[]
        {
            Pr(1, "dependabot", labels: ["NO-MERGE"]),
            Pr(2, "dependabot", labels: ["automated"]),
            Pr(3, "dependabot"),
        };

        var result = BotPrClassifier.Classify(prs, Allowlist);

        var bot = Assert.Single(result);
        Assert.Equal(3, bot.Number);
    }

    [Fact]
    public void UnionsInAutomationBrokenRegardlessOfAuthor()
    {
        var prs = new[] { Pr(1, "human-dev", labels: ["automation-broken"]) };

        var result = BotPrClassifier.Classify(prs, Allowlist);

        Assert.Single(result);
    }

    [Fact]
    public void AutomationBrokenStillDroppedWhenNoMerge()
    {
        var prs = new[] { Pr(1, "human-dev", labels: ["automation-broken", "NO-MERGE"]) };

        Assert.Empty(BotPrClassifier.Classify(prs, Allowlist));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test pr-timeline-app.slnx --filter FullyQualifiedName~BotPrClassifierTests`
Expected: FAIL — `BotPrClassifier` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
// Classifies which open PRs are bot/automation-opened, per aspire-bot-shepherd.prompt.txt:
//   - strip any "app/" prefix from the author login
//   - include if the login is in the allowlist (the floor) OR the author is flagged is_bot
//   - ALWAYS drop the two Copilot apps after that test
//   - drop PRs labeled NO-MERGE (never in scope) or "automated" (the orchestrator's own artifacts)
//   - union in any PR labeled "automation-broken", regardless of author
// NB (v1): the existing PullRequestSummary does not expose is_bot, so callers pass AuthorIsBot=false;
// the allowlist is the floor and catches the User-account bots that report is_bot:false.
static class BotPrClassifier
{
    private static readonly string[] CopilotApps =
        ["copilot-swe-agent", "copilot-pull-request-reviewer"];

    public static IReadOnlyList<BotPullRequest> Classify(
        IReadOnlyList<CandidatePullRequest> pullRequests,
        IReadOnlyCollection<string> allowlist)
    {
        var allow = new HashSet<string>(allowlist, StringComparer.OrdinalIgnoreCase);
        var result = new List<BotPullRequest>();

        foreach (var pr in pullRequests)
        {
            var labels = new HashSet<string>(pr.Labels, StringComparer.OrdinalIgnoreCase);

            // NO-MERGE is an absolute exclusion, even for automation-broken.
            if (labels.Contains("NO-MERGE"))
            {
                continue;
            }

            var login = StripAppPrefix(pr.AuthorLogin);
            var isCopilot = CopilotApps.Contains(login, StringComparer.OrdinalIgnoreCase);

            var isBotAuthor = !isCopilot && (allow.Contains(login) || pr.AuthorIsBot);
            var automationBroken = labels.Contains("automation-broken");

            if (!isBotAuthor && !automationBroken)
            {
                continue;
            }

            // The orchestrator stamps "automated" on its own tracking artifacts; never re-track those.
            if (labels.Contains("automated"))
            {
                continue;
            }

            result.Add(new BotPullRequest(
                pr.Repository,
                pr.Number,
                pr.Title,
                login,
                pr.HtmlUrl,
                pr.CiStatus,
                pr.Labels));
        }

        return result;
    }

    private static string StripAppPrefix(string login) =>
        login.StartsWith("app/", StringComparison.OrdinalIgnoreCase) ? login[4..] : login;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test pr-timeline-app.slnx --filter FullyQualifiedName~BotPrClassifierTests`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add pr-timeline-app.Server/BotPrClassifier.cs pr-timeline-app.Tests/BotPrClassifierTests.cs
git commit -m "feat(ci-health): classify bot and automated PRs"
```

---

## Phase 2 — Backend I/O and wiring

### Task 5: Fetch GitHub Actions runs

**Files:**
- Modify: `pr-timeline-app.Server/GitHubModels.cs` (add DTOs near the other DTOs, ~line 660, and register them in the `[JsonSerializable]` list ~line 665)
- Modify: `pr-timeline-app.Server/GitHubClient.cs` (add a public method)

- [ ] **Step 1: Add the run DTOs in `GitHubModels.cs`**

Insert immediately before the `[JsonSerializable(typeof(GitHubActorDto))]` attribute block (~line 665):

```csharp
sealed class GitHubWorkflowRunsResponseDto
{
    [System.Text.Json.Serialization.JsonPropertyName("workflow_runs")]
    public GitHubWorkflowRunDto[] WorkflowRuns { get; init; } = [];
}

sealed class GitHubWorkflowRunDto
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public long Id { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string? Name { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public string? Status { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("conclusion")]
    public string? Conclusion { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("html_url")]
    public string? HtmlUrl { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("head_branch")]
    public string? HeadBranch { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("event")]
    public string? Event { get; init; }
}
```

- [ ] **Step 2: Register the DTOs in `GitHubJsonSerializerContext`**

Add these two lines into the `[JsonSerializable(...)]` attribute list (alongside the others ~line 665):

```csharp
[JsonSerializable(typeof(GitHubWorkflowRunsResponseDto))]
[JsonSerializable(typeof(GitHubWorkflowRunDto))]
```

- [ ] **Step 3: Add `GetWorkflowRunsAsync` to `GitHubClient.cs`**

Add this method inside the `GitHubClient` class (e.g. after `GetPullRequestsAsync`, ~line 520). It reuses the existing `SendGitHubRequestAsync` (server/public-cache token, redirect-safe) and `ReadGitHubJsonAsync` helpers:

```csharp
// Fetches recent GitHub Actions runs for a repo, newest-first, stopping once runs predate `since`
// or a page cap is hit. Uses the public-cache (server) token so it works for logged-out viewers.
public async Task<IReadOnlyList<WorkflowRun>> GetWorkflowRunsAsync(
    RepositoryName repositoryName,
    DateTimeOffset since,
    CancellationToken cancellationToken)
{
    const int maxPages = 5;
    const int perPage = 100;
    var runs = new List<WorkflowRun>();

    for (var page = 1; page <= maxPages; page++)
    {
        var url = $"repos/{repositoryName.Owner}/{repositoryName.Name}/actions/runs?per_page={perPage}&page={page}";
        using var response = await SendGitHubRequestAsync(url, GitHubRequestAuthorization.PublicCacheToken, cancellationToken);
        var payload = await ReadGitHubJsonAsync(
            response,
            GitHubJsonSerializerContext.Default.GitHubWorkflowRunsResponseDto,
            cancellationToken);

        if (payload.WorkflowRuns.Length == 0)
        {
            break;
        }

        var reachedOlderThanSince = false;
        foreach (var dto in payload.WorkflowRuns)
        {
            if (dto.CreatedAt < since)
            {
                reachedOlderThanSince = true;
                continue;
            }

            runs.Add(new WorkflowRun(
                repositoryName.ToString(),
                dto.Name ?? "(unnamed)",
                dto.Status ?? "",
                dto.Conclusion ?? "",
                dto.CreatedAt,
                dto.Id,
                dto.HtmlUrl ?? "",
                dto.HeadBranch ?? "",
                dto.Event ?? ""));
        }

        if (reachedOlderThanSince || payload.WorkflowRuns.Length < perPage)
        {
            break;
        }
    }

    return runs;
}
```

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build pr-timeline-app.slnx --no-restore`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add pr-timeline-app.Server/GitHubModels.cs pr-timeline-app.Server/GitHubClient.cs
git commit -m "feat(ci-health): fetch GitHub Actions workflow runs"
```

---

### Task 6: Snapshot store (Blob)

**Files:**
- Create: `pr-timeline-app.Server/CiHealthSnapshotStore.cs`

- [ ] **Step 1: Create the store**

```csharp
using Azure;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;

// Reads/writes the two shared CI-health snapshots in the existing github-cache Blob container.
// Server-written only; served to every visitor. Reuses GitHubPublicCacheStore.ConnectionName so no
// new Aspire container/resource is needed. A missing snapshot returns null (first-run / not yet computed).
sealed class CiHealthSnapshotStore(
    [FromKeyedServices(GitHubPublicCacheStore.ConnectionName)] BlobContainerClient container)
{
    private const string PulseBlobName = "ci-health/pulse.json";
    private const string WeeklyBlobName = "ci-health/weekly.json";

    public Task<CiHealthPulseSnapshot?> ReadPulseAsync(CancellationToken cancellationToken) =>
        ReadAsync(PulseBlobName, CiHealthJsonContext.Default.CiHealthPulseSnapshot, cancellationToken);

    public Task WritePulseAsync(CiHealthPulseSnapshot snapshot, CancellationToken cancellationToken) =>
        WriteAsync(PulseBlobName, snapshot, CiHealthJsonContext.Default.CiHealthPulseSnapshot, cancellationToken);

    public Task<CiHealthWeeklySnapshot?> ReadWeeklyAsync(CancellationToken cancellationToken) =>
        ReadAsync(WeeklyBlobName, CiHealthJsonContext.Default.CiHealthWeeklySnapshot, cancellationToken);

    public Task WriteWeeklyAsync(CiHealthWeeklySnapshot snapshot, CancellationToken cancellationToken) =>
        WriteAsync(WeeklyBlobName, snapshot, CiHealthJsonContext.Default.CiHealthWeeklySnapshot, cancellationToken);

    private async Task<T?> ReadAsync<T>(
        string blobName,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken) where T : class
    {
        try
        {
            var blob = container.GetBlobClient(blobName);
            if (!await blob.ExistsAsync(cancellationToken))
            {
                return null;
            }

            var download = await blob.DownloadContentAsync(cancellationToken);
            return download.Value.Content.ToObjectFromJson(typeInfo);
        }
        catch (RequestFailedException)
        {
            return null;
        }
    }

    private async Task WriteAsync<T>(
        string blobName,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        var data = BinaryData.FromObjectAsJson(value, typeInfo);
        await container.GetBlobClient(blobName).UploadAsync(data, overwrite: true, cancellationToken);
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build pr-timeline-app.slnx --no-restore`
Expected: Build succeeded.

> Note: if `BinaryData.ToObjectFromJson` / `FromObjectAsJson` overloads taking a `JsonTypeInfo<T>` are unavailable, fall back to `System.Text.Json.JsonSerializer.Deserialize(stream, typeInfo)` and `JsonSerializer.SerializeToUtf8Bytes(value, typeInfo)` — both accept the source-gen `JsonTypeInfo<T>`.

- [ ] **Step 3: Commit**

```bash
git add pr-timeline-app.Server/CiHealthSnapshotStore.cs
git commit -m "feat(ci-health): add Blob snapshot store"
```

---

### Task 7: Background producer

**Files:**
- Create: `pr-timeline-app.Server/CiHealthProducer.cs`

- [ ] **Step 1: Create the producer**

```csharp
using Microsoft.Extensions.Options;

// Cadenced producer: pulse (36h) recomputed hourly, weekly (7d) recomputed daily. Mirrors
// GitHubPublicCacheWarmupService (repo loop, server token via GitHubClient, rate-limit tolerant) and
// NotificationDetectorService (PeriodicTimer + internal cycle methods exposed for tests). Runs under
// the AppHost's single-replica pinning. The reaction engine (open issue / dispatch agent / drive PR)
// is intentionally NOT here in v1; when it lands, extract this into a dedicated worker project.
sealed class CiHealthProducer(
    IServiceScopeFactory scopeFactory,
    CiHealthSnapshotStore store,
    IOptions<CiHealthOptions> options,
    IOptions<GitHubCacheWarmupOptions> warmupOptions,
    IHostEnvironment environment,
    TimeProvider timeProvider,
    ILogger<CiHealthProducer> logger) : BackgroundService
{
    private static readonly TimeSpan s_startupDelay = TimeSpan.FromSeconds(45);
    private DateTimeOffset _lastWeekly = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsEnabled())
        {
            logger.LogInformation("CI health producer is disabled (set CiHealth:Enabled).");
            return;
        }

        try
        {
            await Task.Delay(s_startupDelay, timeProvider, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await SafeRunAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.PulseRefreshMinutes), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SafeRunAsync(stoppingToken);
        }
    }

    private bool IsEnabled() =>
        options.Value.Enabled && (!environment.IsDevelopment() || options.Value.EnabledInDevelopment);

    private async Task SafeRunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunPulseCycleAsync(cancellationToken);

            var now = timeProvider.GetUtcNow();
            if (now - _lastWeekly >= TimeSpan.FromHours(options.Value.WeeklyRefreshHours))
            {
                await RunWeeklyCycleAsync(cancellationToken);
                _lastWeekly = now;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "CI health producer cycle failed.");
        }
    }

    // Exposed internal so tests can drive a single cycle with a real/fake GitHubClient + store.
    internal async Task RunPulseCycleAsync(CancellationToken cancellationToken)
    {
        var config = options.Value;
        var now = timeProvider.GetUtcNow();
        var window = TimeSpan.FromHours(config.PulseWindowHours);

        var pulses = new List<WorkflowPulse>();
        var failing = new List<FailingWorkflow>();
        var botPrs = new List<BotPullRequest>();

        foreach (var repository in ResolveRepositories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var scope = scopeFactory.CreateScope();
            var gitHub = scope.ServiceProvider.GetRequiredService<GitHubClient>();

            try
            {
                var runs = FilterWorkflows(repository, await gitHub.GetWorkflowRunsAsync(repository, now - window, cancellationToken));
                var (repoPulses, repoFailing) = CiHealthComputer.ComputePulse(runs, now, window, config.StreakThreshold);
                pulses.AddRange(repoPulses);
                failing.AddRange(repoFailing);

                var openPrs = await gitHub.GetPullRequestsAsync(repository, "open", cancellationToken);
                botPrs.AddRange(BotPrClassifier.Classify(ToCandidates(repository, openPrs), config.BotLogins));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "CI health pulse failed for {Repository}.", repository);
            }
        }

        await store.WritePulseAsync(new CiHealthPulseSnapshot(pulses, failing, botPrs, now), cancellationToken);
        logger.LogInformation("CI health pulse written: {Workflows} workflows, {Failing} failing, {BotPrs} bot PRs.",
            pulses.Count, failing.Count, botPrs.Count);
    }

    internal async Task RunWeeklyCycleAsync(CancellationToken cancellationToken)
    {
        var config = options.Value;
        var now = timeProvider.GetUtcNow();
        var since = now - TimeSpan.FromDays(config.WeeklyWindowDays * 2);

        var weekly = new List<WorkflowWeekly>();
        foreach (var repository in ResolveRepositories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var scope = scopeFactory.CreateScope();
            var gitHub = scope.ServiceProvider.GetRequiredService<GitHubClient>();

            try
            {
                var runs = FilterWorkflows(repository, await gitHub.GetWorkflowRunsAsync(repository, since, cancellationToken));
                weekly.AddRange(CiHealthComputer.ComputeWeekly(runs, now, config.WeeklyWindowDays));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "CI health weekly failed for {Repository}.", repository);
            }
        }

        await store.WriteWeeklyAsync(new CiHealthWeeklySnapshot(weekly, now), cancellationToken);
        logger.LogInformation("CI health weekly written: {Workflows} workflows.", weekly.Count);
    }

    private IEnumerable<RepositoryName> ResolveRepositories()
    {
        var configured = options.Value.Repositories.Length > 0
            ? options.Value.Repositories
            : warmupOptions.Value.Repositories;

        foreach (var repository in configured)
        {
            if (RepositoryName.TryParse(repository, out var repositoryName))
            {
                yield return repositoryName;
            }
        }
    }

    private IReadOnlyList<WorkflowRun> FilterWorkflows(RepositoryName repository, IReadOnlyList<WorkflowRun> runs)
    {
        if (!options.Value.Workflows.TryGetValue(repository.ToString(), out var allowed) || allowed.Length == 0)
        {
            // No explicit allowlist: count workflows triggered by push / pull_request (skip schedule etc.).
            return runs.Where(run => run.Event is "push" or "pull_request").ToList();
        }

        var set = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
        return runs.Where(run => set.Contains(run.Workflow)).ToList();
    }

    private static IReadOnlyList<CandidatePullRequest> ToCandidates(
        RepositoryName repository,
        IReadOnlyList<PullRequestSummary> pullRequests) =>
        pullRequests
            .Select(pr => new CandidatePullRequest(
                repository.ToString(),
                pr.Number,
                pr.Title,
                pr.Author,
                AuthorIsBot: false, // PullRequestSummary does not expose is_bot; allowlist is the floor.
                pr.HtmlUrl,
                MapCiStatus(pr.Checks),
                pr.Labels))
            .ToList();

    private static string MapCiStatus(ChecksStatus checks) =>
        checks.FailureCount > 0 ? "failing"
        : checks.PendingCount > 0 ? "pending"
        : checks.State is "success" ? "passing"
        : checks.State is "unknown" ? "unknown"
        : checks.State;
}
```

- [ ] **Step 2: Verify `GetPullRequestsAsync` signature matches the call**

Run: `grep -n "public async Task<IReadOnlyList<PullRequestSummary>> GetPullRequestsAsync" pr-timeline-app.Server/GitHubClient.cs`
Expected: a method taking `(RepositoryName, string state, CancellationToken)`. If the parameter list differs, adjust the call in `RunPulseCycleAsync` to match (e.g. pass the state argument the existing method expects).

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build pr-timeline-app.slnx --no-restore`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add pr-timeline-app.Server/CiHealthProducer.cs
git commit -m "feat(ci-health): add cadenced background producer"
```

---

### Task 8: API endpoint + DI registration + config

**Files:**
- Create: `pr-timeline-app.Server/CiHealthRoutes.cs`
- Create: `pr-timeline-app.Server/CiHealthServiceCollectionExtensions.cs`
- Modify: `pr-timeline-app.Server/Program.cs`
- Modify: `pr-timeline-app.Server/appsettings.json`
- Modify: `pr-timeline-app.Server/appsettings.Development.json`

- [ ] **Step 1: Create the route**

```csharp
public static class CiHealthRoutes
{
    public static IEndpointRouteBuilder MapCiHealthRoutes(this IEndpointRouteBuilder endpoints)
    {
        // Public by design: serves the shared, server-computed snapshots to every visitor.
        endpoints.MapGet("/api/ci-health", async (
            CiHealthSnapshotStore store,
            CancellationToken cancellationToken) =>
        {
            var pulse = await store.ReadPulseAsync(cancellationToken);
            var weekly = await store.ReadWeeklyAsync(cancellationToken);
            return Results.Ok(new CiHealthResponse(pulse, weekly));
        });

        return endpoints;
    }
}
```

- [ ] **Step 2: Create the DI extension**

```csharp
public static class CiHealthServiceCollectionExtensions
{
    public static IServiceCollection AddCiHealthServices(this IServiceCollection services)
    {
        services.AddSingleton<CiHealthSnapshotStore>();
        services.AddHostedService<CiHealthProducer>();
        return services;
    }
}
```

- [ ] **Step 3: Wire into `Program.cs`**

After the existing `builder.Services.Configure<GitHubReviewPolicyOptions>(...)` block (~line 14), add:

```csharp
builder.Services.Configure<CiHealthOptions>(
    builder.Configuration.GetSection(CiHealthOptions.SectionName));
```

After `builder.Services.AddNotificationServices();` (~line 16), add:

```csharp
builder.Services.AddCiHealthServices();
```

After `app.MapNotificationRoutes();` (~line 30), add:

```csharp
app.MapCiHealthRoutes();
```

- [ ] **Step 4: Add config to `appsettings.json`**

Add a top-level `"CiHealth"` section (sibling of the existing sections):

```json
"CiHealth": {
  "Enabled": true,
  "EnabledInDevelopment": false,
  "Repositories": [],
  "Workflows": {},
  "PulseWindowHours": 36,
  "WeeklyWindowDays": 7,
  "PulseRefreshMinutes": 60,
  "WeeklyRefreshHours": 24,
  "StreakThreshold": 3
}
```

- [ ] **Step 5: Enable in development in `appsettings.Development.json`**

Add (so local `aspire start` exercises it against the public cache token if present):

```json
"CiHealth": {
  "EnabledInDevelopment": true
}
```

- [ ] **Step 6: Build and run the full backend test suite**

Run: `dotnet build pr-timeline-app.slnx --no-restore && dotnet test pr-timeline-app.slnx --no-build`
Expected: Build succeeded; all tests pass.

- [ ] **Step 7: Commit**

```bash
git add pr-timeline-app.Server/CiHealthRoutes.cs pr-timeline-app.Server/CiHealthServiceCollectionExtensions.cs pr-timeline-app.Server/Program.cs pr-timeline-app.Server/appsettings.json pr-timeline-app.Server/appsettings.Development.json
git commit -m "feat(ci-health): add /api/ci-health endpoint, DI, and config"
```

---

## Phase 3 — Frontend

### Task 9: Types + fetch helper

**Files:**
- Modify: `frontend/src/types.ts` (the `DashboardMode` union ~line 20)
- Create: `frontend/src/utils/ciHealth.ts`

- [ ] **Step 1: Extend `DashboardMode` and add response types in `types.ts`**

Change:

```typescript
export type DashboardMode = 'review' | 'ship' | 'issues';
```

to:

```typescript
export type DashboardMode = 'review' | 'ship' | 'issues' | 'ci-health';
```

Then append these types to the end of `types.ts`:

```typescript
export type WorkflowPulse = {
  repository: string;
  workflow: string;
  runs: number;
  passes: number;
  passRate: number;
  sequence: boolean[];
};

export type FailingWorkflow = {
  repository: string;
  workflow: string;
  failingSince: string;
  streak: number;
  lastRunId: number;
  lastRunUrl: string;
  likelyReal: boolean;
  linkedIssue: string | null;
};

export type WorkflowWeekly = {
  repository: string;
  workflow: string;
  passRate: number;
  priorPassRate: number;
  delta: number;
  dailyPassRates: number[];
};

export type BotPullRequest = {
  repository: string;
  number: number;
  title: string;
  author: string;
  htmlUrl: string;
  ciStatus: string;
  labels: string[];
};

export type CiHealthPulseSnapshot = {
  workflows: WorkflowPulse[];
  failingNow: FailingWorkflow[];
  botPrs: BotPullRequest[];
  updatedAt: string;
};

export type CiHealthWeeklySnapshot = {
  workflows: WorkflowWeekly[];
  updatedAt: string;
};

export type CiHealthResponse = {
  pulse: CiHealthPulseSnapshot | null;
  weekly: CiHealthWeeklySnapshot | null;
};
```

- [ ] **Step 2: Create the fetch helper**

```typescript
import type { CiHealthResponse } from '../types';

export async function fetchCiHealth(signal?: AbortSignal): Promise<CiHealthResponse> {
  const response = await fetch('/api/ci-health', { signal });
  if (!response.ok) {
    throw new Error(`CI health request failed: ${response.status}`);
  }

  return (await response.json()) as CiHealthResponse;
}
```

> Note: if `frontend/src/utils/http.ts` exposes a `readJson(response)` helper used elsewhere, prefer it for consistency: `import { readJson } from './http';` then `return readJson<CiHealthResponse>(response);`. Confirm its signature before switching.

- [ ] **Step 3: Type-check and lint**

Run: `npm --prefix frontend run lint`
Expected: no errors.

- [ ] **Step 4: Commit**

```bash
git add frontend/src/types.ts frontend/src/utils/ciHealth.ts
git commit -m "feat(ci-health): add frontend types and fetch helper"
```

---

### Task 10: CiHealthView component

**Files:**
- Create: `frontend/src/components/ci/CiHealthView.tsx`
- Modify: `frontend/src/App.css` (append styles)

- [ ] **Step 1: Create the component (Layout A — five stacked blocks)**

```tsx
import { useEffect, useState } from 'react';
import type { CiHealthResponse } from '../../types';
import { fetchCiHealth } from '../../utils/ciHealth';

function percent(value: number): string {
  return `${Math.round(value * 100)}%`;
}

function relativeTime(iso: string): string {
  const then = new Date(iso).getTime();
  const minutes = Math.max(0, Math.round((Date.now() - then) / 60000));
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  return `${Math.round(hours / 24)}d ago`;
}

function CiHealthView() {
  const [data, setData] = useState<CiHealthResponse | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    fetchCiHealth(controller.signal)
      .then(setData)
      .catch((err: unknown) => {
        if (!controller.signal.aborted) {
          setError(err instanceof Error ? err.message : 'Failed to load CI health');
        }
      });
    return () => controller.abort();
  }, []);

  if (error) {
    return <div className="ci-health-empty">Could not load CI health: {error}</div>;
  }

  if (!data) {
    return <div className="ci-health-empty">Loading CI health…</div>;
  }

  const { pulse, weekly } = data;
  const failingCount = pulse?.failingNow.length ?? 0;

  return (
    <div className="ci-health">
      {/* 1. Status strip */}
      <section className="ci-strip">
        <strong>{failingCount === 0 ? '🟢 CI: healthy' : `🟡 CI: ${failingCount} workflow(s) failing`}</strong>
        <span className="ci-strip-meta">
          {pulse ? `pulse ${relativeTime(pulse.updatedAt)}` : 'pulse pending'} ·{' '}
          {weekly ? `weekly ${relativeTime(weekly.updatedAt)}` : 'weekly pending'}
        </span>
      </section>

      {/* 2. Failing workflows now */}
      <section className="ci-block">
        <h3>⚠️ Failing workflows now</h3>
        {failingCount === 0 ? (
          <p className="ci-empty">Nothing on fire.</p>
        ) : (
          <table className="ci-table">
            <thead>
              <tr><th>Repo</th><th>Workflow</th><th>Streak</th><th>Signal</th><th>Run</th><th /></tr>
            </thead>
            <tbody>
              {pulse!.failingNow.map((f) => (
                <tr key={`${f.repository}/${f.workflow}`}>
                  <td>{f.repository}</td>
                  <td>{f.workflow}</td>
                  <td>{f.streak}</td>
                  <td>{f.likelyReal ? 'likely real' : 'maybe flaky'}</td>
                  <td><a href={f.lastRunUrl} target="_blank" rel="noreferrer">run ↗</a></td>
                  <td><span className="ci-ghost-action" title="Coming later">Launch fix · later</span></td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      {/* 3. Daily pulse (36h) */}
      <section className="ci-block">
        <h3>📈 Daily pulse — 36h pass rate</h3>
        <table className="ci-table">
          <thead><tr><th>Repo / workflow</th><th>Runs</th><th>Pass</th><th>Recent</th></tr></thead>
          <tbody>
            {(pulse?.workflows ?? []).map((w) => (
              <tr key={`${w.repository}/${w.workflow}`}>
                <td>{w.repository} · {w.workflow}</td>
                <td>{w.runs}</td>
                <td>{percent(w.passRate)}</td>
                <td>{w.sequence.map((pass) => (pass ? '🟩' : '🟥')).join('')}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>

      {/* 4. Weekly health (7d) */}
      <section className="ci-block">
        <h3>🩺 Weekly health — 7d trend</h3>
        <table className="ci-table">
          <thead><tr><th>Repo / workflow</th><th>7d pass</th><th>vs prior</th></tr></thead>
          <tbody>
            {(weekly?.workflows ?? []).map((w) => (
              <tr key={`${w.repository}/${w.workflow}`}>
                <td>{w.repository} · {w.workflow}</td>
                <td>{percent(w.passRate)}</td>
                <td>{w.delta >= 0 ? `▲ +${percent(w.delta)}` : `▼ ${percent(Math.abs(w.delta))}`}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>

      {/* 5. Bot / automated PRs */}
      <section className="ci-block">
        <h3>🤖 Bot / automated PRs</h3>
        {(pulse?.botPrs.length ?? 0) === 0 ? (
          <p className="ci-empty">No open bot/automated PRs.</p>
        ) : (
          <table className="ci-table">
            <thead><tr><th>PR</th><th>Repo</th><th>Author</th><th>CI</th></tr></thead>
            <tbody>
              {pulse!.botPrs.map((pr) => (
                <tr key={`${pr.repository}#${pr.number}`}>
                  <td><a href={pr.htmlUrl} target="_blank" rel="noreferrer">#{pr.number} ↗</a></td>
                  <td>{pr.repository}</td>
                  <td>{pr.author}</td>
                  <td>{pr.ciStatus}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
    </div>
  );
}

export default CiHealthView;
```

- [ ] **Step 2: Append minimal styles to `frontend/src/App.css`**

```css
.ci-health { display: flex; flex-direction: column; gap: 1rem; }
.ci-strip { display: flex; justify-content: space-between; align-items: center;
  padding: 0.75rem 1rem; border: 1px solid var(--border, #30363d); border-radius: 8px; }
.ci-strip-meta { font-size: 0.8rem; opacity: 0.7; }
.ci-block { border: 1px solid var(--border, #30363d); border-radius: 8px; padding: 0.75rem 1rem; }
.ci-block h3 { margin: 0 0 0.5rem; font-size: 0.95rem; }
.ci-table { width: 100%; border-collapse: collapse; font-size: 0.85rem; }
.ci-table th, .ci-table td { text-align: left; padding: 0.3rem 0.4rem; border-top: 1px solid var(--border, #30363d); }
.ci-table th { opacity: 0.7; font-weight: 500; border-top: none; }
.ci-ghost-action { opacity: 0.35; border: 1px solid var(--border, #30363d); border-radius: 5px; padding: 2px 6px; font-size: 0.75rem; }
.ci-empty, .ci-health-empty { opacity: 0.7; font-size: 0.9rem; }
```

- [ ] **Step 3: Lint**

Run: `npm --prefix frontend run lint`
Expected: no errors.

- [ ] **Step 4: Commit**

```bash
git add frontend/src/components/ci/CiHealthView.tsx frontend/src/App.css
git commit -m "feat(ci-health): add CiHealthView component"
```

---

### Task 11: Routing + nav + App wiring

**Files:**
- Modify: `frontend/src/utils/routing.ts` (`parseDashboardMode` ~line 76)
- Modify: `frontend/src/components/MobileNav.tsx` (`MODES` array ~line 19; `modeLabel` ~line 68)
- Modify: `frontend/src/App.tsx` (mode buttons ~line 1138; hero copy ~line 1151; render ~line 1173)

- [ ] **Step 1: Accept `ci-health` in `parseDashboardMode`**

Change:

```typescript
  if (mode === 'ship' || mode === 'issues') {
    return mode;
  }
```

to:

```typescript
  if (mode === 'ship' || mode === 'issues' || mode === 'ci-health') {
    return mode;
  }
```

- [ ] **Step 2: Add a `MobileNav` entry**

In `MobileNav.tsx`, add an entry to the `MODES` array (match the existing object shape — `id`, `label`, `icon`; reuse an existing icon node or a simple emoji span):

```tsx
  { id: 'ci-health', label: 'CI health', icon: <span aria-hidden>🩺</span> },
```

If `modeLabel` (~line 68) is a switch/map over modes, add a `case 'ci-health': return 'CI health';` arm so the label resolves.

- [ ] **Step 3: Add a desktop mode button in `App.tsx`**

After the "Ship mode" button block (~line 1145, inside `.mode-toggle`), add:

```tsx
              <button
                type="button"
                className={dashboardMode === 'ci-health' ? 'selected' : undefined}
                aria-pressed={dashboardMode === 'ci-health'}
                onClick={() => switchDashboardMode('ci-health')}
              >
                CI health
              </button>
```

- [ ] **Step 4: Add the import and render `CiHealthView` when active**

Add the import near the other component imports at the top of `App.tsx`:

```tsx
import CiHealthView from './components/ci/CiHealthView';
```

In the `<main className={...}>` body, add this block immediately before the `{viewMode === 'dashboard' && (` block (~line 1174):

```tsx
        {viewMode === 'dashboard' && dashboardMode === 'ci-health' && <CiHealthView />}
```

Then guard the existing dashboard render so both don't show. Change:

```tsx
        {viewMode === 'dashboard' && (
          <DashboardView
```

to:

```tsx
        {viewMode === 'dashboard' && dashboardMode !== 'ci-health' && (
          <DashboardView
```

- [ ] **Step 5: Hero copy for the new mode**

In the hero-copy ternary (~line 1151), add a `ci-health` arm:

```tsx
            {dashboardMode === 'ship'
              ? 'Only milestone and base-branch work is shown; the normal attention queue is hidden.'
              : dashboardMode === 'issues'
                ? 'Find the issues that need focused follow-up without mixing them into PR review work.'
                : dashboardMode === 'ci-health'
                  ? 'GitHub Actions health across the watched repos — daily pulse, weekly trend, and what is on fire.'
                  : 'Find the pull requests that need attention and keep reviews moving.'}
```

- [ ] **Step 6: Lint and build the frontend**

Run: `npm --prefix frontend run lint && npm --prefix frontend run build`
Expected: lint clean; build succeeds.

- [ ] **Step 7: Commit**

```bash
git add frontend/src/utils/routing.ts frontend/src/components/MobileNav.tsx frontend/src/App.tsx
git commit -m "feat(ci-health): add CI health tab routing, nav, and rendering"
```

---

### Task 12: End-to-end manual verification

**Files:** none (verification only)

- [ ] **Step 1: Configure a public-cache token + a small repo set for local run**

The producer fetches with the public-cache (server) token. For a local run, set a fine-grained PAT and a repo list via user-secrets on the Server project:

```bash
dotnet user-secrets --project pr-timeline-app.Server set "GitHubCacheWarmup:PublicCacheToken" "<token>"
dotnet user-secrets --project pr-timeline-app.Server set "CiHealth:EnabledInDevelopment" "true"
dotnet user-secrets --project pr-timeline-app.Server set "CiHealth:Repositories:0" "microsoft/aspire"
```

- [ ] **Step 2: Run the app**

Run: `aspire start`
Expected: server + Azurite + frontend start; logs show `CI health pulse written: …` within ~1–2 minutes of startup.

- [ ] **Step 3: Hit the API directly**

Run: `curl -s http://localhost:<server-port>/api/ci-health | head -c 400` (server port from the Aspire dashboard)
Expected: JSON with `pulse` populated (`workflows`, `failingNow`, `botPrs`, `updatedAt`).

- [ ] **Step 4: Verify the tab in the browser**

Open the Vite frontend, click **CI health**. Confirm: status strip shows update times, the five blocks render, daily pulse has pass-rate bars, the bot/automated PR list shows any open bot PRs, and the URL carries `?mode=ci-health`.

- [ ] **Step 5: Final full validation**

Run:
```bash
dotnet build pr-timeline-app.slnx --no-restore && dotnet test pr-timeline-app.slnx --no-build
npm --prefix frontend run lint && npm --prefix frontend run build
```
Expected: all green.

- [ ] **Step 6: Commit any final touch-ups**

```bash
git add -A
git commit -m "chore(ci-health): finalize CI health tab v1"
```

---

## Self-Review notes

- **Spec coverage:** status strip, failing-now, daily pulse (36h), weekly (7d), bot/automated PRs → Tasks 3, 4, 7, 10. In-process producer + shared Blob → Tasks 6, 7. Public API → Task 8. New tab/nav/routing → Tasks 9–11. Config (`CiHealth`) → Tasks 1, 8. Reaction-engine seam (`linkedIssue` + ghost button + producer comment) → Tasks 2, 7, 10. Tests → Tasks 3, 4.
- **Known v1 limitation (documented in code):** bot classification uses allowlist + labels only, because `PullRequestSummary` has no `is_bot`. The classifier supports `is_bot`; the producer passes `false`. Called out in `BotPrClassifier` and `CiHealthProducer` comments, matching the shepherd note that "the allowlist is the floor."
- **Type consistency:** snapshot/record names, JSON camelCase casing, and the TS types in Task 9 mirror the C# records in Task 2. `MapCiStatus` outputs (`passing`/`failing`/`pending`/`unknown`) are the values the component renders.
- **Signature guards:** Tasks 5/7/9 include explicit `grep`/confirm steps for `GetPullRequestsAsync`, `readJson`, and `BinaryData` JsonTypeInfo overloads, since those touch existing code whose exact shape must match.
