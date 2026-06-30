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
    // Serializes the background timer cycle and on-demand manual refreshes so they never run the same
    // repo loop concurrently (overlapping cycles would double the GitHub request budget and race the
    // snapshot write).
    private readonly SemaphoreSlim _cycleLock = new(1, 1);
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
        await _cycleLock.WaitAsync(cancellationToken);
        try
        {
            // Token selection (user token when a request is signed in or the dev gh/GITHUB_TOKEN fallback
            // resolves one, else the shared public-cache server token) is handled inside GitHubClient's
            // scope resolution — the same path PR fetching uses. The cadence relies on the cache, so it
            // does not force a refresh.
            await RunPulseCycleAsync(forceRefresh: false, cancellationToken);

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
        finally
        {
            _cycleLock.Release();
        }
    }

    // On-demand pulse refresh triggered by a signed-in user (POST /api/ci-health/refresh). Forces a
    // fresh fetch (bypassing the cache) using the caller's GitHub token — the same scope-selected token
    // path PR fetching uses. Serialized against the timer cycle via the same lock, then returns the
    // snapshot it wrote alongside the current (cadence-owned) weekly snapshot.
    internal async Task<CiHealthResponse> RefreshNowAsync(CancellationToken cancellationToken)
    {
        await _cycleLock.WaitAsync(cancellationToken);
        try
        {
            var pulse = await RunPulseCycleAsync(forceRefresh: true, cancellationToken);
            var weekly = await store.ReadWeeklyAsync(cancellationToken);
            return new CiHealthResponse(pulse, weekly);
        }
        finally
        {
            _cycleLock.Release();
        }
    }

    // Exposed internal so tests can drive a single cycle with a real/fake GitHubClient + store.
    internal async Task<CiHealthPulseSnapshot> RunPulseCycleAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var config = options.Value;
        var now = timeProvider.GetUtcNow();
        var window = TimeSpan.FromHours(config.PulseWindowHours);

        var pulses = new List<WorkflowPulse>();
        var failing = new List<FailingWorkflow>();
        var botPrs = new List<BotPullRequest>();
        var botIssues = new List<BotIssue>();

        foreach (var repository in ResolveRepositories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var scope = scopeFactory.CreateScope();
            var gitHub = scope.ServiceProvider.GetRequiredService<GitHubClient>();

            try
            {
                var defaultBranch = await gitHub.GetDefaultBranchAsync(repository, cancellationToken, forceRefresh);
                var runs = ToLanes(repository, await gitHub.GetWorkflowRunsAsync(repository, now - window, cancellationToken, workflowId: null, forceRefresh), defaultBranch);
                var (repoPulses, repoFailing) = CiHealthComputer.ComputePulse(runs, now, window, config.StreakThreshold);
                pulses.AddRange(repoPulses);
                failing.AddRange(repoFailing);

                // GraphQL fetch so bot PRs carry merge-readiness state (mergeable / checks / review).
                // forceRefresh follows the cycle: the timer relies on the shared cache, a manual refresh
                // forces fresh data.
                var openPrs = await gitHub.GetPullRequestsGraphQlAsync(repository, "open", forceRefresh, cancellationToken);
                botPrs.AddRange(BotPrClassifier.Classify(ToCandidates(repository, openPrs), config.BotLogins));

                if (config.TrackBotIssues)
                {
                    var openIssues = await gitHub.GetOpenIssuesAsync(repository, cancellationToken, forceRefresh);
                    botIssues.AddRange(BotPrClassifier.ClassifyIssues(openIssues, config.BotLogins));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "CI health pulse failed for {Repository}.", repository);
            }
        }

        var snapshot = new CiHealthPulseSnapshot(pulses, failing, botPrs, botIssues, now);
        await store.WritePulseAsync(snapshot, cancellationToken);
        logger.LogInformation("CI health pulse written: {Lanes} lanes, {Failing} failing, {BotPrs} bot PRs, {BotIssues} bot issues.",
            pulses.Count, failing.Count, botPrs.Count, botIssues.Count);
        return snapshot;
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
                // Fetch runs per workflow so the 14-day window isn't truncated by the repo-wide
                // /actions/runs ~1000-result ceiling on busy repos (which would zero out the prior
                // week and fabricate the trend delta).
                var defaultBranch = await gitHub.GetDefaultBranchAsync(repository, cancellationToken);
                var definitions = await gitHub.GetWorkflowDefinitionsAsync(repository, cancellationToken);
                // Fetch each workflow's runs concurrently; GitHubClient's internal request throttle
                // bounds real concurrency. Sequential fetches are too slow on repos with many workflows.
                var runLists = await Task.WhenAll(
                    definitions.Select(definition => gitHub.GetWorkflowRunsAsync(repository, since, cancellationToken, definition.Id)));
                var runs = runLists.SelectMany(list => list).ToList();

                weekly.AddRange(CiHealthComputer.ComputeWeekly(ToLanes(repository, runs, defaultBranch), now, config.WeeklyWindowDays));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "CI health weekly failed for {Repository}.", repository);
            }
        }

        await store.WriteWeeklyAsync(new CiHealthWeeklySnapshot(weekly, now), cancellationToken);
        logger.LogInformation("CI health weekly written: {Lanes} lanes.", weekly.Count);
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

    // Assigns each run to a lane + section using the repo's lane config. Drops runs that aren't part of
    // a tracked lane (PRs, feature-branch pushes, skipped/other scheduled). Tags the run with its
    // cleaned workflow name, lane label, section, and always-show flag.
    private IReadOnlyList<WorkflowRun> ToLanes(RepositoryName repository, IReadOnlyList<WorkflowRun> runs, string defaultBranch)
    {
        var laneConfig = options.Value.Lanes.GetValueOrDefault(repository.ToString());
        var branches = laneConfig is { Branches.Length: > 0 } ? laneConfig.Branches : [defaultBranch];
        var mainWorkflows = laneConfig?.MainWorkflows ?? [];
        var skipScheduled = laneConfig?.SkipScheduled ?? [];
        var alwaysShow = new HashSet<string>(laneConfig?.AlwaysShowScheduled ?? [], StringComparer.OrdinalIgnoreCase);

        var result = new List<WorkflowRun>();
        foreach (var run in runs)
        {
            var assignment = WorkflowLane.Resolve(run.Workflow, run.Event, run.HeadBranch, branches, mainWorkflows, skipScheduled);
            if (assignment is null)
            {
                continue;
            }

            var clean = WorkflowLane.CleanName(run.Workflow);
            result.Add(run with
            {
                Workflow = clean,
                Lane = assignment.Lane,
                Section = assignment.Section,
                AlwaysShow = assignment.Section == WorkflowLane.ScheduledSection
                    && (alwaysShow.Contains(clean) || alwaysShow.Contains(run.Workflow)),
            });
        }

        return result;
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
                MapMergeable(pr.MergeableState),
                MapReview(pr.Review),
                pr.Labels))
            .ToList();

    private static string MapCiStatus(ChecksStatus checks) =>
        checks.FailureCount > 0 ? "failing"
        : checks.PendingCount > 0 ? "pending"
        : checks.State is "success" ? "passing"
        : checks.State is "unknown" ? "unknown"
        : checks.State;

    private static string MapMergeable(string? mergeableState) =>
        mergeableState?.ToLowerInvariant() switch
        {
            "mergeable" or "clean" or "unstable" or "has_hooks" or "behind" or "blocked" => "mergeable",
            "conflicting" or "dirty" => "conflicting",
            _ => "unknown",
        };

    private static string MapReview(ReviewStatus review) =>
        review.ApprovalCount > 0 ? "approved"
        : review.ChangesRequestedCount > 0 ? "changes_requested"
        : "review_required";
}
