// Pure detection helpers for the `ci_build_broken` trigger: when a main-section CI lane (a
// push rolling build on the default branch or a tracked release branch) is red at tip, alert
// every opted-in user. Unlike the per-PR triggers this is a team-wide broadcast: a broken main
// build is an ops signal, not something tied to one reviewer or author. Kept free of
// GitHub/storage/push dependencies so the criteria can be unit tested.

// One alert candidate: user `UserId` should be told that `Lane` in `Repository` is red at tip.
sealed record DetectedBuildBroken(long UserId, string Repository, string Lane);

static class CiBuildBrokenDetection
{
    public const string EventPrefix = "ci_build_broken";

    // Constant marker: presence of a state entry means "already alerted that this lane is red".
    // The entry is pruned when the lane recovers (drops out of FailingNow), so the next
    // green->red transition alerts again. This makes the trigger fire on the transition, not on
    // every cycle the lane stays red.
    public const string RedFingerprint = "red";

    // In-app destination: the CI health view, which carries the failing run link plus the triage
    // assessment, rather than bouncing straight to GitHub Actions and losing the dashboard.
    public static string DeepLink() => "/?mode=ci-health";

    // Lane names contain '/', '·' and spaces (e.g. "CI · release/13.4"); repo slugs never
    // contain '#'. So '#' is a safe separator and the repo is everything up to the first '#'.
    public static string EventKey(string repository, string lane) =>
        $"{EventPrefix}:{ReviewRequestDetection.NormalizeRepository(repository)}#{lane}";

    // Recovers the repository slug from a ci_build_broken event key so stale state for observed
    // repos can be pruned. Returns false for keys that aren't ci_build_broken events.
    public static bool TryGetRepository(string eventKey, out string repository)
    {
        repository = string.Empty;
        var prefix = EventPrefix + ":";
        if (!eventKey.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var hashIndex = eventKey.IndexOf('#', prefix.Length);
        if (hashIndex <= prefix.Length)
        {
            return false;
        }

        repository = ReviewRequestDetection.NormalizeRepository(eventKey[prefix.Length..hashIndex]);
        return repository.Length > 0;
    }

    // A "main build" is a main-section lane: a push rolling build on the default branch or a
    // tracked release branch. Scheduled and PR lanes are excluded. LikelyReal is deliberately
    // NOT required: the alert fires on the first red (a single failing run), which is the
    // green->red transition the team cares about.
    public static bool IsMainBuild(FailingWorkflow failing) =>
        string.Equals(failing.Section, WorkflowLane.MainSection, StringComparison.OrdinalIgnoreCase);

    // Broadcast: yield a candidate for every enabled user for each main lane red at tip.
    public static IEnumerable<DetectedBuildBroken> DetectFromSnapshot(
        CiHealthPulseSnapshot? snapshot,
        IReadOnlySet<long> enabledUserIds)
    {
        if (snapshot is null || enabledUserIds.Count == 0)
        {
            yield break;
        }

        foreach (var failing in snapshot.FailingNow)
        {
            if (!IsMainBuild(failing))
            {
                continue;
            }

            foreach (var userId in enabledUserIds)
            {
                if (userId > 0)
                {
                    yield return new DetectedBuildBroken(userId, failing.Repository, failing.Lane);
                }
            }
        }
    }
}
