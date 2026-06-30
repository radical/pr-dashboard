using Microsoft.Extensions.Options;

// Background loop that turns "you are a requested reviewer" into a real push notification.
//
// Concurrency assumption (v1): exactly ONE instance of this service runs, enforced by the
// AppHost pinning the server to MinReplicas = MaxReplicas = 1. That makes the per-user dedupe
// state blob effectively single-writer, so the optimistic-concurrency retry around it is
// defense-in-depth rather than load-bearing. To scale the server horizontally with
// notifications enabled, add a single-leader mechanism (e.g. an Azure Blob lease) so only one
// replica detects + sends; the ETag-guarded state writes already in place then become the
// safety net.
sealed class NotificationDetectorService(
    IServiceScopeFactory scopeFactory,
    INotificationStore store,
    IPushSender sender,
    CiHealthSnapshotStore ciSnapshotStore,
    IOptions<GitHubCacheWarmupOptions> warmupOptions,
    IOptions<WebPushOptions> webPushOptions,
    TimeProvider timeProvider,
    ILogger<NotificationDetectorService> logger) : BackgroundService
{
    // Let the public cache warm before the first scan so the opening cycle has data to match.
    private static readonly TimeSpan s_startupDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!sender.IsEnabled)
        {
            logger.LogInformation(
                "Notification detector is idle because Web Push is not configured (set WebPush:PublicKey/PrivateKey/Subject).");
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

        await SafeRunCycleAsync(stoppingToken);

        using var timer = new PeriodicTimer(GetInterval(), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SafeRunCycleAsync(stoppingToken);
        }
    }

    private async Task SafeRunCycleAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunCycleAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Notification detector cycle failed.");
        }
    }

    // Core cycle, exposed internally so tests can drive it directly with fakes.
    internal async Task<DetectorCycleStats> RunCycleAsync(CancellationToken cancellationToken)
    {
        var stats = new DetectorCycleStats();
        if (!sender.IsEnabled)
        {
            return stats;
        }

        var repositories = ResolveRepositories();
        if (repositories.Count == 0)
        {
            return stats;
        }

        // Build the per-trigger sets of users who can actually receive a push: opted in (have a
        // profile), have at least one subscription, and have that specific trigger enabled.
        var reviewRequestedUserIds = new HashSet<long>();
        var readyToMergeUserIds = new HashSet<long>();
        var buildBrokenUserIds = new HashSet<long>();
        var subscriptionsByUser = new Dictionary<long, IReadOnlyList<PushSubscriptionRecord>>();
        foreach (var profile in await store.ListUserProfilesAsync(cancellationToken))
        {
            if (profile.Id <= 0)
            {
                continue;
            }

            var preferences = await store.GetPreferencesAsync(profile.Id, cancellationToken);
            if (!preferences.ReviewRequested && !preferences.ReadyToMerge && !preferences.BuildBroken)
            {
                continue;
            }

            var subscriptions = await store.GetSubscriptionsAsync(profile.Id, cancellationToken);
            if (subscriptions.Count == 0)
            {
                continue;
            }

            subscriptionsByUser[profile.Id] = subscriptions;
            if (preferences.ReviewRequested)
            {
                reviewRequestedUserIds.Add(profile.Id);
            }

            if (preferences.ReadyToMerge)
            {
                readyToMergeUserIds.Add(profile.Id);
            }

            if (preferences.BuildBroken)
            {
                buildBrokenUserIds.Add(profile.Id);
            }

            stats.Subscribers++;
        }

        if (subscriptionsByUser.Count == 0)
        {
            return stats;
        }

        // Scan each allowlist repo from the (warmed) public cache and collect candidates per
        // user. Track which repos were scanned successfully so we only prune state for those.
        var now = timeProvider.GetUtcNow();
        var candidatesByUser = new Dictionary<long, List<DetectedNotification>>();
        var scannedRepositories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var repository in repositories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<PullRequestSummary> pullRequests;
            try
            {
                using var scope = scopeFactory.CreateScope();
                var gitHub = scope.ServiceProvider.GetRequiredService<GitHubClient>();
                pullRequests = await gitHub.GetPullRequestsAsync(
                    repository,
                    "open",
                    forceRefresh: false,
                    cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Notification detector could not read pull requests for {Repository}.", repository);
                continue;
            }

            scannedRepositories.Add(repository.ToString());
            stats.PullRequestsScanned += pullRequests.Count;

            foreach (var candidate in ReviewRequestDetection.DetectForRepository(repository.ToString(), pullRequests, reviewRequestedUserIds))
            {
                AddCandidate(candidatesByUser, new DetectedNotification(
                    candidate.UserId,
                    candidate.Repository,
                    candidate.Number,
                    ReviewRequestDetection.EventKey(candidate.Repository, candidate.Number),
                    ReviewRequestDetection.RequestedFingerprint,
                    NotificationPayloads.ReviewRequested(candidate.Repository, candidate.Number, candidate.Title, candidate.Url)));
            }

            foreach (var candidate in ReadyToMergeDetection.DetectForRepository(repository.ToString(), pullRequests, readyToMergeUserIds, now))
            {
                AddCandidate(candidatesByUser, new DetectedNotification(
                    candidate.UserId,
                    candidate.Repository,
                    candidate.Number,
                    ReadyToMergeDetection.EventKey(candidate.Repository, candidate.Number),
                    ReadyToMergeDetection.ReadyFingerprint,
                    NotificationPayloads.ReadyToMerge(candidate.Role, candidate.Repository, candidate.Number, candidate.Title, candidate.Url)));
            }
        }

        // Team-wide CI: alert every opted-in user about each main lane red at tip. The shared
        // pulse snapshot is read once; when it's available, every allowlist repo's CI state was
        // observed this cycle, so build-broken state for recovered lanes can be pruned.
        var ciObservedRepositories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (buildBrokenUserIds.Count > 0)
        {
            CiHealthPulseSnapshot? pulse = null;
            try
            {
                pulse = await ciSnapshotStore.ReadPulseAsync(cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Notification detector could not read the CI health snapshot.");
            }

            if (pulse is not null)
            {
                foreach (var repository in repositories)
                {
                    ciObservedRepositories.Add(repository.ToString());
                }

                foreach (var candidate in CiBuildBrokenDetection.DetectFromSnapshot(pulse, buildBrokenUserIds))
                {
                    AddCandidate(candidatesByUser, new DetectedNotification(
                        candidate.UserId,
                        candidate.Repository,
                        0,
                        CiBuildBrokenDetection.EventKey(candidate.Repository, candidate.Lane),
                        CiBuildBrokenDetection.RedFingerprint,
                        NotificationPayloads.BuildBroken(candidate.Repository, candidate.Lane, CiBuildBrokenDetection.DeepLink())));
                }
            }
        }

        // Dedupe + send per user. Users with no candidates are still processed so stale state
        // (PRs they were removed from) gets pruned.
        foreach (var (userId, subscriptions) in subscriptionsByUser)
        {
            candidatesByUser.TryGetValue(userId, out var candidates);
            await ProcessUserAsync(userId, subscriptions, candidates ?? [], scannedRepositories, ciObservedRepositories, stats, cancellationToken);
        }

        logger.LogInformation(
            "Notification detector cycle complete. subscribers={Subscribers} prsScanned={PullRequests} candidates={Candidates} new={NewEvents} sent={Sent} failed={Failed} pruned={Pruned} stateCleared={StateCleared}.",
            stats.Subscribers,
            stats.PullRequestsScanned,
            candidatesByUser.Values.Sum(list => list.Count),
            stats.NewEvents,
            stats.Sent,
            stats.Failed,
            stats.SubscriptionsPruned,
            stats.StateEntriesCleared);

        return stats;
    }

    internal async Task ProcessUserAsync(
        long userId,
        IReadOnlyList<PushSubscriptionRecord> subscriptions,
        IReadOnlyList<DetectedNotification> candidates,
        IReadOnlySet<string> scannedRepositories,
        DetectorCycleStats stats,
        CancellationToken cancellationToken) =>
        await ProcessUserAsync(userId, subscriptions, candidates, scannedRepositories, null, stats, cancellationToken);

    internal async Task ProcessUserAsync(
        long userId,
        IReadOnlyList<PushSubscriptionRecord> subscriptions,
        IReadOnlyList<DetectedNotification> candidates,
        IReadOnlySet<string> scannedRepositories,
        IReadOnlySet<string>? ciObservedRepositories,
        DetectorCycleStats stats,
        CancellationToken cancellationToken)
    {
        // Distinct by event key in case the same PR surfaces twice in a cycle.
        var distinctCandidates = candidates
            .GroupBy(candidate => candidate.EventKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        var currentKeys = distinctCandidates
            .Select(candidate => candidate.EventKey)
            .ToHashSet(StringComparer.Ordinal);

        // Read current state to decide which candidates are genuinely new (not yet notified).
        var existing = (await store.GetStateAsync(userId, cancellationToken)).State.Events;
        var existingKeys = existing.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newCandidates = distinctCandidates
            .Where(candidate => !existingKeys.Contains(candidate.EventKey))
            .ToList();

        // Send first, then persist state only for events that actually delivered, so a transient
        // push failure doesn't permanently suppress the notification. eventKey -> fingerprint.
        var sent = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var candidate in newCandidates)
        {
            stats.NewEvents++;
            if (await SendToAllAsync(userId, subscriptions, candidate.PayloadJson, stats, cancellationToken))
            {
                sent[candidate.EventKey] = candidate.Fingerprint;
            }
        }

        var now = timeProvider.GetUtcNow();
        await store.UpdateStateAsync(userId, state =>
        {
            var changed = false;

            foreach (var (key, fingerprint) in sent)
            {
                state.Events[key] = new NotificationEventState
                {
                    Fingerprint = fingerprint,
                    LastNotifiedAt = now
                };
                changed = true;
            }

            // Migrate pre-normalization keys (same repo/PR but different casing) without
            // re-sending a notification. Future cycles then use the stable canonical key.
            foreach (var key in currentKeys)
            {
                if (state.Events.ContainsKey(key))
                {
                    continue;
                }

                var existingKey = state.Events.Keys
                    .FirstOrDefault(candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase));
                if (existingKey is not null)
                {
                    state.Events[key] = state.Events[existingKey];
                    state.Events.Remove(existingKey);
                    changed = true;
                }
            }

            // Prune entries for domains we observed this cycle where the trigger no longer
            // applies (e.g. the user is no longer a requested reviewer, the PR is no longer ready
            // to merge, or a main lane recovered), so a future re-entry notifies again. CI keys
            // are gated on the CI snapshot being read this cycle; PR keys on the repo being
            // scanned, so a partial failure of one source never spuriously prunes the other.
            foreach (var key in state.Events.Keys.ToList())
            {
                if (currentKeys.Contains(key))
                {
                    continue;
                }

                var observed = CiBuildBrokenDetection.TryGetRepository(key, out var ciRepository)
                    ? ciObservedRepositories is not null && ciObservedRepositories.Contains(ciRepository)
                    : TryGetEventRepository(key, out var repository) && scannedRepositories.Contains(repository);

                if (observed)
                {
                    state.Events.Remove(key);
                    stats.StateEntriesCleared++;
                    changed = true;
                }
            }

            return changed;
        }, cancellationToken);
    }

    private static void AddCandidate(
        Dictionary<long, List<DetectedNotification>> candidatesByUser,
        DetectedNotification candidate)
    {
        if (!candidatesByUser.TryGetValue(candidate.UserId, out var list))
        {
            list = [];
            candidatesByUser[candidate.UserId] = list;
        }

        list.Add(candidate);
    }

    // Recovers the repository slug from any known notification event key so stale state can be
    // pruned regardless of which trigger wrote it.
    private static bool TryGetEventRepository(string eventKey, out string repository) =>
        ReviewRequestDetection.TryGetRepository(eventKey, out repository)
        || ReadyToMergeDetection.TryGetRepository(eventKey, out repository);

    private async Task<bool> SendToAllAsync(
        long userId,
        IReadOnlyList<PushSubscriptionRecord> subscriptions,
        string payloadJson,
        DetectorCycleStats stats,
        CancellationToken cancellationToken)
    {
        var anySent = false;
        foreach (var subscription in subscriptions)
        {
            var result = await sender.SendAsync(subscription, payloadJson, cancellationToken);
            switch (result.Outcome)
            {
                case PushDeliveryOutcome.Sent:
                    anySent = true;
                    stats.Sent++;
                    break;
                case PushDeliveryOutcome.Expired:
                    stats.SubscriptionsPruned++;
                    await store.RemoveSubscriptionAsync(userId, subscription.Endpoint, cancellationToken);
                    break;
                default:
                    stats.Failed++;
                    break;
            }
        }

        return anySent;
    }

    private IReadOnlyList<RepositoryName> ResolveRepositories()
    {
        var result = new List<RepositoryName>();
        foreach (var repository in warmupOptions.Value.Repositories)
        {
            if (RepositoryName.TryParse(repository, out var repositoryName))
            {
                result.Add(repositoryName);
            }
            else
            {
                logger.LogWarning("Skipping notification detection for invalid repository '{Repository}'.", repository);
            }
        }

        return result;
    }

    private TimeSpan GetInterval()
    {
        var minutes = webPushOptions.Value.DetectionIntervalMinutes;
        return minutes <= 0 ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(minutes);
    }
}

// One ready-to-send notification for a single user: a deduped event with its payload. Both
// triggers (review_requested, ready_to_merge) map their candidates to this so the detector
// shares one send/dedupe/prune path.
sealed record DetectedNotification(
    long UserId,
    string Repository,
    int Number,
    string EventKey,
    string Fingerprint,
    string PayloadJson);

// Per-cycle counters for observability. No secrets, only counts.
sealed class DetectorCycleStats
{
    public int Subscribers { get; set; }

    public int PullRequestsScanned { get; set; }

    public int NewEvents { get; set; }

    public int Sent { get; set; }

    public int Failed { get; set; }

    public int SubscriptionsPruned { get; set; }

    public int StateEntriesCleared { get; set; }
}
