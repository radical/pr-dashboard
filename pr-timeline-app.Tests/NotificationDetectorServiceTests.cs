using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace pr_timeline_app.Tests;

internal sealed class FakePushSender : IPushSender
{
    public bool IsEnabled { get; set; } = true;

    public List<(string Endpoint, string Payload)> Sent { get; } = [];

    // Lets a test force a specific outcome (e.g. Failed/Expired) per subscription.
    public Func<PushSubscriptionRecord, PushDeliveryResult>? Behavior { get; set; }

    public Task<PushDeliveryResult> SendAsync(
        PushSubscriptionRecord subscription,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        var result = Behavior?.Invoke(subscription) ?? new PushDeliveryResult(PushDeliveryOutcome.Sent, 201);
        if (result.Outcome == PushDeliveryOutcome.Sent)
        {
            Sent.Add((subscription.Endpoint, payloadJson));
        }

        return Task.FromResult(result);
    }
}

public sealed class ReviewRequestDetectionTests
{
    [Fact]
    public void EventKeyAndDeepLinkFollowConventions()
    {
        Assert.Equal("review_requested:microsoft/aspire#101", ReviewRequestDetection.EventKey("Microsoft/Aspire", 101));
        Assert.Equal("/#pr/microsoft%2Faspire/101", ReviewRequestDetection.DeepLink("Microsoft/Aspire", 101));
    }

    [Fact]
    public void TryGetRepositoryRecoversSlug()
    {
        Assert.True(ReviewRequestDetection.TryGetRepository("review_requested:Microsoft/Aspire#101", out var repo));
        Assert.Equal("microsoft/aspire", repo);
        Assert.False(ReviewRequestDetection.TryGetRepository("something_else:foo#1", out _));
    }

    [Fact]
    public void DetectMatchesRequestedReviewerIdsAndSkipsDraftAndClosed()
    {
        var subscribers = new HashSet<long> { 7 };

        var pullRequests = new[]
        {
            Pr(1, "Open, requested", ["renamed-login", "someone-else"], [7, 99]),
            Pr(2, "Draft, requested", ["renamed-login"], [7], draft: true),
            Pr(3, "Closed, requested", ["renamed-login"], [7], state: "closed"),
            Pr(4, "Open, not requested", ["nobody"], [42])
        };

        var detected = ReviewRequestDetection
            .DetectForRepository("O/R", pullRequests, subscribers)
            .ToList();

        Assert.Single(detected);
        Assert.Equal(7, detected[0].UserId);
        Assert.Equal("o/r", detected[0].Repository);
        Assert.Equal(1, detected[0].Number);
        Assert.Equal("/#pr/o%2Fr/1", detected[0].Url);
    }

    [Fact]
    public void DetectDoesNotFallBackToMutableReviewerLogin()
    {
        var pullRequests = new[]
        {
            Pr(1, "Login matches but id missing", ["octocat"], [])
        };

        var detected = ReviewRequestDetection
            .DetectForRepository("o/r", pullRequests, new HashSet<long> { 7 })
            .ToList();

        Assert.Empty(detected);
    }

    [Fact]
    public void DetectSkipsThePullRequestsOwnHumanOwner()
    {
        // The human behind a Copilot PR is listed as both the sole assignee and a requested
        // reviewer; OwnerUserId carries that human's id so they are not pinged to review their
        // own work. A different requested reviewer on the same PR still gets a candidate.
        var pullRequests = new[]
        {
            Pr(1, "Copilot PR owned by 7", ["radical", "adamint"], [7, 8], ownerUserId: 7)
        };

        var detected = ReviewRequestDetection
            .DetectForRepository("o/r", pullRequests, new HashSet<long> { 7, 8 })
            .ToList();

        Assert.Single(detected);
        Assert.Equal(8, detected[0].UserId);
    }

    internal static PullRequestSummary Pr(
        int number,
        string title,
        string[] reviewers,
        long[] reviewerIds,
        bool draft = false,
        string state = "open",
        long? ownerUserId = null) =>
        new(
            number,
            title,
            state,
            draft,
            "author",
            $"https://github.com/o/r/pull/{number}",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            [],
            reviewers,
            null,
            [],
            0,
            0,
            0,
            0,
            null,
            null,
            null,
            null,
            ReviewStatus.Waiting,
            ChecksStatus.Unknown)
        {
            RequestedReviewerIds = reviewerIds,
            OwnerUserId = ownerUserId
        };
}

public sealed class NotificationDetectorServiceTests
{
    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan delta) => now += delta;
    }

    private static NotificationDetectorService CreateService(
        INotificationStore store,
        IPushSender sender,
        TimeProvider time) =>
        new(
            scopeFactory: null!,
            store,
            sender,
            ciSnapshotStore: null!,
            Options.Create(new GitHubCacheWarmupOptions()),
            Options.Create(new WebPushOptions
            {
                Enabled = true,
                PublicKey = "public",
                PrivateKey = "private",
                Subject = "mailto:test@example.com"
            }),
            time,
            NullLogger<NotificationDetectorService>.Instance);

    private static PushSubscriptionRecord Subscription(string endpoint = "https://push.example/abc") =>
        new() { Endpoint = endpoint, P256dh = "p", Auth = "a" };

    private static DetectedNotification Candidate(long userId, string repo, int number) =>
        new(
            userId,
            repo,
            number,
            ReviewRequestDetection.EventKey(repo, number),
            ReviewRequestDetection.RequestedFingerprint,
            NotificationPayloads.ReviewRequested(repo, number, $"PR {number}", ReviewRequestDetection.DeepLink(repo, number)));

    private static DetectedNotification ReadyCandidate(long userId, string repo, int number) =>
        new(
            userId,
            repo,
            number,
            ReadyToMergeDetection.EventKey(repo, number),
            ReadyToMergeDetection.ReadyFingerprint,
            NotificationPayloads.ReadyToMerge(ReadyToMergeRole.Author, repo, number, $"PR {number}", ReadyToMergeDetection.DeepLink(repo, number)));

    private static DetectedNotification BuildBrokenCandidate(long userId, string repo, string lane) =>
        new(
            userId,
            repo,
            0,
            CiBuildBrokenDetection.EventKey(repo, lane),
            CiBuildBrokenDetection.RedFingerprint,
            NotificationPayloads.BuildBroken(repo, lane, CiBuildBrokenDetection.DeepLink()));

    [Fact]
    public async Task BuildBrokenNotifiesOnRedTransitionAndPrunesOnRecovery()
    {
        var store = new InMemoryNotificationStore();
        var sender = new FakePushSender();
        var service = CreateService(store, sender, new TestTimeProvider());

        var subs = new[] { Subscription() };
        await store.UpsertSubscriptionAsync(1, subs[0], TestContext.Current.CancellationToken);

        var prScanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ciObserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "o/r" };

        // First red → one alert.
        await service.ProcessUserAsync(1, subs, [BuildBrokenCandidate(1, "o/r", "CI · main")], prScanned, ciObserved, new DetectorCycleStats(), TestContext.Current.CancellationToken);
        Assert.Single(sender.Sent);

        // Still red on the next cycle → no repeat.
        await service.ProcessUserAsync(1, subs, [BuildBrokenCandidate(1, "o/r", "CI · main")], prScanned, ciObserved, new DetectorCycleStats(), TestContext.Current.CancellationToken);
        Assert.Single(sender.Sent);

        // Recovered (snapshot read, lane absent) → state pruned.
        var pruneStats = new DetectorCycleStats();
        await service.ProcessUserAsync(1, subs, [], prScanned, ciObserved, pruneStats, TestContext.Current.CancellationToken);
        Assert.Equal(1, pruneStats.StateEntriesCleared);

        // Breaks again → alerts again.
        await service.ProcessUserAsync(1, subs, [BuildBrokenCandidate(1, "o/r", "CI · main")], prScanned, ciObserved, new DetectorCycleStats(), TestContext.Current.CancellationToken);
        Assert.Equal(2, sender.Sent.Count);
    }

    [Fact]
    public async Task BuildBrokenStateSurvivesWhenCiSnapshotMissing()
    {
        var store = new InMemoryNotificationStore();
        var sender = new FakePushSender();
        var service = CreateService(store, sender, new TestTimeProvider());

        var subs = new[] { Subscription() };
        await store.UpsertSubscriptionAsync(1, subs[0], TestContext.Current.CancellationToken);

        var prScanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "o/r" };
        await service.ProcessUserAsync(1, subs, [BuildBrokenCandidate(1, "o/r", "CI · main")], prScanned,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "o/r" }, new DetectorCycleStats(), TestContext.Current.CancellationToken);
        Assert.Single(sender.Sent);

        // A cycle where the CI snapshot wasn't read (ciObserved empty) must not prune the
        // build-broken entry, even though the PR scan covered the same repo.
        var pruneStats = new DetectorCycleStats();
        await service.ProcessUserAsync(1, subs, [], prScanned,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), pruneStats, TestContext.Current.CancellationToken);
        Assert.Equal(0, pruneStats.StateEntriesCleared);

        var state = await store.GetStateAsync(1, TestContext.Current.CancellationToken);
        Assert.True(state.State.Events.ContainsKey(CiBuildBrokenDetection.EventKey("o/r", "CI · main")));
    }

    [Fact]
    public async Task NewReviewRequestSendsOnceAndDedupesOnRepeat()
    {
        var store = new InMemoryNotificationStore();
        var sender = new FakePushSender();
        var time = new TestTimeProvider();
        var service = CreateService(store, sender, time);

        var subs = new[] { Subscription() };
        await store.UpsertSubscriptionAsync(1, subs[0], TestContext.Current.CancellationToken);

        var candidates = new[] { Candidate(1, "o/r", 5) };
        var scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "o/r" };

        await service.ProcessUserAsync(1, subs, candidates, scanned, new DetectorCycleStats(), TestContext.Current.CancellationToken);
        Assert.Single(sender.Sent);

        // Same requested reviewer on the next cycle → no second notification.
        await service.ProcessUserAsync(1, subs, candidates, scanned, new DetectorCycleStats(), TestContext.Current.CancellationToken);
        Assert.Single(sender.Sent);

        var state = await store.GetStateAsync(1, TestContext.Current.CancellationToken);
        Assert.True(state.State.Events.ContainsKey(ReviewRequestDetection.EventKey("o/r", 5)));
    }

    [Fact]
    public async Task ReadyToMergeSendsOnceAndPrunesOnReEntry()
    {
        var store = new InMemoryNotificationStore();
        var sender = new FakePushSender();
        var service = CreateService(store, sender, new TestTimeProvider());

        var subs = new[] { Subscription() };
        await store.UpsertSubscriptionAsync(1, subs[0], TestContext.Current.CancellationToken);
        var scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "o/r" };

        await service.ProcessUserAsync(1, subs, [ReadyCandidate(1, "o/r", 5)], scanned, new DetectorCycleStats(), TestContext.Current.CancellationToken);
        Assert.Single(sender.Sent);

        // Still ready next cycle → no duplicate.
        await service.ProcessUserAsync(1, subs, [ReadyCandidate(1, "o/r", 5)], scanned, new DetectorCycleStats(), TestContext.Current.CancellationToken);
        Assert.Single(sender.Sent);

        var state = await store.GetStateAsync(1, TestContext.Current.CancellationToken);
        Assert.True(state.State.Events.ContainsKey(ReadyToMergeDetection.EventKey("o/r", 5)));

        // No longer ready (repo scanned, candidate absent) → pruned, then re-entry notifies again.
        await service.ProcessUserAsync(1, subs, [], scanned, new DetectorCycleStats(), TestContext.Current.CancellationToken);
        Assert.Empty((await store.GetStateAsync(1, TestContext.Current.CancellationToken)).State.Events);
        await service.ProcessUserAsync(1, subs, [ReadyCandidate(1, "o/r", 5)], scanned, new DetectorCycleStats(), TestContext.Current.CancellationToken);
        Assert.Equal(2, sender.Sent.Count);
    }

    [Fact]
    public async Task BothTriggersCoexistAndPruneIndependently()
    {
        var store = new InMemoryNotificationStore();
        var sender = new FakePushSender();
        var service = CreateService(store, sender, new TestTimeProvider());

        var subs = new[] { Subscription() };
        await store.UpsertSubscriptionAsync(1, subs[0], TestContext.Current.CancellationToken);
        var scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "o/r" };

        // A review_requested and a ready_to_merge event for the same user/PR are independent keys.
        await service.ProcessUserAsync(
            1,
            subs,
            [Candidate(1, "o/r", 5), ReadyCandidate(1, "o/r", 5)],
            scanned,
            new DetectorCycleStats(),
            TestContext.Current.CancellationToken);
        Assert.Equal(2, sender.Sent.Count);

        // Next cycle only the ready trigger still applies → the review entry is pruned, ready stays.
        await service.ProcessUserAsync(1, subs, [ReadyCandidate(1, "o/r", 5)], scanned, new DetectorCycleStats(), TestContext.Current.CancellationToken);
        var state = await store.GetStateAsync(1, TestContext.Current.CancellationToken);
        Assert.False(state.State.Events.ContainsKey(ReviewRequestDetection.EventKey("o/r", 5)));
        Assert.True(state.State.Events.ContainsKey(ReadyToMergeDetection.EventKey("o/r", 5)));
        Assert.Equal(2, sender.Sent.Count);
    }

    [Fact]
    public async Task ExistingMixedCaseStateMigratesWithoutDuplicateNotification()
    {
        var store = new InMemoryNotificationStore();
        var sender = new FakePushSender();
        var service = CreateService(store, sender, new TestTimeProvider());

        var subs = new[] { Subscription() };
        await store.UpsertSubscriptionAsync(1, subs[0], TestContext.Current.CancellationToken);
        await store.UpdateStateAsync(1, state =>
        {
            state.Events["review_requested:O/R#5"] = new NotificationEventState
            {
                Fingerprint = ReviewRequestDetection.RequestedFingerprint,
                LastNotifiedAt = DateTimeOffset.UnixEpoch
            };
            return true;
        }, TestContext.Current.CancellationToken);

        await service.ProcessUserAsync(
            1,
            subs,
            [Candidate(1, "o/r", 5)],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "o/r" },
            new DetectorCycleStats(),
            TestContext.Current.CancellationToken);

        Assert.Empty(sender.Sent);
        var state = await store.GetStateAsync(1, TestContext.Current.CancellationToken);
        Assert.True(state.State.Events.ContainsKey(ReviewRequestDetection.EventKey("o/r", 5)));
        Assert.False(state.State.Events.ContainsKey("review_requested:O/R#5"));
    }

    [Fact]
    public async Task RemovalPrunesStateAndAllowsReNotify()
    {
        var store = new InMemoryNotificationStore();
        var sender = new FakePushSender();
        var service = CreateService(store, sender, new TestTimeProvider());

        var subs = new[] { Subscription() };
        await store.UpsertSubscriptionAsync(1, subs[0], TestContext.Current.CancellationToken);
        var scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "o/r" };

        await service.ProcessUserAsync(1, subs, [Candidate(1, "o/r", 5)], scanned, new DetectorCycleStats(), TestContext.Current.CancellationToken);
        Assert.Single(sender.Sent);

        // No longer requested (repo scanned, candidate absent) → state entry pruned.
        var pruneStats = new DetectorCycleStats();
        await service.ProcessUserAsync(1, subs, [], scanned, pruneStats, TestContext.Current.CancellationToken);
        Assert.Equal(1, pruneStats.StateEntriesCleared);
        var cleared = await store.GetStateAsync(1, TestContext.Current.CancellationToken);
        Assert.Empty(cleared.State.Events);

        // Re-requested later → notifies again.
        await service.ProcessUserAsync(1, subs, [Candidate(1, "o/r", 5)], scanned, new DetectorCycleStats(), TestContext.Current.CancellationToken);
        Assert.Equal(2, sender.Sent.Count);
    }

    [Fact]
    public async Task UnscannedRepoStateSurvivesPrune()
    {
        var store = new InMemoryNotificationStore();
        var sender = new FakePushSender();
        var service = CreateService(store, sender, new TestTimeProvider());

        var subs = new[] { Subscription() };
        await store.UpsertSubscriptionAsync(1, subs[0], TestContext.Current.CancellationToken);

        // Seed state for two repos.
        await service.ProcessUserAsync(1, subs, [Candidate(1, "o/r", 5)],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "o/r" }, new DetectorCycleStats(), TestContext.Current.CancellationToken);
        await service.ProcessUserAsync(1, subs, [Candidate(1, "o/other", 9)],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "o/other" }, new DetectorCycleStats(), TestContext.Current.CancellationToken);

        // A cycle that only scanned o/r must not prune o/other's entry.
        await service.ProcessUserAsync(1, subs, [],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "o/r" }, new DetectorCycleStats(), TestContext.Current.CancellationToken);

        var state = await store.GetStateAsync(1, TestContext.Current.CancellationToken);
        Assert.True(state.State.Events.ContainsKey(ReviewRequestDetection.EventKey("o/other", 9)));
        Assert.False(state.State.Events.ContainsKey(ReviewRequestDetection.EventKey("o/r", 5)));
    }

    [Fact]
    public async Task FailedSendDoesNotPersistStateSoItRetries()
    {
        var store = new InMemoryNotificationStore();
        var sender = new FakePushSender
        {
            Behavior = _ => new PushDeliveryResult(PushDeliveryOutcome.Failed, 500)
        };
        var service = CreateService(store, sender, new TestTimeProvider());

        var subs = new[] { Subscription() };
        await store.UpsertSubscriptionAsync(1, subs[0], TestContext.Current.CancellationToken);
        var scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "o/r" };
        var candidates = new[] { Candidate(1, "o/r", 5) };

        var stats = new DetectorCycleStats();
        await service.ProcessUserAsync(1, subs, candidates, scanned, stats, TestContext.Current.CancellationToken);
        Assert.Equal(1, stats.Failed);
        Assert.Empty((await store.GetStateAsync(1, TestContext.Current.CancellationToken)).State.Events);

        // Recover: the next cycle still treats it as new and succeeds.
        sender.Behavior = null;
        await service.ProcessUserAsync(1, subs, candidates, scanned, new DetectorCycleStats(), TestContext.Current.CancellationToken);
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task ExpiredSubscriptionIsPruned()
    {
        var store = new InMemoryNotificationStore();
        var sender = new FakePushSender
        {
            Behavior = _ => new PushDeliveryResult(PushDeliveryOutcome.Expired, 410)
        };
        var service = CreateService(store, sender, new TestTimeProvider());

        var subs = new[] { Subscription() };
        await store.UpsertSubscriptionAsync(1, subs[0], TestContext.Current.CancellationToken);
        var scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "o/r" };

        var stats = new DetectorCycleStats();
        await service.ProcessUserAsync(1, subs, [Candidate(1, "o/r", 5)], scanned, stats, TestContext.Current.CancellationToken);

        Assert.Equal(1, stats.SubscriptionsPruned);
        Assert.Empty(await store.GetSubscriptionsAsync(1, TestContext.Current.CancellationToken));
    }
}

public sealed class CiBuildBrokenDetectionTests
{
    [Fact]
    public void EventKeyNormalizesRepoAndKeepsLane()
    {
        Assert.Equal("ci_build_broken:microsoft/aspire#CI · release/13.4",
            CiBuildBrokenDetection.EventKey("Microsoft/Aspire", "CI · release/13.4"));
    }

    [Fact]
    public void TryGetRepositoryRecoversSlugAcrossLaneSeparators()
    {
        // The lane itself contains '/', so parsing must stop at the first '#'.
        Assert.True(CiBuildBrokenDetection.TryGetRepository("ci_build_broken:microsoft/aspire#CI · release/13.4", out var repo));
        Assert.Equal("microsoft/aspire", repo);
        Assert.False(CiBuildBrokenDetection.TryGetRepository("review_requested:o/r#5", out _));
        Assert.False(CiBuildBrokenDetection.TryGetRepository("ci_build_broken:no-hash", out _));
    }

    [Fact]
    public void IsMainBuildAcceptsMainSectionOnly()
    {
        Assert.True(CiBuildBrokenDetection.IsMainBuild(Failing("CI · main", WorkflowLane.MainSection)));
        Assert.False(CiBuildBrokenDetection.IsMainBuild(Failing("Deployment E2E Tests", WorkflowLane.ScheduledSection)));
    }

    [Fact]
    public void DetectBroadcastsEveryMainLaneToEveryEnabledUser()
    {
        var snapshot = new CiHealthPulseSnapshot(
            Workflows: [],
            FailingNow:
            [
                Failing("CI · main", WorkflowLane.MainSection, "o/r"),
                Failing("CI · release/13.4", WorkflowLane.MainSection, "o/r"),
                Failing("Deployment E2E Tests", WorkflowLane.ScheduledSection, "o/r"),
            ],
            BotPrs: [],
            BotIssues: [],
            UpdatedAt: DateTimeOffset.UnixEpoch);

        var detected = CiBuildBrokenDetection
            .DetectFromSnapshot(snapshot, new HashSet<long> { 1, 2 })
            .ToList();

        // 2 main lanes × 2 users; the scheduled lane is excluded.
        Assert.Equal(4, detected.Count);
        Assert.All(detected, candidate => Assert.NotEqual("Deployment E2E Tests", candidate.Lane));
        Assert.Contains(detected, candidate => candidate.UserId == 1 && candidate.Lane == "CI · main");
        Assert.Contains(detected, candidate => candidate.UserId == 2 && candidate.Lane == "CI · release/13.4");
    }

    [Fact]
    public void DetectYieldsNothingWithoutSnapshotOrUsers()
    {
        Assert.Empty(CiBuildBrokenDetection.DetectFromSnapshot(null, new HashSet<long> { 1 }));
        var snapshot = new CiHealthPulseSnapshot([], [Failing("CI · main", WorkflowLane.MainSection)], [], [], DateTimeOffset.UnixEpoch);
        Assert.Empty(CiBuildBrokenDetection.DetectFromSnapshot(snapshot, new HashSet<long>()));
    }

    private static FailingWorkflow Failing(string lane, string section, string repository = "o/r") =>
        new(
            repository,
            Workflow: lane,
            Lane: lane,
            Section: section,
            FailingSince: DateTimeOffset.UnixEpoch,
            Streak: 1,
            LastRunId: 1,
            LastRunUrl: "https://example/run",
            LikelyReal: false,
            LinkedIssue: null);
}
