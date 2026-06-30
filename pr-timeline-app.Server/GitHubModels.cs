using System.Net;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

readonly partial record struct RepositoryName(string Owner, string Name)
{
    public static bool TryParse(string value, out RepositoryName repositoryName)
    {
        repositoryName = default;

        if (RepositoryRegex().Match(value.Trim()) is not { Success: true } match)
        {
            return false;
        }

        repositoryName = new RepositoryName(match.Groups["owner"].Value, match.Groups["repo"].Value);
        return true;
    }

    public override string ToString() => $"{Owner}/{Name}";

    [GeneratedRegex("^(?<owner>[A-Za-z0-9._-]+)/(?<repo>[A-Za-z0-9._-]+)$")]
    private static partial Regex RepositoryRegex();
}

sealed class GitHubApiException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

record TokenResult(string Value, string Source);

record AuthStatusResponse(bool Authenticated, bool Configured, bool CanLogin, string? Source, string? Login, string Message);

record PullRequestListResponse(
    string Repository,
    IReadOnlyList<PullRequestSummary> PullRequests,
    PullRequestListSnapshot? Snapshot = null);

record PullRequestListSnapshot(
    string Source,
    DateTimeOffset FetchedAt,
    bool Stale,
    bool RefreshInProgress,
    bool RefreshQueued,
    string? Error = null);

record IssueListResponse(string Repository, IReadOnlyList<ShipWeekIssueSummary> Issues);

record PullRequestStreamItem(string Repository, PullRequestSummary? PullRequest, bool IsStale = false, bool IsComplete = false);

readonly record struct PullRequestStreamEntry(
    PullRequestSummary? PullRequest = null,
    bool IsStale = false,
    bool IsComplete = false,
    bool IsStaleRefreshOverlay = false);

record PullRequestChecksRequest(IReadOnlyList<PullRequestChecksRequestItem>? PullRequests);

record PullRequestChecksRequestItem(int Number, string? HeadSha);

record PullRequestChecksResponse(string Repository, IReadOnlyList<PullRequestChecksSummary> PullRequests);

record PullRequestChecksSummary(int Number, string HeadSha, ChecksStatus Checks);

record ShipWeekLoadResult(ShipWeekResponse? Response, IReadOnlyDictionary<string, string[]> ValidationErrors)
{
    public static ShipWeekLoadResult Success(ShipWeekResponse response) => new(response, new Dictionary<string, string[]>());

    public static ShipWeekLoadResult ValidationProblem(string field, string message) =>
        new(null, new Dictionary<string, string[]> { [field] = [message] });
}

record ShipWeekResponse(
    string Repository,
    string Milestone,
    string ReleaseBranch,
    IReadOnlyList<ShipWeekPullRequestSummary> PullRequests,
    IReadOnlyList<ShipWeekIssueSummary> Issues);

record ShipWeekPullRequestSummary(PullRequestSummary PullRequest, ShipWeekReleaseScope ReleaseScope);

record ShipWeekReleaseScope(
    bool InMilestone,
    bool TargetsReleaseBranch,
    bool ReleaseBranchException,
    IReadOnlyList<int> MilestoneIssueNumbers,
    bool DocsFromCode = false);

readonly record struct LinkedOpenPullRequestSummary(int Number, DateTimeOffset UpdatedAt);

record ShipWeekIssueSummary(
    string Repository,
    int Number,
    string Title,
    string HtmlUrl,
    string Author,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> Assignees,
    string? Milestone,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<int> LinkedOpenPullRequests)
{
    public DateTimeOffset FetchedAt { get; init; }

    public static ShipWeekIssueSummary FromDto(
        RepositoryName repositoryName,
        GitHubIssueDto issue,
        IReadOnlyList<LinkedOpenPullRequestSummary> linkedOpenPullRequests) =>
        new(
            repositoryName.ToString(),
            issue.Number,
            issue.Title ?? "",
            issue.HtmlUrl ?? "",
            issue.User?.Login ?? "unknown",
            issue.Labels
                .Select(label => label.Name)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Select(label => label!)
                .ToArray(),
            issue.Assignees
                .Select(assignee => assignee.Login)
                .Where(assignee => !string.IsNullOrWhiteSpace(assignee))
                .Select(assignee => assignee!)
                .ToArray(),
            issue.Milestone?.Title,
            ResolveUpdatedAt(issue.UpdatedAt, linkedOpenPullRequests),
            linkedOpenPullRequests.Select(pullRequest => pullRequest.Number).ToArray());

    private static DateTimeOffset ResolveUpdatedAt(
        DateTimeOffset issueUpdatedAt,
        IReadOnlyList<LinkedOpenPullRequestSummary> linkedOpenPullRequests)
    {
        var updatedAt = issueUpdatedAt;
        foreach (var pullRequest in linkedOpenPullRequests)
        {
            if (pullRequest.UpdatedAt > updatedAt)
            {
                updatedAt = pullRequest.UpdatedAt;
            }
        }

        return updatedAt;
    }
}

record PullRequestSummary(
    int Number,
    string Title,
    string State,
    bool Draft,
    string Author,
    string HtmlUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> RequestedReviewers,
    string? Milestone,
    IReadOnlyList<LinkedIssueSummary> LinkedIssues,
    int CommitCount,
    int Additions,
    int Deletions,
    int ChangedFiles,
    DateTimeOffset? LastCommitAt,
    string? HeadSha,
    string? BaseRef,
    string? MergeableState,
    ReviewStatus Review,
    ChecksStatus Checks)
{
    public DateTimeOffset FetchedAt { get; init; }

    public IReadOnlyList<long> RequestedReviewerIds { get; init; } = [];

    // GitHub numeric id of the human who owns the PR: the author for a human-authored PR, or the
    // sole human assignee for a Copilot-authored PR (mirrors ResolveAuthor's attribution).
    // Used to avoid notifying that human to "review" their own work. Null when no unambiguous
    // human owner exists (e.g. a Copilot PR with zero or multiple human assignees).
    public long? OwnerUserId { get; init; }

    public static PullRequestSummary FromDto(GitHubPullRequestDto pullRequest)
    {
        var requestedReviewerLogins = pullRequest.RequestedReviewers
            .Select(reviewer => reviewer.Login)
            .Where(login => !string.IsNullOrWhiteSpace(login))
            .Select(login => login!)
            .ToArray();

        return new(
            pullRequest.Number,
            pullRequest.Title ?? "",
            pullRequest.State ?? "",
            pullRequest.Draft,
            ResolveAuthor(
                pullRequest.User?.Login,
                pullRequest.Assignees?
                    .Select(assignee => assignee.Login)
                ?? []),
            pullRequest.HtmlUrl ?? "",
            pullRequest.CreatedAt,
            pullRequest.UpdatedAt,
            pullRequest.Labels
                .Select(label => label.Name)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Select(label => label!)
                .ToArray(),
            // Only individual reviewer logins — team names are intentionally excluded so
            // frontend "for me" checks cannot false-positive on a team named like a user.
            requestedReviewerLogins,
            pullRequest.Milestone?.Title,
            [],
            pullRequest.Commits,
            pullRequest.Additions,
            pullRequest.Deletions,
            pullRequest.ChangedFiles,
            null,
            pullRequest.Head?.Sha,
            pullRequest.Base?.Ref,
            pullRequest.MergeableState,
            ReviewStatus.Waiting,
            pullRequest.State?.Equals("open", StringComparison.OrdinalIgnoreCase) is true
                && !string.IsNullOrEmpty(pullRequest.Head?.Sha)
                    ? ChecksStatus.Unknown
                    : ChecksStatus.None)
        {
            // Notification delivery uses stable numeric user ids instead of mutable logins.
            RequestedReviewerIds = pullRequest.RequestedReviewers
                .Select(reviewer => reviewer.Id)
                .Where(id => id is > 0)
                .Select(id => id!.Value)
                .Distinct()
                .ToArray(),
            OwnerUserId = ResolveOwnerUserId(pullRequest)
        };
    }

    // The human who owns the PR, for self-notification suppression. Keep this in lockstep with
    // ResolveAuthor: a human author owns their own PR; a Copilot-authored PR is owned by its
    // sole human assignee (ambiguous when there are zero or several, so no owner is returned).
    private static long? ResolveOwnerUserId(GitHubPullRequestDto pullRequest) =>
        ResolveOwnerUserId(
            pullRequest.User?.Login,
            pullRequest.User?.Id,
            (pullRequest.Assignees ?? []).Select(assignee => (assignee.Login, assignee.Id)));

    public static long? ResolveOwnerUserId(string? authorLogin, long? authorId, IEnumerable<(string? Login, long? Id)> assignees)
    {
        if (!IsCopilotLogin(authorLogin) && authorId is > 0)
        {
            return authorId;
        }

        var humanAssigneeIds = assignees
            .Where(assignee => !IsCopilotLogin(assignee.Login) && assignee.Id is > 0)
            .Select(assignee => assignee.Id!.Value)
            .Distinct()
            .ToArray();

        return humanAssigneeIds.Length == 1 ? humanAssigneeIds[0] : null;
    }

    public static string ResolveAuthor(string? login, IEnumerable<string?> assigneeLogins)
    {
        if (string.IsNullOrWhiteSpace(login))
        {
            return "unknown";
        }

        if (!IsCopilotLogin(login))
        {
            return login;
        }

        // Copilot-authored PRs are assigned to both Copilot and the human who started the work.
        // Attribute the PR to that human while keeping the Copilot marker, but only when there is
        // exactly one human assignee; otherwise the human is ambiguous, so keep the Copilot login.
        var humans = assigneeLogins
            .Where(assignee => !string.IsNullOrWhiteSpace(assignee) && !IsCopilotLogin(assignee))
            .ToArray();

        return humans.Length == 1 ? $"{humans[0]}/copilot" : login;
    }

    private static bool IsCopilotLogin(string? login)
    {
        if (string.IsNullOrWhiteSpace(login))
        {
            return false;
        }

        var normalized = login.Trim().ToLowerInvariant();
        return normalized == "copilot"
            || (normalized.StartsWith("copilot") && normalized.EndsWith("[bot]"));
    }
}

record LinkedIssueSummary(
    string Repository,
    int Number,
    string Title,
    string? Milestone,
    IReadOnlyList<string> Labels,
    string HtmlUrl)
{
    public static LinkedIssueSummary FromDto(RepositoryName repositoryName, GitHubIssueDto issue) =>
        new(
            repositoryName.ToString(),
            issue.Number,
            issue.Title ?? "",
            issue.Milestone?.Title,
            issue.Labels
                .Select(label => label.Name)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Select(label => label!)
                .ToArray(),
            issue.HtmlUrl ?? "");
}

record ReviewStatus(
    string State,
    string? LatestState,
    int ReviewerCount,
    int ApprovalCount,
    int ChangesRequestedCount,
    int CommentedReviewCount,
    DateTimeOffset? LastApprovedAt,
    DateTimeOffset? LastReviewedAt,
    int UnresolvedThreadCount = 0,
    bool RequiresConversationResolution = false)
{
    // Numeric GitHub ids of the reviewers whose latest review is an approval. Used by the
    // notification detector to nag approvers when a PR is ready to merge; empty unless the
    // review source supplied reviewer ids.
    public IReadOnlyList<long> ApprovedReviewerIds { get; init; } = [];

    public static ReviewStatus Waiting { get; } = new(
        State: "waiting",
        LatestState: null,
        ReviewerCount: 0,
        ApprovalCount: 0,
        ChangesRequestedCount: 0,
        CommentedReviewCount: 0,
        LastApprovedAt: null,
        LastReviewedAt: null);
}

record ChecksStatus(
    string State,
    int TotalCount,
    int SuccessCount,
    int FailureCount,
    int PendingCount,
    int NeutralCount,
    int SkippedCount,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<FailingCheck> FailingChecks)
{
    public static ChecksStatus Unknown { get; } = new(
        State: "unknown",
        TotalCount: 0,
        SuccessCount: 0,
        FailureCount: 0,
        PendingCount: 0,
        NeutralCount: 0,
        SkippedCount: 0,
        CompletedAt: null,
        FailingChecks: []);

    public static ChecksStatus None { get; } = new(
        State: "none",
        TotalCount: 0,
        SuccessCount: 0,
        FailureCount: 0,
        PendingCount: 0,
        NeutralCount: 0,
        SkippedCount: 0,
        CompletedAt: null,
        FailingChecks: []);
}

record FailingCheck(string Name, string? Conclusion, string? HtmlUrl);

record ReviewEvent(string Actor, string State, DateTimeOffset SubmittedAt, long? ActorId = null)
{
    public static ReviewEvent FromDto(GitHubReviewDto review) =>
        new(
            Actor: review.User?.Login ?? "unknown",
            State: review.State ?? "UNKNOWN",
            SubmittedAt: review.SubmittedAt,
            ActorId: review.User?.Id);

    public static ReviewEvent? FromGraphQl(GitHubGraphQlReviewNodeDto review) =>
        review.SubmittedAt is { } submittedAt
            ? new(
                Actor: review.Author?.Login ?? "unknown",
                State: review.State ?? "UNKNOWN",
                SubmittedAt: submittedAt,
                ActorId: review.Author?.DatabaseId)
            : null;
}

record PullRequestDetails(
    DateTimeOffset CreatedAt,
    string Author,
    DateTimeOffset? MergedAt,
    int CommitCount,
    int Additions,
    int Deletions,
    int ChangedFiles,
    string? HeadSha,
    string? MergeableState)
{
    public static PullRequestDetails FromDto(GitHubPullRequestDto pullRequest) =>
        new(
            pullRequest.CreatedAt,
            pullRequest.User?.Login ?? "unknown",
            pullRequest.MergedAt,
            pullRequest.Commits,
            pullRequest.Additions,
            pullRequest.Deletions,
            pullRequest.ChangedFiles,
            pullRequest.Head?.Sha,
            pullRequest.MergeableState);
}

record TimelineResponse(
    string Repository,
    int Number,
    TimelineStats Stats,
    ChecksStatus Checks,
    string? MergeableState,
    IReadOnlyList<TimelineItem> Items);

record TimelineStats(
    int CommitCount,
    int HumanCommenterCount,
    int HumanCommentCount,
    int ReviewCount,
    int ApprovalCount,
    double? FirstHumanCommentDelayMs,
    double? FirstReviewDelayMs,
    double? FirstApprovalDelayMs,
    double? ApprovalToMergeDelayMs,
    double? CreatedToMergeDelayMs,
    double? AverageHumanCommentGapMs,
    double? LongestHumanCommentGapMs,
    DateTimeOffset? MergedAt,
    IReadOnlyList<DeveloperStats> Developers)
{
    public static TimelineStats Create(PullRequestDetails pullRequest, IReadOnlyList<TimelineItem> timeline)
    {
        var humanComments = timeline
            .Where(item => item.Event == "commented"
                && IsHuman(item.Actor)
                && !SameActor(item.Actor, pullRequest.Author))
            .OrderBy(item => item.OccurredAt)
            .ToArray();

        var humanReviews = timeline
            .Where(item => item.Event == "reviewed" && IsHuman(item.Actor))
            .OrderBy(item => item.OccurredAt)
            .ToArray();

        var approvals = humanReviews
            .Where(item => item.State?.Equals("APPROVED", StringComparison.OrdinalIgnoreCase) is true)
            .ToArray();

        var mergedAt = pullRequest.MergedAt
            ?? timeline.FirstOrDefault(item => item.Event == "merged")?.OccurredAt;
        var lastApprovalBeforeMerge = mergedAt is null
            ? null
            : approvals.LastOrDefault(item => item.OccurredAt <= mergedAt.Value);

        return new TimelineStats(
            CommitCount: pullRequest.CommitCount,
            HumanCommenterCount: humanComments.Select(item => NormalizeActorIdentity(item.Actor)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            HumanCommentCount: humanComments.Length,
            ReviewCount: humanReviews.Length,
            ApprovalCount: approvals.Length,
            FirstHumanCommentDelayMs: DelayMs(pullRequest.CreatedAt, humanComments.FirstOrDefault()?.OccurredAt),
            FirstReviewDelayMs: DelayMs(pullRequest.CreatedAt, humanReviews.FirstOrDefault()?.OccurredAt),
            FirstApprovalDelayMs: DelayMs(pullRequest.CreatedAt, approvals.FirstOrDefault()?.OccurredAt),
            ApprovalToMergeDelayMs: DelayMs(lastApprovalBeforeMerge?.OccurredAt, mergedAt),
            CreatedToMergeDelayMs: DelayMs(pullRequest.CreatedAt, mergedAt),
            AverageHumanCommentGapMs: AverageGapMs(humanComments),
            LongestHumanCommentGapMs: LongestGapMs(humanComments),
            MergedAt: mergedAt,
            Developers: CreateDeveloperStats(timeline));
    }

    private static IReadOnlyList<DeveloperStats> CreateDeveloperStats(IReadOnlyList<TimelineItem> timeline) =>
        timeline
            .Where(item => IsHuman(item.Actor))
            .GroupBy(item => NormalizeActorIdentity(item.Actor), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var ordered = group.OrderBy(item => item.OccurredAt).ToArray();
                return new DeveloperStats(
                    Actor: PreferredActorName(ordered.Select(item => item.Actor)),
                    ActivityCount: ordered.Length,
                    CommitCount: ordered.Count(item => item.Event == "committed"),
                    CommentCount: ordered.Count(item => item.Event == "commented"),
                    ReviewCount: ordered.Count(item => item.Event == "reviewed"),
                    ApprovalCount: ordered.Count(item => item.Event == "reviewed" && item.State?.Equals("APPROVED", StringComparison.OrdinalIgnoreCase) is true),
                    ChangesRequestedCount: ordered.Count(item => item.Event == "reviewed" && item.State?.Equals("CHANGES_REQUESTED", StringComparison.OrdinalIgnoreCase) is true),
                    FirstActivityAt: ordered.First().OccurredAt,
                    LastActivityAt: ordered.Last().OccurredAt);
            })
            .OrderByDescending(developer => developer.ActivityCount)
            .ThenBy(developer => developer.Actor)
            .ToArray();

    private static bool IsHuman(string actor) =>
        !string.IsNullOrWhiteSpace(actor)
        && !actor.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase)
        && !s_knownBotActors.Contains(actor);

    private static bool SameActor(string first, string second) =>
        NormalizeActorIdentity(first).Equals(NormalizeActorIdentity(second), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeActorIdentity(string actor) =>
        string.Concat(actor.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static string PreferredActorName(IEnumerable<string> actors) =>
        actors
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(actor => actor.Any(char.IsWhiteSpace))
            .ThenBy(actor => actor.Length)
            .First();

    private static double? DelayMs(DateTimeOffset? start, DateTimeOffset? end) =>
        start is null || end is null ? null : Math.Max(0, (end.Value - start.Value).TotalMilliseconds);

    private static double? AverageGapMs(IReadOnlyList<TimelineItem> items)
    {
        if (items.Count < 2)
        {
            return null;
        }

        return items.Zip(items.Skip(1), (first, second) => (second.OccurredAt - first.OccurredAt).TotalMilliseconds)
            .Average();
    }

    private static double? LongestGapMs(IReadOnlyList<TimelineItem> items)
    {
        if (items.Count < 2)
        {
            return null;
        }

        return items.Zip(items.Skip(1), (first, second) => (second.OccurredAt - first.OccurredAt).TotalMilliseconds)
            .Max();
    }

    private static readonly HashSet<string> s_knownBotActors = new(StringComparer.OrdinalIgnoreCase)
    {
        "Copilot",
        "dependabot",
        "dependabot-preview",
        "github-actions",
        "renovate"
    };
}

record DeveloperStats(
    string Actor,
    int ActivityCount,
    int CommitCount,
    int CommentCount,
    int ReviewCount,
    int ApprovalCount,
    int ChangesRequestedCount,
    DateTimeOffset FirstActivityAt,
    DateTimeOffset LastActivityAt);

record TimelineItem(
    string Id,
    string Event,
    string Actor,
    DateTimeOffset OccurredAt,
    string? State,
    string Summary,
    string? Body,
    string? HtmlUrl)
{
    public static TimelineItem FromDto(GitHubTimelineItemDto item) =>
        FromDto(item, commitAuthorLoginsBySha: null);

    // commitAuthorLoginsBySha maps a commit SHA to the GitHub login that authored it. The REST
    // issues-timeline "committed" event only carries the raw git author name (no login/id), so
    // without this map a commit surfaces under the git name (e.g. "Ankit Jain") and is counted as
    // a different person from that same user's logged-in activity (e.g. "radical"). Resolving the
    // login here collapses them into one participant.
    public static TimelineItem FromDto(
        GitHubTimelineItemDto item,
        IReadOnlyDictionary<string, string>? commitAuthorLoginsBySha)
    {
        var eventName = item.Event ?? "event";
        var occurredAt = item.CreatedAt
            ?? item.SubmittedAt
            ?? item.CommittedAt
            ?? item.Author?.Date
            ?? item.Committer?.Date
            ?? DateTimeOffset.MinValue;

        string? commitAuthorLogin = null;
        if (commitAuthorLoginsBySha is not null
            && item.Sha is { Length: > 0 } sha)
        {
            commitAuthorLoginsBySha.TryGetValue(sha, out commitAuthorLogin);
        }

        var actor = item.Actor?.Login
            ?? item.User?.Login
            ?? item.Author?.Login
            ?? commitAuthorLogin
            ?? item.Author?.Name
            ?? item.Committer?.Login
            ?? item.Committer?.Name
            ?? "unknown";

        return new TimelineItem(
            Id: item.Id?.ToString(CultureInfo.InvariantCulture) ?? item.Sha ?? $"{eventName}-{occurredAt.ToUnixTimeMilliseconds()}",
            Event: eventName,
            Actor: actor,
            OccurredAt: occurredAt,
            State: item.State,
            Summary: BuildSummary(item, eventName, actor),
            Body: item.Body,
            HtmlUrl: item.HtmlUrl);
    }

    private static string BuildSummary(GitHubTimelineItemDto item, string eventName, string actor)
    {
        var normalizedEvent = eventName.Replace('_', ' ');
        return eventName switch
        {
            "commented" => $"{actor} commented",
            "committed" => $"{actor} pushed commit {ShortSha(item.Sha ?? item.CommitId)}",
            "reviewed" => $"{actor} reviewed with state {item.State ?? "unknown"}",
            "review_requested" => $"{actor} requested review from {item.RequestedReviewer?.Login ?? item.RequestedTeam?.Name ?? "someone"}",
            "ready_for_review" => $"{actor} marked the PR ready for review",
            "converted_to_draft" => $"{actor} converted the PR to draft",
            "labeled" => $"{actor} added label {item.Label?.Name ?? "unknown"}",
            "unlabeled" => $"{actor} removed label {item.Label?.Name ?? "unknown"}",
            "assigned" => $"{actor} assigned {item.Assignee?.Login ?? "someone"}",
            "unassigned" => $"{actor} unassigned {item.Assignee?.Login ?? "someone"}",
            "cross-referenced" => $"{actor} cross-referenced another issue or PR",
            "renamed" => $"{actor} renamed the title",
            "closed" => $"{actor} closed the PR",
            "reopened" => $"{actor} reopened the PR",
            "merged" => $"{actor} merged the PR",
            _ => $"{actor} {normalizedEvent}"
        };
    }

    private static string ShortSha(string? sha) => string.IsNullOrWhiteSpace(sha)
        ? "unknown"
        : sha[..Math.Min(7, sha.Length)];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GitHubActorDto))]
[JsonSerializable(typeof(GitHubCheckRunsResponseDto))]
[JsonSerializable(typeof(GitHubCombinedStatusDto))]
[JsonSerializable(typeof(GitHubErrorDto))]
[JsonSerializable(typeof(GitHubBranchDto))]
[JsonSerializable(typeof(GitHubBranchDto[]))]
[JsonSerializable(typeof(GitHubGitReferenceDto[]))]
[JsonSerializable(typeof(GitHubIssueDto))]
[JsonSerializable(typeof(GitHubIssueDto[]))]
[JsonSerializable(typeof(GitHubIssueSearchResponseDto))]
[JsonSerializable(typeof(GitHubIssuePullRequestDto))]
[JsonSerializable(typeof(GitHubLabelDto[]))]
[JsonSerializable(typeof(GitHubMilestoneDto))]
[JsonSerializable(typeof(GitHubMilestoneDto[]))]
[JsonSerializable(typeof(GitHubPullRequestCommitDto[]))]
[JsonSerializable(typeof(GitHubPullRequestDto))]
[JsonSerializable(typeof(GitHubPullRequestDto[]))]
[JsonSerializable(typeof(GitHubReviewDto[]))]
[JsonSerializable(typeof(GitHubGraphQlRequestDto))]
[JsonSerializable(typeof(GitHubPullRequestsGraphQlRequestDto))]
[JsonSerializable(typeof(GitHubPullRequestsGraphQlResponseDto))]
[JsonSerializable(typeof(GitHubLinkedPullRequestsGraphQlRequestDto))]
[JsonSerializable(typeof(GitHubReviewThreadsResponseDto))]
[JsonSerializable(typeof(GitHubLinkedPullRequestsResponseDto))]
[JsonSerializable(typeof(GitHubRepositoryDto))]
[JsonSerializable(typeof(GitHubTimelineItemDto[]))]
[JsonSerializable(typeof(GitHubWorkflowRunsResponseDto))]
[JsonSerializable(typeof(GitHubWorkflowRunDto))]
[JsonSerializable(typeof(GitHubWorkflowDefinitionsResponseDto))]
[JsonSerializable(typeof(GitHubWorkflowDefinitionDto))]
partial class GitHubJsonSerializerContext : JsonSerializerContext;

sealed class GitHubActorDto
{
    public long? Id { get; init; }
    public string? Login { get; init; }
    public string? Name { get; init; }
    public DateTimeOffset? Date { get; init; }
}

sealed class GitHubErrorDto
{
    public string? Message { get; init; }
}

sealed class GitHubRepositoryDto
{
    public string? Visibility { get; init; }
}

sealed class GitHubLabelDto
{
    public string? Name { get; init; }
}

sealed class GitHubTeamDto
{
    public string? Name { get; init; }
}

sealed class GitHubMilestoneDto
{
    public int Number { get; init; }
    public string? Title { get; init; }
}

sealed class GitHubBranchDto
{
    public string? Name { get; init; }
}

sealed class GitHubGitReferenceDto
{
    public string? Ref { get; init; }
}

sealed class GitHubIssuePullRequestDto
{
    public string? Url { get; init; }
}

sealed class GitHubIssueDto
{
    public int Number { get; init; }
    public string? NodeId { get; init; }
    public string? Title { get; init; }
    public string? State { get; init; }
    public GitHubActorDto? User { get; init; }
    public string? HtmlUrl { get; init; }
    public string? RepositoryUrl { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public GitHubLabelDto[] Labels { get; init; } = [];
    public GitHubActorDto[] Assignees { get; init; } = [];
    public GitHubMilestoneDto? Milestone { get; init; }
    public GitHubIssuePullRequestDto? PullRequest { get; init; }
}

sealed class GitHubIssueSearchResponseDto
{
    public int TotalCount { get; init; }
    public bool IncompleteResults { get; init; }
    public GitHubIssueDto[] Items { get; init; } = [];
}

sealed class GitHubPullRequestDto
{
    public int Number { get; init; }
    public string? Title { get; init; }
    public string? State { get; init; }
    public bool Draft { get; init; }
    public GitHubActorDto? User { get; init; }
    public string? HtmlUrl { get; init; }
    public string? Body { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public GitHubLabelDto[] Labels { get; init; } = [];
    public GitHubActorDto[] Assignees { get; init; } = [];
    public GitHubActorDto[] RequestedReviewers { get; init; } = [];
    public GitHubTeamDto[] RequestedTeams { get; init; } = [];
    public GitHubMilestoneDto? Milestone { get; init; }
    public DateTimeOffset? MergedAt { get; init; }
    public int Commits { get; init; }
    public int Additions { get; init; }
    public int Deletions { get; init; }
    public int ChangedFiles { get; init; }
    public GitHubPullRequestRefDto? Head { get; init; }
    public GitHubPullRequestRefDto? Base { get; init; }
    public string? MergeableState { get; init; }
}

sealed class GitHubPullRequestRefDto
{
    public string? Sha { get; init; }
    public string? Ref { get; init; }
}

sealed class GitHubCheckRunsResponseDto
{
    public int TotalCount { get; init; }
    public GitHubCheckRunDto[] CheckRuns { get; init; } = [];
}

sealed class GitHubCheckRunDto
{
    public long Id { get; init; }
    public string? Name { get; init; }
    public string? Status { get; init; }
    public string? Conclusion { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? HtmlUrl { get; init; }
}

sealed class GitHubCombinedStatusDto
{
    public string? State { get; init; }
    public int TotalCount { get; init; }
    public GitHubStatusDto[] Statuses { get; init; } = [];
}

sealed class GitHubStatusDto
{
    public string? State { get; init; }
    public string? Context { get; init; }
    public string? Description { get; init; }
    public string? TargetUrl { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

sealed class GitHubCommitDto
{
    public GitHubActorDto? Author { get; init; }
    public GitHubActorDto? Committer { get; init; }
}

sealed class GitHubPullRequestCommitDto
{
    public string? Sha { get; init; }
    public GitHubCommitDto? Commit { get; init; }

    // Top-level author/committer carry the GitHub user (login + id) that GitHub linked to the
    // commit. The nested Commit.Author/Committer only carry the raw git identity (name/email),
    // so these are what let us resolve a commit's git name to a GitHub login.
    public GitHubActorDto? Author { get; init; }
    public GitHubActorDto? Committer { get; init; }
}

sealed class GitHubReviewDto
{
    public GitHubActorDto? User { get; init; }
    public string? State { get; init; }
    public DateTimeOffset SubmittedAt { get; init; }
}

// GitHub's GraphQL API returns camelCase fields, so these DTOs use explicit
// [JsonPropertyName] attributes rather than the snake_case policy applied to
// the REST DTOs in this serializer context.
sealed class GitHubGraphQlRequestDto
{
    [JsonPropertyName("query")]
    public string? Query { get; init; }

    [JsonPropertyName("variables")]
    public GitHubReviewThreadsVariablesDto? Variables { get; init; }
}

sealed class GitHubReviewThreadsVariablesDto
{
    [JsonPropertyName("owner")]
    public string? Owner { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("number")]
    public int Number { get; init; }

    [JsonPropertyName("after")]
    public string? After { get; init; }
}

sealed class GitHubReviewThreadsResponseDto
{
    [JsonPropertyName("data")]
    public GitHubReviewThreadsDataDto? Data { get; init; }
}

sealed class GitHubPullRequestsGraphQlRequestDto
{
    [JsonPropertyName("query")]
    public string? Query { get; init; }

    [JsonPropertyName("variables")]
    public GitHubPullRequestsGraphQlVariablesDto? Variables { get; init; }
}

sealed class GitHubPullRequestsGraphQlVariablesDto
{
    [JsonPropertyName("owner")]
    public string? Owner { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("states")]
    public IReadOnlyList<string>? States { get; init; }

    [JsonPropertyName("orderField")]
    public string? OrderField { get; init; }

    [JsonPropertyName("orderDirection")]
    public string? OrderDirection { get; init; }

    [JsonPropertyName("after")]
    public string? After { get; init; }
}

sealed class GitHubPullRequestsGraphQlResponseDto
{
    [JsonPropertyName("data")]
    public GitHubPullRequestsGraphQlDataDto? Data { get; init; }

    [JsonPropertyName("errors")]
    public IReadOnlyList<GitHubGraphQlErrorDto>? Errors { get; init; }
}

sealed class GitHubGraphQlErrorDto
{
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

sealed class GitHubPullRequestsGraphQlDataDto
{
    [JsonPropertyName("repository")]
    public GitHubPullRequestsGraphQlRepositoryDto? Repository { get; init; }
}

sealed class GitHubPullRequestsGraphQlRepositoryDto
{
    [JsonPropertyName("pullRequests")]
    public GitHubPullRequestsGraphQlConnectionDto? PullRequests { get; init; }
}

sealed class GitHubPullRequestsGraphQlConnectionDto
{
    [JsonPropertyName("pageInfo")]
    public GitHubGraphQlPageInfoDto? PageInfo { get; init; }

    [JsonPropertyName("nodes")]
    public IReadOnlyList<GitHubPullRequestGraphQlNodeDto?>? Nodes { get; init; }
}

sealed class GitHubGraphQlPageInfoDto
{
    [JsonPropertyName("hasNextPage")]
    public bool HasNextPage { get; init; }

    [JsonPropertyName("hasPreviousPage")]
    public bool HasPreviousPage { get; init; }

    [JsonPropertyName("endCursor")]
    public string? EndCursor { get; init; }

    [JsonPropertyName("startCursor")]
    public string? StartCursor { get; init; }
}

sealed class GitHubPullRequestGraphQlNodeDto
{
    [JsonPropertyName("number")]
    public int Number { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("isDraft")]
    public bool IsDraft { get; init; }

    [JsonPropertyName("author")]
    public GitHubGraphQlActorDto? Author { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("labels")]
    public GitHubGraphQlLabelConnectionDto? Labels { get; init; }

    [JsonPropertyName("assignees")]
    public GitHubGraphQlActorConnectionDto? Assignees { get; init; }

    [JsonPropertyName("reviewRequests")]
    public GitHubGraphQlReviewRequestConnectionDto? ReviewRequests { get; init; }

    [JsonPropertyName("milestone")]
    public GitHubGraphQlMilestoneDto? Milestone { get; init; }

    [JsonPropertyName("commits")]
    public GitHubGraphQlCommitConnectionDto? Commits { get; init; }

    [JsonPropertyName("additions")]
    public int Additions { get; init; }

    [JsonPropertyName("deletions")]
    public int Deletions { get; init; }

    [JsonPropertyName("changedFiles")]
    public int ChangedFiles { get; init; }

    [JsonPropertyName("headRefOid")]
    public string? HeadRefOid { get; init; }

    [JsonPropertyName("headRefName")]
    public string? HeadRefName { get; init; }

    [JsonPropertyName("baseRefName")]
    public string? BaseRefName { get; init; }

    [JsonPropertyName("mergeable")]
    public string? Mergeable { get; init; }

    [JsonPropertyName("reviews")]
    public GitHubGraphQlReviewConnectionDto? Reviews { get; init; }

    [JsonPropertyName("reviewThreads")]
    public GitHubReviewThreadsConnectionDto? ReviewThreads { get; init; }

    [JsonPropertyName("closingIssuesReferences")]
    public GitHubGraphQlLinkedIssueConnectionDto? ClosingIssuesReferences { get; init; }
}

sealed class GitHubGraphQlActorDto
{
    [JsonPropertyName("login")]
    public string? Login { get; init; }

    [JsonPropertyName("databaseId")]
    public long? DatabaseId { get; init; }
}

sealed class GitHubGraphQlLabelConnectionDto
{
    [JsonPropertyName("nodes")]
    public IReadOnlyList<GitHubLabelDto?>? Nodes { get; init; }
}

sealed class GitHubGraphQlActorConnectionDto
{
    [JsonPropertyName("nodes")]
    public IReadOnlyList<GitHubGraphQlActorDto?>? Nodes { get; init; }
}

sealed class GitHubGraphQlReviewRequestConnectionDto
{
    [JsonPropertyName("nodes")]
    public IReadOnlyList<GitHubGraphQlReviewRequestNodeDto?>? Nodes { get; init; }
}

sealed class GitHubGraphQlReviewRequestNodeDto
{
    [JsonPropertyName("requestedReviewer")]
    public GitHubGraphQlRequestedReviewerDto? RequestedReviewer { get; init; }
}

sealed class GitHubGraphQlRequestedReviewerDto
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; init; }

    [JsonPropertyName("login")]
    public string? Login { get; init; }

    [JsonPropertyName("databaseId")]
    public long? DatabaseId { get; init; }
}

sealed class GitHubGraphQlMilestoneDto
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }
}

sealed class GitHubGraphQlCommitConnectionDto
{
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }

    [JsonPropertyName("nodes")]
    public IReadOnlyList<GitHubGraphQlCommitNodeDto?>? Nodes { get; init; }
}

sealed class GitHubGraphQlCommitNodeDto
{
    [JsonPropertyName("commit")]
    public GitHubGraphQlCommitDto? Commit { get; init; }
}

sealed class GitHubGraphQlCommitDto
{
    [JsonPropertyName("committedDate")]
    public DateTimeOffset? CommittedDate { get; init; }

    [JsonPropertyName("statusCheckRollup")]
    public GitHubGraphQlStatusCheckRollupDto? StatusCheckRollup { get; init; }
}

sealed class GitHubGraphQlStatusCheckRollupDto
{
    [JsonPropertyName("state")]
    public string? State { get; init; }
}

sealed class GitHubGraphQlReviewConnectionDto
{
    [JsonPropertyName("pageInfo")]
    public GitHubGraphQlPageInfoDto? PageInfo { get; init; }

    [JsonPropertyName("nodes")]
    public IReadOnlyList<GitHubGraphQlReviewNodeDto?>? Nodes { get; init; }
}

sealed class GitHubGraphQlReviewNodeDto
{
    [JsonPropertyName("author")]
    public GitHubGraphQlActorDto? Author { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("submittedAt")]
    public DateTimeOffset? SubmittedAt { get; init; }
}

sealed class GitHubGraphQlLinkedIssueConnectionDto
{
    [JsonPropertyName("nodes")]
    public IReadOnlyList<GitHubGraphQlLinkedIssueNodeDto?>? Nodes { get; init; }
}

sealed class GitHubGraphQlLinkedIssueNodeDto
{
    [JsonPropertyName("number")]
    public int Number { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("repository")]
    public GitHubLinkedPullRequestsRepositoryDto? Repository { get; init; }

    [JsonPropertyName("labels")]
    public GitHubGraphQlLabelConnectionDto? Labels { get; init; }

    [JsonPropertyName("milestone")]
    public GitHubGraphQlMilestoneDto? Milestone { get; init; }
}

sealed class GitHubReviewThreadsDataDto
{
    [JsonPropertyName("repository")]
    public GitHubReviewThreadsRepositoryDto? Repository { get; init; }
}

sealed class GitHubReviewThreadsRepositoryDto
{
    [JsonPropertyName("pullRequest")]
    public GitHubReviewThreadsPullRequestDto? PullRequest { get; init; }
}

sealed class GitHubReviewThreadsPullRequestDto
{
    [JsonPropertyName("reviewThreads")]
    public GitHubReviewThreadsConnectionDto? ReviewThreads { get; init; }
}

sealed class GitHubReviewThreadsConnectionDto
{
    [JsonPropertyName("pageInfo")]
    public GitHubReviewThreadsPageInfoDto? PageInfo { get; init; }

    [JsonPropertyName("nodes")]
    public IReadOnlyList<GitHubReviewThreadNodeDto>? Nodes { get; init; }
}

sealed class GitHubReviewThreadsPageInfoDto
{
    [JsonPropertyName("hasNextPage")]
    public bool HasNextPage { get; init; }

    [JsonPropertyName("endCursor")]
    public string? EndCursor { get; init; }
}

sealed class GitHubReviewThreadNodeDto
{
    [JsonPropertyName("isResolved")]
    public bool IsResolved { get; init; }
}

sealed class GitHubLinkedPullRequestsGraphQlRequestDto
{
    [JsonPropertyName("query")]
    public string? Query { get; init; }

    [JsonPropertyName("variables")]
    public GitHubLinkedPullRequestsVariablesDto? Variables { get; init; }
}

sealed class GitHubLinkedPullRequestsVariablesDto
{
    [JsonPropertyName("ids")]
    public IReadOnlyList<string> Ids { get; init; } = [];
}

sealed class GitHubLinkedPullRequestsResponseDto
{
    [JsonPropertyName("data")]
    public GitHubLinkedPullRequestsDataDto? Data { get; init; }
}

sealed class GitHubLinkedPullRequestsDataDto
{
    [JsonPropertyName("nodes")]
    public IReadOnlyList<GitHubLinkedPullRequestsIssueNodeDto?>? Nodes { get; init; }
}

sealed class GitHubLinkedPullRequestsIssueNodeDto
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; init; }

    [JsonPropertyName("number")]
    public int Number { get; init; }

    [JsonPropertyName("timelineItems")]
    public GitHubLinkedPullRequestsTimelineConnectionDto? TimelineItems { get; init; }
}

sealed class GitHubLinkedPullRequestsTimelineConnectionDto
{
    [JsonPropertyName("nodes")]
    public IReadOnlyList<GitHubLinkedPullRequestsTimelineNodeDto>? Nodes { get; init; }
}

sealed class GitHubLinkedPullRequestsTimelineNodeDto
{
    [JsonPropertyName("source")]
    public GitHubLinkedPullRequestsSourceDto? Source { get; init; }
}

sealed class GitHubLinkedPullRequestsSourceDto
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; init; }

    [JsonPropertyName("number")]
    public int Number { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("isDraft")]
    public bool IsDraft { get; init; }

    [JsonPropertyName("repository")]
    public GitHubLinkedPullRequestsRepositoryDto? Repository { get; init; }
}

sealed class GitHubLinkedPullRequestsRepositoryDto
{
    [JsonPropertyName("nameWithOwner")]
    public string? NameWithOwner { get; init; }
}

sealed class GitHubTimelineItemDto
{
    public long? Id { get; init; }
    public string? Sha { get; init; }
    public string? CommitId { get; init; }
    public string? Event { get; init; }
    public GitHubActorDto? Actor { get; init; }
    public GitHubActorDto? User { get; init; }
    public GitHubActorDto? Author { get; init; }
    public GitHubActorDto? Committer { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? SubmittedAt { get; init; }
    public DateTimeOffset? CommittedAt { get; init; }
    public string? State { get; init; }
    public string? Body { get; init; }
    public string? HtmlUrl { get; init; }
    public GitHubActorDto? RequestedReviewer { get; init; }
    public GitHubTeamDto? RequestedTeam { get; init; }
    public GitHubLabelDto? Label { get; init; }
    public GitHubActorDto? Assignee { get; init; }
}

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

sealed class GitHubWorkflowDefinitionsResponseDto
{
    [System.Text.Json.Serialization.JsonPropertyName("workflows")]
    public GitHubWorkflowDefinitionDto[] Workflows { get; init; } = [];
}

sealed class GitHubWorkflowDefinitionDto
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public long Id { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string? Name { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("state")]
    public string? State { get; init; }
}
