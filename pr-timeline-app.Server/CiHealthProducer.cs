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
                var defaultBranch = await gitHub.GetDefaultBranchAsync(repository, cancellationToken);
                var runs = ToLanes(repository, await gitHub.GetWorkflowRunsAsync(repository, now - window, cancellationToken), defaultBranch);
                var (repoPulses, repoFailing) = CiHealthComputer.ComputePulse(runs, now, window, config.StreakThreshold);
                pulses.AddRange(repoPulses);
                failing.AddRange(repoFailing);

                // forceRefresh: false — rely on the shared cache; CI health doesn't need fresher data
                // than the existing cache warmup provides.
                var openPrs = await gitHub.GetPullRequestsAsync(repository, "open", false, cancellationToken);
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
                // Fetch runs per workflow so the 14-day window isn't truncated by the repo-wide
                // /actions/runs ~1000-result ceiling on busy repos (which would zero out the prior
                // week and fabricate the trend delta).
                var defaultBranch = await gitHub.GetDefaultBranchAsync(repository, cancellationToken);
                var definitions = FilterDefinitions(repository, await gitHub.GetWorkflowDefinitionsAsync(repository, cancellationToken));
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
        logger.LogInformation("CI health weekly written: {Workflows} workflows.", weekly.Count);
    }

    // When a per-repo workflow allowlist is configured, restrict the definitions we fetch runs for to
    // that set (by cleaned name); otherwise fetch all of them and let ToLanes apply the lane filter.
    private IReadOnlyList<WorkflowDefinition> FilterDefinitions(RepositoryName repository, IReadOnlyList<WorkflowDefinition> definitions)
    {
        if (!options.Value.Workflows.TryGetValue(repository.ToString(), out var allowed) || allowed.Length == 0)
        {
            return definitions;
        }

        var set = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
        return definitions.Where(definition => set.Contains(WorkflowLane.CleanName(definition.Name)) || set.Contains(definition.Name)).ToList();
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

    // Assigns each run to a lane (workflow x trigger), dropping runs that aren't main-push / PR /
    // scheduled, and (when configured) keeping only allowlisted workflows. Tags the run with its
    // cleaned workflow name, trigger, and lane label for the computer to group on.
    private IReadOnlyList<WorkflowRun> ToLanes(RepositoryName repository, IReadOnlyList<WorkflowRun> runs, string defaultBranch)
    {
        options.Value.Workflows.TryGetValue(repository.ToString(), out var allowed);
        var allowSet = allowed is { Length: > 0 }
            ? new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase)
            : null;

        var result = new List<WorkflowRun>();
        foreach (var run in runs)
        {
            var trigger = WorkflowLane.Classify(run.Event, run.HeadBranch, defaultBranch);
            if (trigger == LaneTrigger.Other)
            {
                continue;
            }

            var cleanName = WorkflowLane.CleanName(run.Workflow);
            if (allowSet is not null && !allowSet.Contains(cleanName) && !allowSet.Contains(run.Workflow))
            {
                continue;
            }

            result.Add(run with
            {
                Workflow = cleanName,
                Trigger = trigger,
                Lane = WorkflowLane.LaneLabel(run.Workflow, trigger),
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
                pr.Labels))
            .ToList();

    private static string MapCiStatus(ChecksStatus checks) =>
        checks.FailureCount > 0 ? "failing"
        : checks.PendingCount > 0 ? "pending"
        : checks.State is "success" ? "passing"
        : checks.State is "unknown" ? "unknown"
        : checks.State;
}
