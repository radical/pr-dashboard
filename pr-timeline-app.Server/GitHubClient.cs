using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;

sealed partial class GitHubClient(
    HttpClient httpClient,
    GitHubTokenProvider tokenProvider,
    GitHubPublicCacheIdentity publicCacheIdentity,
    GitHubPublicCacheStore publicCacheStore,
    GitHubCacheScopeResolver cacheScopeResolver,
    GitHubResponseCache cache,
    GitHubPullRequestGraphQlState graphQlState,
    IHostEnvironment environment,
    IOptions<GitHubReviewPolicyOptions> reviewPolicyOptions)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(45);
    internal static readonly TimeSpan PublicCacheDuration = TimeSpan.FromHours(1);
    private static readonly TimeSpan LastGoodCacheDuration = TimeSpan.FromHours(24);
    private static readonly TimeSpan PullRequestGraphQlStateDuration = LastGoodCacheDuration;
    private static readonly TimeSpan MaxStaleDisplayAge = TimeSpan.FromMinutes(10);
    private const int PullRequestPageSize = 100;
    private const int GraphQlLabelPageSize = 100;
    private const int PullRequestStreamBatchSize = 20;
    private const int MaxLinkedIssuesPerPullRequest = 10;
    private static readonly string PullRequestsGraphQlQuery =
        "query PullRequestsForDashboard($owner:String!,$name:String!,$states:[PullRequestState!],$after:String,$orderField:IssueOrderField!,$orderDirection:OrderDirection!){" +
        "repository(owner:$owner,name:$name){" +
        "pullRequests(first:" + PullRequestPageSize + ",after:$after,states:$states,orderBy:{field:$orderField,direction:$orderDirection}){" +
        "pageInfo{hasNextPage endCursor}" +
        "nodes{" +
        "number title state isDraft author{login ... on User{databaseId}} url createdAt updatedAt " +
        "labels(first:" + GraphQlLabelPageSize + "){nodes{name}} " +
        "assignees(first:10){nodes{login databaseId}} " +
        "reviewRequests(first:100){nodes{requestedReviewer{__typename ... on User{login databaseId}}}} " +
        "milestone{title} " +
        "commits(last:1){totalCount nodes{commit{committedDate " +
        "statusCheckRollup{state}}}} " +
        "additions deletions changedFiles headRefOid headRefName baseRefName mergeable " +
        "reviews(last:100){pageInfo{hasPreviousPage startCursor}nodes{author{login ... on User{databaseId}}state submittedAt}} " +
        "reviewThreads(first:" + PullRequestPageSize + "){pageInfo{hasNextPage endCursor}nodes{isResolved}} " +
        "closingIssuesReferences(first:" + MaxLinkedIssuesPerPullRequest + "){nodes{number title url repository{nameWithOwner} labels(first:" + GraphQlLabelPageSize + "){nodes{name}} milestone{title}}}" +
        "}}}}";
    private const int FocusIssueGraphQlBatchSize = 20;
    private const int FocusIssueCrossReferenceEventLimit = 20;
    private static readonly string FocusIssueLinkedPullRequestsGraphQlQuery =
        "query($ids:[ID!]!){" +
        "nodes(ids:$ids){" +
        "__typename " +
        "... on Issue{" +
        "number " +
        "timelineItems(last:" + FocusIssueCrossReferenceEventLimit + ",itemTypes:[CROSS_REFERENCED_EVENT]){" +
        "nodes{... on CrossReferencedEvent{source{__typename ... on PullRequest{number updatedAt state isDraft repository{nameWithOwner}}}}}}}}}";
    internal const int MaxConcurrentGitHubRequests = 8;
    private const int MaxConcurrentChecksFetches = 4;
    private const string RegressionLabelMarker = "regression";
    private const string CtiTeamTitleMarker = "[AspireE2E]";
    private const string CtiTeamSearchTerm = "AspireE2E";
    private static readonly SemaphoreSlim s_githubRequestThrottle = new(MaxConcurrentGitHubRequests, MaxConcurrentGitHubRequests);
    private static readonly SemaphoreSlim s_checksFetchThrottle = new(MaxConcurrentChecksFetches, MaxConcurrentChecksFetches);
    private static readonly TimeSpan PullRequestGraphQlRefreshFailureCooldown = TimeSpan.FromSeconds(30);

    private readonly HashSet<string> _conversationResolutionRepositories =
        new(reviewPolicyOptions.Value.RequireConversationResolution, StringComparer.OrdinalIgnoreCase);

    private bool RequiresConversationResolution(RepositoryName repositoryName) =>
        _conversationResolutionRepositories.Contains(repositoryName.ToString());

    private async Task<GitHubCacheScope> GetRepositoryCacheScopeAsync(
        RepositoryName repositoryName,
        CancellationToken cancellationToken) =>
        await cacheScopeResolver.GetRepositoryScopeAsync(repositoryName, cancellationToken);

    private async Task<RepositoryCacheScopeSelection> GetRepositoryCacheScopeSelectionAsync(
        RepositoryName repositoryName,
        bool forceRefresh,
        CancellationToken cancellationToken,
        bool allowSharedLiveFetch = false)
    {
        var scope = await GetRepositoryCacheScopeAsync(repositoryName, cancellationToken);
        if (scope.IsShared)
        {
            return new RepositoryCacheScopeSelection(
                scope,
                SharedFallbackScope: null,
                Refresh: allowSharedLiveFetch && forceRefresh,
                CacheOnly: !allowSharedLiveFetch);
        }

        var sharedFallbackScope = await GetSharedFallbackScopeAsync(repositoryName, cancellationToken);
        return new RepositoryCacheScopeSelection(
            scope,
            SharedFallbackScope: sharedFallbackScope,
            Refresh: forceRefresh,
            CacheOnly: false);
    }

    private async Task<GitHubCacheScope?> GetSharedFallbackScopeAsync(
        RepositoryName repositoryName,
        CancellationToken cancellationToken)
    {
        if (!cacheScopeResolver.IsPublicCacheAllowlisted(repositoryName))
        {
            return null;
        }

        return await publicCacheStore.HasTrackedSnapshotAsync(repositoryName, cancellationToken)
            ? GitHubCachePolicy.CreatePublicRepositoryScope()
            : null;
    }

    private static string CreateRepositoryCacheKey(
        GitHubCacheScope scope,
        RepositoryName repositoryName,
        string resourceName,
        params string?[] parts) =>
        GitHubCachePolicy.CreateRepositoryCacheKey(
            scope,
            repositoryName,
            resourceName,
            parts);

    private static string? CreateSharedFallbackCacheKey(
        GitHubCacheScope? sharedFallbackScope,
        RepositoryName repositoryName,
        string resourceName,
        params string?[] parts) =>
        sharedFallbackScope is { } scope
            ? CreateRepositoryCacheKey(scope, repositoryName, resourceName, parts)
            : null;

    private static async IAsyncEnumerable<PullRequestSummary> SelectPullRequestsAsync(
        IAsyncEnumerable<PullRequestStreamEntry> entries,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var entry in entries.WithCancellation(cancellationToken))
        {
            if (entry.PullRequest is not null && !entry.IsStaleRefreshOverlay)
            {
                yield return entry.PullRequest;
            }
        }
    }

    private static TimeSpan CacheDurationForScope(GitHubCacheScope scope) =>
        scope.IsShared ? PublicCacheDuration : CacheDuration;

    private async Task SetCacheEntryAsync<T>(
        string cacheKey,
        T value,
        TimeSpan cacheDuration,
        CancellationToken cancellationToken) =>
        await cache.SetAsync(cacheKey, value, cacheDuration, cancellationToken);

    private static string GetLastGoodCacheKey(string cacheKey) => $"last-good:{cacheKey}";

    private async Task SetLastGoodAsync<T>(
        string cacheKey,
        T value,
        CancellationToken cancellationToken,
        Action? onLocalEvicted = null) =>
        await cache.SetAsync(GetLastGoodCacheKey(cacheKey), value, LastGoodCacheDuration, cancellationToken, onLocalEvicted);

    private async Task SetPullRequestGraphQlLastGoodAsync(
        string cacheKey,
        IReadOnlyList<PullRequestSummary> value,
        CancellationToken cancellationToken) =>
        await SetLastGoodAsync(
            cacheKey,
            value,
            cancellationToken,
            () => RemovePullRequestGraphQlStateIfLastGoodMissing(cacheKey));

    private void RemovePullRequestGraphQlStateIfLastGoodMissing(string cacheKey)
    {
        if (!cache.TryGetLocalValue<IReadOnlyList<PullRequestSummary>>(GetLastGoodCacheKey(cacheKey), out _))
        {
            graphQlState.Remove(cacheKey);
        }
    }

    private async Task<GitHubCacheLookup<T>> GetLastGoodAsync<T>(
        string cacheKey,
        CancellationToken cancellationToken)
    {
        var lookup = await cache.GetAsync<T>(GetLastGoodCacheKey(cacheKey), cancellationToken);
        return lookup.Found && lookup.Value is not null ? lookup : default;
    }

    private async Task RemoveLastGoodAsync(string cacheKey, CancellationToken cancellationToken) =>
        await cache.RemoveAsync(GetLastGoodCacheKey(cacheKey), cancellationToken);

    private async Task<GitHubCacheLookup<T>> GetCachedFallbackAsync<T>(
        string? cacheKey,
        CancellationToken cancellationToken)
    {
        if (cacheKey is null)
        {
            return default;
        }

        var lookup = await cache.GetAsync<T>(cacheKey, cancellationToken);
        return lookup.Found && lookup.Value is not null ? lookup : default;
    }

    private async Task<GitHubCacheLookup<T>> GetTransientFallbackAsync<T>(
        string cacheKey,
        string? transientFallbackCacheKey,
        CancellationToken cancellationToken)
    {
        var cachedFallback = await GetCachedFallbackAsync<T>(transientFallbackCacheKey, cancellationToken);
        if (cachedFallback.Found)
        {
            return cachedFallback;
        }

        if (transientFallbackCacheKey is not null)
        {
            var fallbackLastGood = await GetLastGoodAsync<T>(transientFallbackCacheKey, cancellationToken);
            if (fallbackLastGood.Found)
            {
                return fallbackLastGood;
            }
        }

        return await GetLastGoodAsync<T>(cacheKey, cancellationToken);
    }

    private async Task<GitHubCacheLookup<T>> GetStaleSnapshotAsync<T>(
        string cacheKey,
        string? transientFallbackCacheKey,
        CancellationToken cancellationToken)
    {
        var cached = await cache.GetAsync<T>(cacheKey, cancellationToken);
        if (cached.Found && cached.Value is not null)
        {
            return cached;
        }

        var lastGood = await GetLastGoodAsync<T>(cacheKey, cancellationToken);
        if (lastGood.Found)
        {
            return lastGood;
        }

        return await GetTransientFallbackAsync<T>(
            cacheKey,
            transientFallbackCacheKey,
            cancellationToken);
    }

    private async Task<T> GetOrCreateWithLastGoodFallbackAsync<T>(
        string cacheKey,
        TimeSpan cacheDuration,
        bool refresh,
        bool cacheOnly,
        Func<Task<T>> factory,
        CancellationToken cancellationToken,
        Func<T, bool>? storeLastGood = null,
        Func<T, TimeSpan>? cacheDurationSelector = null,
        string? transientFallbackCacheKey = null,
        Action? lastGoodLocalEvicted = null)
        where T : class
    {
        var cachedLookup = await cache.GetAsync<T>(cacheKey, cancellationToken);
        var hasCachedValue = cachedLookup.Found && cachedLookup.Value is not null;
        if (!refresh && hasCachedValue)
        {
            return cachedLookup.Value!;
        }

        if (cacheOnly)
        {
            var lastGood = await GetLastGoodAsync<T>(cacheKey, cancellationToken);
            if (lastGood.Found)
            {
                return lastGood.Value!;
            }

            var cachedFallback = await GetCachedFallbackAsync<T>(transientFallbackCacheKey, cancellationToken);
            if (cachedFallback.Found)
            {
                return cachedFallback.Value!;
            }

            if (transientFallbackCacheKey is not null)
            {
                var fallbackLastGood = await GetLastGoodAsync<T>(transientFallbackCacheKey, cancellationToken);
                if (fallbackLastGood.Found)
                {
                    return fallbackLastGood.Value!;
                }
            }

            throw CreatePublicCacheUnavailableException();
        }

        try
        {
            var value = await factory();
            if (storeLastGood?.Invoke(value) ?? true)
            {
                await SetLastGoodAsync(cacheKey, value, cancellationToken, lastGoodLocalEvicted);
            }
            else
            {
                await RemoveLastGoodAsync(cacheKey, cancellationToken);
            }

            await SetCacheEntryAsync(cacheKey, value, cacheDurationSelector?.Invoke(value) ?? cacheDuration, cancellationToken);
            return value;
        }
        catch (Exception ex) when (IsTransientGitHubFailure(ex, cancellationToken))
        {
            if (hasCachedValue)
            {
                return cachedLookup.Value!;
            }

            var fallback = await GetTransientFallbackAsync<T>(cacheKey, transientFallbackCacheKey, cancellationToken);
            if (fallback.Found)
            {
                return fallback.Value!;
            }

            throw;
        }
    }

    private async Task<T?> GetOrRefreshCacheAsync<T>(
        string cacheKey,
        TimeSpan cacheDuration,
        bool refresh,
        bool cacheOnly,
        Func<Task<T?>> factory,
        CancellationToken cancellationToken,
        Func<T, TimeSpan>? cacheDurationSelector = null,
        Func<T?, bool>? storeValue = null,
        string? transientFallbackCacheKey = null)
    {
        var cachedLookup = await cache.GetAsync<T>(cacheKey, cancellationToken);
        if (!refresh && cachedLookup.Found)
        {
            return cachedLookup.Value;
        }

        if (cacheOnly)
        {
            var lastGood = await GetLastGoodAsync<T>(cacheKey, cancellationToken);
            if (lastGood.Found)
            {
                return lastGood.Value;
            }

            var cachedFallback = await GetCachedFallbackAsync<T>(transientFallbackCacheKey, cancellationToken);
            if (cachedFallback.Found)
            {
                return cachedFallback.Value;
            }

            if (transientFallbackCacheKey is not null)
            {
                var fallbackLastGood = await GetLastGoodAsync<T>(transientFallbackCacheKey, cancellationToken);
                if (fallbackLastGood.Found)
                {
                    return fallbackLastGood.Value;
                }
            }

            throw CreatePublicCacheUnavailableException();
        }

        try
        {
            var value = await factory();
            var shouldStoreValue = storeValue?.Invoke(value) ?? true;
            if (shouldStoreValue)
            {
                if (value is not null)
                {
                    await SetLastGoodAsync(cacheKey, value, cancellationToken);
                }
                else
                {
                    await RemoveLastGoodAsync(cacheKey, cancellationToken);
                }

                var resolvedCacheDuration = value is not null && cacheDurationSelector is not null
                    ? cacheDurationSelector(value)
                    : cacheDuration;
                await SetCacheEntryAsync(cacheKey, value, resolvedCacheDuration, cancellationToken);
            }
            else
            {
                await RemoveLastGoodAsync(cacheKey, cancellationToken);
            }

            return value;
        }
        catch (Exception ex) when (IsTransientGitHubFailure(ex, cancellationToken))
        {
            if (cachedLookup.Found)
            {
                return cachedLookup.Value;
            }

            var fallback = await GetTransientFallbackAsync<T>(cacheKey, transientFallbackCacheKey, cancellationToken);
            if (fallback.Found)
            {
                return fallback.Value;
            }

            throw;
        }
    }

    private readonly record struct RepositoryCacheScopeSelection(
        GitHubCacheScope Scope,
        GitHubCacheScope? SharedFallbackScope,
        bool Refresh,
        bool CacheOnly);

    // Fetches a per-repository resource through the same scope-selection + cache pipeline the dashboard's
    // PR data uses: the scope decides the token (user token when signed in, shared public-cache token for
    // anonymous viewers of public repos), responses are cached per scope (user entries stay in L1; public
    // entries write through to the shared github-cache Blob), and transient GitHub failures fall back to
    // last-good / the shared public snapshot. Used by the GitHub Actions endpoints so they behave exactly
    // like PR fetching.
    private async Task<T?> GetRepositoryResourceCachedAsync<T>(
        RepositoryName repositoryName,
        bool forceRefresh,
        string resourceName,
        string? keyPart,
        Func<GitHubCacheScope, Task<T?>> factory,
        CancellationToken cancellationToken)
    {
        var selection = await GetRepositoryCacheScopeSelectionAsync(repositoryName, forceRefresh, cancellationToken);
        var cacheKey = CreateRepositoryCacheKey(selection.Scope, repositoryName, resourceName, keyPart);
        var sharedFallbackKey = CreateSharedFallbackCacheKey(selection.SharedFallbackScope, repositoryName, resourceName, keyPart);
        return await GetOrRefreshCacheAsync(
            cacheKey,
            CacheDurationForScope(selection.Scope),
            selection.Refresh,
            selection.CacheOnly,
            () => factory(selection.Scope),
            cancellationToken,
            transientFallbackCacheKey: sharedFallbackKey);
    }

    private static GitHubApiException CreatePublicCacheUnavailableException() =>
        new(
            HttpStatusCode.ServiceUnavailable,
            "The shared public GitHub cache is warming or temporarily unavailable. Sign in with GitHub to load live data.");

    private static bool IsTransientGitHubFailure(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && exception switch
        {
            GitHubApiException ex => IsTransientGitHubStatusCode(ex.StatusCode)
                || ex.StatusCode == HttpStatusCode.Forbidden && IsRateLimitMessage(ex.Message),
            _ when IsTransientGitHubTransportFailure(exception) => true,
            _ => false
        };

    private static bool IsTransientGitHubTransportFailure(Exception exception) =>
        exception is HttpRequestException
           or TimeoutException
           or OperationCanceledException
        || IsBrokenCircuitException(exception);

    private static bool IsBrokenCircuitException(Exception exception) =>
        exception.GetType().FullName?.StartsWith("Polly.CircuitBreaker.BrokenCircuitException", StringComparison.Ordinal) is true;

    private static bool IsTransientGitHubStatusCode(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static bool IsRateLimitMessage(string message) =>
        message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("secondary rate", StringComparison.OrdinalIgnoreCase)
            || message.Contains("abuse detection", StringComparison.OrdinalIgnoreCase);

    public async Task<string?> GetCurrentUserLoginAsync(CancellationToken cancellationToken)
    {
        var authCacheKey = await tokenProvider.GetCacheKeyAsync(cancellationToken);
        var cacheKey = GitHubCachePolicy.CreateUserCacheKey(
            GitHubCachePolicy.CreateUserScope(authCacheKey),
            "current-user");
        var cachedLogin = await cache.GetAsync<string>(cacheKey, cancellationToken);
        if (cachedLogin.Found)
        {
            return cachedLogin.Value;
        }

        var user = await SendGitHubRequestAsync(
            "user",
            GitHubJsonSerializerContext.Default.GitHubActorDto,
            GitHubRequestAuthorization.Token,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(user.Login))
        {
            await SetCacheEntryAsync(cacheKey, user.Login, TimeSpan.FromMinutes(5), cancellationToken);
        }

        return user.Login;
    }

    // Resolves the authenticated user's numeric id and login from GitHub's /user endpoint.
    // Used only as a development fallback when there is no OAuth cookie principal to read the
    // id/login claims from; production reads them from the cookie without an extra call.
    public async Task<NotificationUser?> GetCurrentUserIdentityAsync(CancellationToken cancellationToken)
    {
        var user = await SendGitHubRequestAsync(
            "user",
            GitHubJsonSerializerContext.Default.GitHubActorDto,
            GitHubRequestAuthorization.Token,
            cancellationToken);

        return user is { Id: > 0, Login: { Length: > 0 } login }
            ? new NotificationUser(user.Id.Value, login)
            : null;
    }

    public async Task<IReadOnlyList<PullRequestSummary>> GetPullRequestsAsync(
        RepositoryName repositoryName,
        string state,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var scopeSelection = await GetRepositoryCacheScopeSelectionAsync(repositoryName, forceRefresh, cancellationToken);
        return await GetPullRequestsAsync(
            repositoryName,
            state,
            scopeSelection.Refresh,
            scopeSelection.Scope,
            scopeSelection.CacheOnly,
            CreateSharedFallbackCacheKey(scopeSelection.SharedFallbackScope, repositoryName, "pulls", state),
            cancellationToken);
    }

    // Returns the repository's default branch (e.g. "main"), used to classify push runs into the
    // rolling lane. One cheap GET /repos/{owner}/{repo} per cycle.
    public async Task<string> GetDefaultBranchAsync(
        RepositoryName repositoryName,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var branch = await GetRepositoryResourceCachedAsync<string>(
            repositoryName,
            forceRefresh,
            "default-branch",
            keyPart: null,
            async scope =>
            {
                var url = $"repos/{repositoryName.Owner}/{repositoryName.Name}";
                using var response = await SendGitHubRequestAsync(url, scope.RequestAuthorization, cancellationToken);
                var payload = await ReadGitHubJsonAsync(
                    response,
                    GitHubJsonSerializerContext.Default.GitHubRepositoryDto,
                    cancellationToken);
                return payload.DefaultBranch ?? "main";
            },
            cancellationToken);
        return branch ?? "main";
    }

    // Returns open issues for a repo (pull requests excluded), newest-updated first, as BotIssue
    // carriers with the raw author login. The caller filters to tracked bots. Fetched through the same
    // scope-selection + cache pipeline as PR data (see GetRepositoryResourceCachedAsync).
    public async Task<IReadOnlyList<BotIssue>> GetOpenIssuesAsync(
        RepositoryName repositoryName,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        const int maxPages = 3;
        const int perPage = 100;

        var issues = await GetRepositoryResourceCachedAsync<IReadOnlyList<BotIssue>>(
            repositoryName,
            forceRefresh,
            "open-issues",
            keyPart: null,
            async scope =>
            {
                var collected = new List<BotIssue>();
                for (var page = 1; page <= maxPages; page++)
                {
                    var url = $"repos/{repositoryName.Owner}/{repositoryName.Name}/issues?state=open&sort=updated&per_page={perPage}&page={page}";
                    using var response = await SendGitHubRequestAsync(url, scope.RequestAuthorization, cancellationToken);
                    var payload = await ReadGitHubJsonAsync(
                        response,
                        GitHubJsonSerializerContext.Default.GitHubIssueDtoArray,
                        cancellationToken);

                    if (payload.Length == 0)
                    {
                        break;
                    }

                    foreach (var dto in payload)
                    {
                        // The /issues endpoint returns PRs too; PRs carry a `pull_request` object.
                        if (dto.PullRequest is not null)
                        {
                            continue;
                        }

                        collected.Add(new BotIssue(
                            repositoryName.ToString(),
                            dto.Number,
                            dto.Title ?? "",
                            dto.User?.Login ?? "",
                            dto.HtmlUrl ?? "",
                            dto.Labels.Select(label => label.Name ?? "").Where(name => name.Length > 0).ToList()));
                    }

                    if (payload.Length < perPage)
                    {
                        break;
                    }
                }

                return collected;
            },
            cancellationToken);

        return issues ?? [];
    }

    // Lists a repo's workflow definitions (id + display name). Used by the weekly cycle to fetch runs
    // per workflow, which avoids the repo-wide /actions/runs ~1000-result ceiling truncating the
    // 14-day window on busy repos.
    public async Task<IReadOnlyList<WorkflowDefinition>> GetWorkflowDefinitionsAsync(
        RepositoryName repositoryName,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        const int maxPages = 5;
        const int perPage = 100;

        var definitions = await GetRepositoryResourceCachedAsync<IReadOnlyList<WorkflowDefinition>>(
            repositoryName,
            forceRefresh,
            "workflow-definitions",
            keyPart: null,
            async scope =>
            {
                var collected = new List<WorkflowDefinition>();
                for (var page = 1; page <= maxPages; page++)
                {
                    var url = $"repos/{repositoryName.Owner}/{repositoryName.Name}/actions/workflows?per_page={perPage}&page={page}";
                    using var response = await SendGitHubRequestAsync(url, scope.RequestAuthorization, cancellationToken);
                    var payload = await ReadGitHubJsonAsync(
                        response,
                        GitHubJsonSerializerContext.Default.GitHubWorkflowDefinitionsResponseDto,
                        cancellationToken);

                    if (payload.Workflows.Length == 0)
                    {
                        break;
                    }

                    foreach (var dto in payload.Workflows)
                    {
                        // Skip disabled workflows; only "active" ones produce meaningful health signal.
                        if (dto.State is null or "active")
                        {
                            collected.Add(new WorkflowDefinition(dto.Id, dto.Name ?? "(unnamed)"));
                        }
                    }

                    if (payload.Workflows.Length < perPage)
                    {
                        break;
                    }
                }

                return collected;
            },
            cancellationToken);

        return definitions ?? [];
    }

    // Fetches recent GitHub Actions runs for a repo (or a single workflow when workflowId is set),
    // newest-first, stopping once runs predate `since` or a page cap is hit. Fetched through the same
    // scope-selection + cache pipeline as PR data (see GetRepositoryResourceCachedAsync). The server-side
    // `created>=since` filter keeps the page budget spent on in-window runs only, so busy repos don't
    // exhaust it on older runs. Cached per workflow ("all" for the repo-wide call): the window size is
    // fixed per caller, so keying by workflow id alone keeps the cache key stable across cycles.
    public async Task<IReadOnlyList<WorkflowRun>> GetWorkflowRunsAsync(
        RepositoryName repositoryName,
        DateTimeOffset since,
        CancellationToken cancellationToken,
        long? workflowId = null,
        bool forceRefresh = false)
    {
        const int maxPages = 10;
        const int perPage = 100;
        // GitHub's `created` filter takes a date-range expression; ">=<iso>" bounds it to the window.
        var createdFilter = Uri.EscapeDataString($">={since.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}");
        var runsPath = workflowId is long id
            ? $"repos/{repositoryName.Owner}/{repositoryName.Name}/actions/workflows/{id}/runs"
            : $"repos/{repositoryName.Owner}/{repositoryName.Name}/actions/runs";

        var runs = await GetRepositoryResourceCachedAsync<IReadOnlyList<WorkflowRun>>(
            repositoryName,
            forceRefresh,
            "workflow-runs",
            keyPart: workflowId?.ToString() ?? "all",
            async scope =>
            {
                var collected = new List<WorkflowRun>();
                for (var page = 1; page <= maxPages; page++)
                {
                    var url = $"{runsPath}?per_page={perPage}&page={page}&created={createdFilter}";
                    using var response = await SendGitHubRequestAsync(url, scope.RequestAuthorization, cancellationToken);
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

                        collected.Add(new WorkflowRun(
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

                return collected;
            },
            cancellationToken);

        return runs ?? [];
    }

    public async Task<IReadOnlyList<PullRequestSummary>> GetPullRequestsGraphQlAsync(
        RepositoryName repositoryName,
        string state,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var scopeSelection = await GetRepositoryCacheScopeSelectionAsync(repositoryName, forceRefresh, cancellationToken);
        return await GetPullRequestsGraphQlAsync(
            repositoryName,
            state,
            scopeSelection.Refresh,
            scopeSelection.Scope,
            scopeSelection.CacheOnly,
            CreateSharedFallbackCacheKey(scopeSelection.SharedFallbackScope, repositoryName, "pulls-graphql", state),
            cancellationToken);
    }

    public async Task<PullRequestListResponse> GetPullRequestsGraphQlSnapshotAsync(
        RepositoryName repositoryName,
        string state,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var scopeSelection = await GetRepositoryCacheScopeSelectionAsync(repositoryName, forceRefresh, cancellationToken);
        return await GetPullRequestsGraphQlSnapshotAsync(
            repositoryName,
            state,
            scopeSelection.Refresh,
            scopeSelection.Scope,
            scopeSelection.CacheOnly,
            CreateSharedFallbackCacheKey(scopeSelection.SharedFallbackScope, repositoryName, "pulls-graphql", state),
            cancellationToken);
    }

    public async Task<bool> TryPrewarmPublicPullRequestsAsync(
        RepositoryName repositoryName,
        string state,
        CancellationToken cancellationToken)
    {
        if (publicCacheIdentity.GetToken() is null)
        {
            return false;
        }

        var eligibility = await cacheScopeResolver.GetPublicCacheRepositoryEligibilityAsync(
            repositoryName,
            forceRefresh: true,
            cancellationToken);
        if (eligibility != GitHubPublicCacheRepositoryEligibility.Public)
        {
            if (eligibility is GitHubPublicCacheRepositoryEligibility.NotAllowlisted
                or GitHubPublicCacheRepositoryEligibility.NotPublic)
            {
                await publicCacheStore.RemoveRepositoryAsync(repositoryName, cancellationToken);
            }

            return false;
        }

        var scope = GitHubCachePolicy.CreatePublicRepositoryScope();
        var restCacheKey = CreateRepositoryCacheKey(
            scope,
            repositoryName,
            "pulls",
            state);
        await GetPullRequestsAsync(
            repositoryName,
            state,
            forceRefresh: true,
            scope,
            cacheOnly: false,
            transientFallbackCacheKey: null,
            cancellationToken);
        await publicCacheStore.TrackAsync(repositoryName, restCacheKey, cancellationToken);

        var graphQlCacheKey = CreateRepositoryCacheKey(
            scope,
            repositoryName,
            "pulls-graphql",
            state);
        await GetPullRequestsGraphQlAsync(
            repositoryName,
            state,
            forceRefresh: true,
            scope,
            cacheOnly: false,
            transientFallbackCacheKey: null,
            cancellationToken);
        await publicCacheStore.TrackAsync(repositoryName, graphQlCacheKey, cancellationToken);
        return true;
    }

    private async Task<IReadOnlyList<PullRequestSummary>> GetPullRequestsAsync(
        RepositoryName repositoryName,
        string state,
        bool forceRefresh,
        GitHubCacheScope scope,
        bool cacheOnly,
        string? transientFallbackCacheKey,
        CancellationToken cancellationToken)
    {
        var cacheKey = CreateRepositoryCacheKey(
            scope,
            repositoryName,
            "pulls",
            state);
        var refreshCache = forceRefresh;
        return await GetOrCreateWithLastGoodFallbackAsync(
            cacheKey,
            CacheDurationForScope(scope),
            refreshCache,
            cacheOnly,
            async () =>
        {
            var sort = state.Equals("open", StringComparison.OrdinalIgnoreCase) ? "created" : "updated";
            var direction = state.Equals("open", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";
            var url = $"repos/{repositoryName.Owner}/{repositoryName.Name}/pulls?state={Uri.EscapeDataString(state)}&sort={sort}&direction={direction}&per_page={PullRequestPageSize}";
            var pullRequestDtos = await SendPagedGitHubRequestAsync(
                url,
                GitHubJsonSerializerContext.Default.GitHubPullRequestDtoArray,
                scope.RequestAuthorization,
                cancellationToken);
            var activePullRequestDtos = pullRequestDtos
                .Where(pullRequest => !pullRequest.Draft)
                .ToArray();
            if (scope.IsShared)
            {
                return CreatePullRequestBaselineSummaries(activePullRequestDtos);
            }

            var mergeableStateEnrichmentNumbers = activePullRequestDtos
                .Select(pullRequest => pullRequest.Number)
                .ToHashSet();

            return await CreatePullRequestSummariesAsync(
                repositoryName,
                activePullRequestDtos,
                mergeableStateEnrichmentNumbers,
                refreshCache,
                scope,
                cancellationToken);
        },
            cancellationToken,
            transientFallbackCacheKey: transientFallbackCacheKey);
    }

    private async Task<IReadOnlyList<PullRequestSummary>> GetPullRequestsGraphQlAsync(
        RepositoryName repositoryName,
        string state,
        bool forceRefresh,
        GitHubCacheScope scope,
        bool cacheOnly,
        string? transientFallbackCacheKey,
        CancellationToken cancellationToken)
    {
        var cacheKey = CreateRepositoryCacheKey(
            scope,
            repositoryName,
            "pulls-graphql",
            state);
        var refreshCache = forceRefresh;
        return await GetOrCreateWithLastGoodFallbackAsync(
            cacheKey,
            CacheDurationForScope(scope),
            refreshCache,
            cacheOnly,
            async () =>
            {
                var result = await FetchPullRequestGraphQlSummariesAsync(
                    repositoryName,
                    state,
                    scope,
                    cancellationToken);
                graphQlState.SetListFetchedAt(cacheKey, DateTimeOffset.UtcNow, PullRequestGraphQlStateDuration);
                return result;
            },
            cancellationToken,
            transientFallbackCacheKey: transientFallbackCacheKey,
            lastGoodLocalEvicted: () => graphQlState.Remove(cacheKey));
    }

    private async Task<PullRequestListResponse> GetPullRequestsGraphQlSnapshotAsync(
        RepositoryName repositoryName,
        string state,
        bool forceRefresh,
        GitHubCacheScope scope,
        bool cacheOnly,
        string? transientFallbackCacheKey,
        CancellationToken cancellationToken)
    {
        var cacheKey = CreateRepositoryCacheKey(
            scope,
            repositoryName,
            "pulls-graphql",
            state);
        var refreshCache = forceRefresh;
        if (refreshCache && !cacheOnly)
        {
            var livePullRequests = await FetchPullRequestGraphQlSummariesAsync(
                repositoryName,
                state,
                scope,
                cancellationToken);
            graphQlState.SetListFetchedAt(cacheKey, DateTimeOffset.UtcNow, PullRequestGraphQlStateDuration);
            await SetPullRequestGraphQlLastGoodAsync(cacheKey, livePullRequests, cancellationToken);
            await SetCacheEntryAsync(cacheKey, livePullRequests, CacheDurationForScope(scope), cancellationToken);
            graphQlState.RemoveRefreshError(cacheKey);
            return CreatePullRequestListResponse(
                repositoryName,
                livePullRequests,
                "live",
                stale: false,
                refreshInProgress: false,
                refreshQueued: false,
                error: null,
                cacheKey: cacheKey);
        }

        if (!refreshCache)
        {
            var cachedLookup = await cache.GetAsync<IReadOnlyList<PullRequestSummary>>(cacheKey, cancellationToken);
            if (cachedLookup is { Found: true, Value: not null })
            {
                return CreatePullRequestListResponse(
                    repositoryName,
                    cachedLookup.Value,
                    "fresh-cache",
                    stale: false,
                    refreshInProgress: IsPullRequestGraphQlRefreshInProgress(cacheKey),
                    refreshQueued: false,
                    error: GetPullRequestGraphQlRefreshError(cacheKey),
                    cacheKey: cacheKey);
            }

            var lastGood = await GetLastGoodAsync<IReadOnlyList<PullRequestSummary>>(cacheKey, cancellationToken);
            if (lastGood is { Found: true, Value: not null }
                && (cacheOnly || IsPullRequestListWithinMaxStaleAge(lastGood.Value, cacheKey)))
            {
                var refreshQueued = await TryQueuePullRequestGraphQlRefreshAsync(
                    cacheKey,
                    repositoryName,
                    state,
                    scope,
                    cacheOnly,
                    cancellationToken);

                return CreatePullRequestListResponse(
                    repositoryName,
                    lastGood.Value,
                    "last-good",
                    stale: true,
                    refreshInProgress: !cacheOnly && IsPullRequestGraphQlRefreshInProgress(cacheKey),
                    refreshQueued,
                    error: GetPullRequestGraphQlRefreshError(cacheKey),
                    cacheKey: cacheKey);
            }

            var cachedFallback = await GetCachedFallbackAsync<IReadOnlyList<PullRequestSummary>>(
                transientFallbackCacheKey,
                cancellationToken);
            if (cachedFallback is { Found: true, Value: not null })
            {
                var refreshQueued = await TryQueuePullRequestGraphQlRefreshAsync(
                    cacheKey,
                    repositoryName,
                    state,
                    scope,
                    cacheOnly,
                    cancellationToken);

                return CreatePullRequestListResponse(
                    repositoryName,
                    cachedFallback.Value,
                    "shared-cache",
                    stale: false,
                    refreshInProgress: !cacheOnly && IsPullRequestGraphQlRefreshInProgress(cacheKey),
                    refreshQueued,
                    error: GetPullRequestGraphQlRefreshError(cacheKey),
                    cacheKey: cacheKey);
            }

            if (transientFallbackCacheKey is not null)
            {
                var fallbackLastGood = await GetLastGoodAsync<IReadOnlyList<PullRequestSummary>>(
                    transientFallbackCacheKey,
                    cancellationToken);
                if (fallbackLastGood is { Found: true, Value: not null }
                    && (cacheOnly || IsPullRequestListWithinMaxStaleAge(fallbackLastGood.Value, cacheKey)))
                {
                    var refreshQueued = await TryQueuePullRequestGraphQlRefreshAsync(
                        cacheKey,
                        repositoryName,
                        state,
                        scope,
                        cacheOnly,
                        cancellationToken);

                    return CreatePullRequestListResponse(
                        repositoryName,
                        fallbackLastGood.Value,
                        "last-good",
                        stale: true,
                        refreshInProgress: !cacheOnly && IsPullRequestGraphQlRefreshInProgress(cacheKey),
                        refreshQueued,
                        error: GetPullRequestGraphQlRefreshError(cacheKey),
                        cacheKey: cacheKey);
                }
            }

            var publicRestFallback = await GetPublicRestPullRequestSnapshotFallbackAsync(
                scope,
                repositoryName,
                state,
                cancellationToken);
            if (publicRestFallback.Found)
            {
                return CreatePullRequestListResponse(
                    repositoryName,
                    publicRestFallback.PullRequests!,
                    publicRestFallback.Source,
                    publicRestFallback.Stale,
                    refreshInProgress: false,
                    refreshQueued: false,
                    error: GetPullRequestGraphQlRefreshError(cacheKey),
                    cacheKey: cacheKey);
            }

            if (cacheOnly)
            {
                throw CreatePublicCacheUnavailableException();
            }
        }

        try
        {
            var pullRequests = await FetchPullRequestGraphQlSummariesAsync(
                repositoryName,
                state,
                scope,
                cancellationToken);
            graphQlState.SetListFetchedAt(cacheKey, DateTimeOffset.UtcNow, PullRequestGraphQlStateDuration);
            await SetPullRequestGraphQlLastGoodAsync(cacheKey, pullRequests, cancellationToken);
            await SetCacheEntryAsync(cacheKey, pullRequests, CacheDurationForScope(scope), cancellationToken);
            graphQlState.RemoveRefreshError(cacheKey);
            graphQlState.RemoveRefreshCooldownUntil(cacheKey);
            return CreatePullRequestListResponse(
                repositoryName,
                pullRequests,
                "live",
                stale: false,
                refreshInProgress: false,
                refreshQueued: false,
                error: null,
                cacheKey: cacheKey);
        }
        catch (Exception ex) when (IsTransientGitHubFailure(ex, cancellationToken))
        {
            var fallback = await GetPullRequestGraphQlTransientFallbackAsync(
                cacheKey,
                transientFallbackCacheKey,
                cancellationToken);
            if (!fallback.Found)
            {
                throw;
            }

            graphQlState.SetRefreshError(cacheKey, ex.Message, PullRequestGraphQlStateDuration);
            return CreatePullRequestListResponse(
                repositoryName,
                fallback.PullRequests!,
                fallback.Source,
                fallback.Stale,
                refreshInProgress: false,
                refreshQueued: false,
                error: ex.Message,
                cacheKey: fallback.CacheKey);
        }
    }

    private async Task<PullRequestGraphQlSnapshotFallback> GetPublicRestPullRequestSnapshotFallbackAsync(
        GitHubCacheScope scope,
        RepositoryName repositoryName,
        string state,
        CancellationToken cancellationToken)
    {
        if (!scope.IsShared)
        {
            return default;
        }

        var cacheKey = CreateRepositoryCacheKey(
            scope,
            repositoryName,
            "pulls",
            state);
        var cached = await GetCachedFallbackAsync<IReadOnlyList<PullRequestSummary>>(
            cacheKey,
            cancellationToken);
        if (cached is { Found: true, Value: not null })
        {
            return new PullRequestGraphQlSnapshotFallback(cached.Value, "shared-cache", false, cacheKey);
        }

        var lastGood = await GetLastGoodAsync<IReadOnlyList<PullRequestSummary>>(
            cacheKey,
            cancellationToken);
        return lastGood is { Found: true, Value: not null }
            ? new PullRequestGraphQlSnapshotFallback(lastGood.Value, "last-good", true, cacheKey)
            : default;
    }

    private async Task<PullRequestGraphQlSnapshotFallback> GetPullRequestGraphQlTransientFallbackAsync(
        string cacheKey,
        string? transientFallbackCacheKey,
        CancellationToken cancellationToken)
    {
        var cachedFallback = await GetCachedFallbackAsync<IReadOnlyList<PullRequestSummary>>(
            transientFallbackCacheKey,
            cancellationToken);
        if (cachedFallback is { Found: true, Value: not null } && transientFallbackCacheKey is not null)
        {
            return new PullRequestGraphQlSnapshotFallback(cachedFallback.Value, "shared-cache", false, transientFallbackCacheKey);
        }

        if (transientFallbackCacheKey is not null)
        {
            var fallbackLastGood = await GetLastGoodAsync<IReadOnlyList<PullRequestSummary>>(
                transientFallbackCacheKey,
                cancellationToken);
            if (fallbackLastGood is { Found: true, Value: not null })
            {
                return new PullRequestGraphQlSnapshotFallback(fallbackLastGood.Value, "last-good", true, transientFallbackCacheKey);
            }
        }

        var lastGood = await GetLastGoodAsync<IReadOnlyList<PullRequestSummary>>(
            cacheKey,
            cancellationToken);
        return lastGood is { Found: true, Value: not null }
            ? new PullRequestGraphQlSnapshotFallback(lastGood.Value, "last-good", true, cacheKey)
            : default;
    }

    private readonly record struct PullRequestGraphQlSnapshotFallback(
        IReadOnlyList<PullRequestSummary>? PullRequests,
        string Source,
        bool Stale,
        string CacheKey)
    {
        public bool Found => PullRequests is not null;
    }

    private async Task<IReadOnlyList<PullRequestSummary>> FetchPullRequestGraphQlSummariesAsync(
        RepositoryName repositoryName,
        string state,
        GitHubCacheScope scope,
        CancellationToken cancellationToken)
    {
        var token = await GetRequiredGraphQlTokenAsync(scope.RequestAuthorization, cancellationToken);
        return await FetchPullRequestGraphQlSummariesAsync(
            repositoryName,
            state,
            token,
            cancellationToken);
    }

    private async Task<IReadOnlyList<PullRequestSummary>> FetchPullRequestGraphQlSummariesAsync(
        RepositoryName repositoryName,
        string state,
        string token,
        CancellationToken cancellationToken)
    {
        var pullRequests = await FetchPullRequestsGraphQlAsync(
            repositoryName,
            state,
            token,
            cancellationToken);

        var summaries = pullRequests.Select(pullRequest =>
            CreatePullRequestSummaryFromGraphQlAsync(
                repositoryName,
                pullRequest,
                token,
                cancellationToken));

        var fetchedAt = DateTimeOffset.UtcNow;
        return (await Task.WhenAll(summaries))
            .Select(pullRequest => pullRequest with { FetchedAt = fetchedAt })
            .ToArray();
    }

    private async Task<string> GetRequiredGraphQlTokenAsync(
        GitHubRequestAuthorization requestAuthorization,
        CancellationToken cancellationToken)
    {
        var token = await GetGraphQlTokenAsync(requestAuthorization, cancellationToken);
        if (token is not null)
        {
            return token;
        }

        throw new GitHubApiException(
            HttpStatusCode.Unauthorized,
            requestAuthorization == GitHubRequestAuthorization.PublicCacheToken
                ? "GitHub authentication is required. Configure the public cache identity token."
                : "GitHub authentication is required. Sign in with GitHub.");
    }

    private bool QueuePullRequestGraphQlRefresh(
        string cacheKey,
        RepositoryName repositoryName,
        string state,
        GitHubCacheScope scope,
        string token)
    {
        // Reserve the slot synchronously before starting any work so concurrent callers
        // for the same key cannot each launch a background fetch (true single-flight).
        var reservation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!graphQlState.RefreshTasks.TryAdd(cacheKey, reservation.Task))
        {
            return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var pullRequests = await FetchPullRequestGraphQlSummariesAsync(
                    repositoryName,
                    state,
                    token,
                    CancellationToken.None);
                graphQlState.SetListFetchedAt(cacheKey, DateTimeOffset.UtcNow, PullRequestGraphQlStateDuration);
                await SetPullRequestGraphQlLastGoodAsync(cacheKey, pullRequests, CancellationToken.None);
                await SetCacheEntryAsync(
                    cacheKey,
                    pullRequests,
                    CacheDurationForScope(scope),
                    CancellationToken.None);
                graphQlState.RemoveRefreshError(cacheKey);
                graphQlState.RemoveRefreshCooldownUntil(cacheKey);
            }
            catch (Exception ex)
            {
                // Back off before another refresh can be queued for this key so a polling client
                // cannot drive a tight retry loop against a persistently failing upstream.
                graphQlState.SetRefreshCooldownUntil(cacheKey, DateTimeOffset.UtcNow + PullRequestGraphQlRefreshFailureCooldown);
                graphQlState.SetRefreshError(cacheKey, ex.Message, PullRequestGraphQlStateDuration);
            }
            finally
            {
                // Remove only our own reservation so we can never evict a newer in-flight refresh.
                graphQlState.RefreshTasks.TryRemove(new KeyValuePair<string, Task>(cacheKey, reservation.Task));
                reservation.SetResult();
            }
        });

        return true;
    }

    private async Task<bool> TryQueuePullRequestGraphQlRefreshAsync(
        string cacheKey,
        RepositoryName repositoryName,
        string state,
        GitHubCacheScope scope,
        bool cacheOnly,
        CancellationToken cancellationToken)
    {
        if (cacheOnly)
        {
            return false;
        }

        if (graphQlState.TryGetRefreshCooldownUntil(cacheKey, out var cooldownUntil)
            && cooldownUntil > DateTimeOffset.UtcNow)
        {
            // A recent background refresh for this key failed; skip queueing until the cooldown
            // elapses so repeated client polls don't spin up a refresh on every request.
            return false;
        }

        var token = await GetGraphQlTokenAsync(scope.RequestAuthorization, cancellationToken);
        if (token is null)
        {
            graphQlState.SetRefreshError(
                cacheKey,
                scope.RequestAuthorization == GitHubRequestAuthorization.PublicCacheToken
                    ? "GitHub authentication is required. Configure the public cache identity token."
                    : "GitHub authentication is required. Sign in with GitHub.",
                PullRequestGraphQlStateDuration);
            return false;
        }

        return QueuePullRequestGraphQlRefresh(cacheKey, repositoryName, state, scope, token);
    }

    private bool IsPullRequestGraphQlRefreshInProgress(string cacheKey) =>
        graphQlState.RefreshTasks.ContainsKey(cacheKey);

    private string? GetPullRequestGraphQlRefreshError(string cacheKey) =>
        graphQlState.TryGetRefreshError(cacheKey, out var error) ? error : null;

    private PullRequestListResponse CreatePullRequestListResponse(
        RepositoryName repositoryName,
        IReadOnlyList<PullRequestSummary> pullRequests,
        string source,
        bool stale,
        bool refreshInProgress,
        bool refreshQueued,
        string? error,
        string cacheKey) =>
        new(
            repositoryName.ToString(),
            pullRequests,
            new PullRequestListSnapshot(
                source,
                GetPullRequestListFetchedAt(pullRequests, cacheKey),
                stale,
                refreshInProgress,
                refreshQueued,
                error));

    private DateTimeOffset GetPullRequestListFetchedAt(IReadOnlyList<PullRequestSummary> pullRequests, string cacheKey)
    {
        // An empty list carries no per-PR FetchedAt, so fall back to the time this cache key was last
        // fetched rather than "now" -- otherwise an empty cached result would look fresh on every read
        // and defeat the max-stale-age gate. UtcNow is only used when nothing has been tracked yet
        // (e.g. immediately after a process restart, before the first fetch completes).
        var emptyListFetchedAt = graphQlState.TryGetListFetchedAt(cacheKey, out var tracked)
            ? tracked
            : DateTimeOffset.UtcNow;
        var fetchedAt = pullRequests
            .Select(pullRequest => pullRequest.FetchedAt)
            .Where(timestamp => timestamp != default)
            .DefaultIfEmpty(emptyListFetchedAt)
            .Max();
        return fetchedAt;
    }

    private bool IsPullRequestListWithinMaxStaleAge(IReadOnlyList<PullRequestSummary> pullRequests, string cacheKey) =>
        DateTimeOffset.UtcNow - GetPullRequestListFetchedAt(pullRequests, cacheKey) <= MaxStaleDisplayAge;

    private async Task<IReadOnlyList<GitHubPullRequestGraphQlNodeDto>> FetchPullRequestsGraphQlAsync(
        RepositoryName repositoryName,
        string state,
        string token,
        CancellationToken cancellationToken)
    {
        var pullRequests = new List<GitHubPullRequestGraphQlNodeDto>();
        var states = ToGraphQlPullRequestStates(state);
        var (orderField, orderDirection) = ToGraphQlPullRequestOrder(state);
        string? afterCursor = null;

        while (true)
        {
            var connection = await SendPullRequestsGraphQlPageAsync(
                repositoryName,
                states,
                orderField,
                orderDirection,
                afterCursor,
                token,
                cancellationToken);
            if (connection?.Nodes is { Count: > 0 } nodes)
            {
                pullRequests.AddRange(nodes.OfType<GitHubPullRequestGraphQlNodeDto>());
            }

            var pageInfo = connection?.PageInfo;
            if (pageInfo?.HasNextPage == true
                && !string.IsNullOrEmpty(pageInfo.EndCursor)
                && pageInfo.EndCursor != afterCursor)
            {
                afterCursor = pageInfo.EndCursor;
            }
            else
            {
                break;
            }
        }

        return pullRequests;
    }

    private async Task<PullRequestSummary> CreatePullRequestSummaryFromGraphQlAsync(
        RepositoryName repositoryName,
        GitHubPullRequestGraphQlNodeDto pullRequest,
        string graphQlToken,
        CancellationToken cancellationToken)
    {
        var reviewStatus = await CreateReviewStatusFromGraphQlAsync(
            repositoryName,
            pullRequest,
            graphQlToken,
            cancellationToken);
        var state = MapGraphQlPullRequestState(pullRequest.State);
        var headSha = string.IsNullOrWhiteSpace(pullRequest.HeadRefOid) ? null : pullRequest.HeadRefOid;
        var requestedReviewers = GetRequestedReviewerLogins(pullRequest).ToArray();
        var requestedReviewerIds = GetRequestedReviewerIds(pullRequest).ToArray();
        var lastCommitAt = reviewStatus.LastReviewedAt is not null
            && (reviewStatus.State == "reviewed" || reviewStatus.State == "changes_requested")
                ? GetLastCommitAt(pullRequest)
                : null;

        return new PullRequestSummary(
            pullRequest.Number,
            pullRequest.Title ?? "",
            state,
            pullRequest.IsDraft,
            PullRequestSummary.ResolveAuthor(
                pullRequest.Author?.Login,
                pullRequest.Assignees?.Nodes?
                    .Select(assignee => assignee?.Login)
                    .Where(login => !string.IsNullOrWhiteSpace(login))
                    .Select(login => login!)
                ?? []),
            pullRequest.Url ?? "",
            pullRequest.CreatedAt,
            pullRequest.UpdatedAt,
            pullRequest.Labels?.Nodes?
                .Select(label => label?.Name)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Select(label => label!)
                .ToArray()
            ?? [],
            requestedReviewers,
            pullRequest.Milestone?.Title,
            GetLinkedIssueSummaries(pullRequest).ToArray(),
            pullRequest.Commits?.TotalCount ?? 0,
            pullRequest.Additions,
            pullRequest.Deletions,
            pullRequest.ChangedFiles,
            lastCommitAt,
            headSha,
            pullRequest.BaseRefName,
            MapGraphQlMergeableState(pullRequest.Mergeable),
            reviewStatus,
            state.Equals("open", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(headSha)
                ? CreateChecksStatusFromGraphQl(GetHeadCommitStatusCheckRollup(pullRequest))
                : ChecksStatus.None)
        {
            RequestedReviewerIds = requestedReviewerIds,
            OwnerUserId = PullRequestSummary.ResolveOwnerUserId(
                pullRequest.Author?.Login,
                pullRequest.Author?.DatabaseId,
                pullRequest.Assignees?.Nodes?
                    .Where(assignee => assignee is not null)
                    .Select(assignee => (assignee!.Login, assignee.DatabaseId))
                    ?? []),
        };
    }

    private static IReadOnlyList<string>? ToGraphQlPullRequestStates(string state) =>
        state.Trim().ToLowerInvariant() switch
        {
            "open" => ["OPEN"],
            "closed" => ["CLOSED", "MERGED"],
            "all" => null,
            _ => [state.ToUpperInvariant()],
        };

    private static (string Field, string Direction) ToGraphQlPullRequestOrder(string state) =>
        state.Equals("open", StringComparison.OrdinalIgnoreCase)
            ? ("CREATED_AT", "ASC")
            : ("UPDATED_AT", "DESC");

    private static string MapGraphQlPullRequestState(string? state) =>
        state?.Equals("OPEN", StringComparison.OrdinalIgnoreCase) is true
            ? "open"
            : "closed";

    private static string? MapGraphQlMergeableState(string? mergeable) =>
        mergeable?.ToUpperInvariant() switch
        {
            "MERGEABLE" => "clean",
            "CONFLICTING" => "dirty",
            "UNKNOWN" => "unknown",
            null => null,
            _ => mergeable.ToLowerInvariant(),
        };

    private static DateTimeOffset? GetLastCommitAt(GitHubPullRequestGraphQlNodeDto pullRequest) =>
        pullRequest.Commits?.Nodes?
            .Select(node => node?.Commit?.CommittedDate)
            .LastOrDefault(date => date is not null);

    private static IEnumerable<string> GetRequestedReviewerLogins(GitHubPullRequestGraphQlNodeDto pullRequest) =>
        pullRequest.ReviewRequests?.Nodes?
            .Select(node => node?.RequestedReviewer)
            .Where(reviewer => reviewer?.TypeName == "User" && !string.IsNullOrWhiteSpace(reviewer.Login))
            .Select(reviewer => reviewer!.Login!)
        ?? [];

    private static IEnumerable<long> GetRequestedReviewerIds(GitHubPullRequestGraphQlNodeDto pullRequest) =>
        pullRequest.ReviewRequests?.Nodes?
            .Select(node => node?.RequestedReviewer)
            .Where(reviewer => reviewer?.TypeName == "User" && reviewer.DatabaseId is > 0)
            .Select(reviewer => reviewer!.DatabaseId!.Value)
            .Distinct()
        ?? [];

    private static IEnumerable<LinkedIssueSummary> GetLinkedIssueSummaries(GitHubPullRequestGraphQlNodeDto pullRequest)
    {
        if (pullRequest.ClosingIssuesReferences?.Nodes is not { Count: > 0 } issues)
        {
            yield break;
        }

        foreach (var issue in issues)
        {
            if (issue is null)
            {
                continue;
            }

            var repository = issue.Repository?.NameWithOwner;
            if (string.IsNullOrWhiteSpace(repository))
            {
                continue;
            }

            yield return new LinkedIssueSummary(
                repository,
                issue.Number,
                issue.Title ?? "",
                issue.Milestone?.Title,
                issue.Labels?.Nodes?
                    .Select(label => label?.Name)
                    .Where(label => !string.IsNullOrWhiteSpace(label))
                    .Select(label => label!)
                    .ToArray()
                ?? [],
                issue.Url ?? "");
        }
    }

    private async Task<ReviewStatus> CreateReviewStatusFromGraphQlAsync(
        RepositoryName repositoryName,
        GitHubPullRequestGraphQlNodeDto pullRequest,
        string graphQlToken,
        CancellationToken cancellationToken)
    {
        var reviewEvents = pullRequest.Reviews?.Nodes?
            .Where(review => review is not null)
            .Select(review => ReviewEvent.FromGraphQl(review!))
            .OfType<ReviewEvent>()
            .ToArray()
        ?? [];
        var humanReviews = reviewEvents
            .Where(review => !IsBotActor(review.Actor))
            .OrderBy(review => review.SubmittedAt)
            .ToArray();

        var latestByReviewer = humanReviews
            .GroupBy(review => review.Actor, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.MaxBy(review => review.SubmittedAt)!)
            .ToArray();

        var state =
            latestByReviewer.Any(review => review.State == "CHANGES_REQUESTED") ? "changes_requested" :
            latestByReviewer.Any(review => review.State == "APPROVED") ? "approved" :
            latestByReviewer.Any(review => review.State == "COMMENTED") ? "reviewed" :
            "waiting";
        var requiresConversationResolution = RequiresConversationResolution(repositoryName);
        var copilotReviewed = reviewEvents.Any(review => IsCopilotReviewer(review.Actor));
        var shouldCountUnresolvedThreads =
            state == "approved"
            || state == "reviewed"
            || (state == "waiting" && copilotReviewed);
        var unresolvedThreadCount = shouldCountUnresolvedThreads
            ? await CountUnresolvedReviewThreadsFromGraphQlAsync(
                repositoryName,
                pullRequest.Number,
                pullRequest.ReviewThreads,
                graphQlToken,
                cancellationToken)
            : 0;

        return new ReviewStatus(
            State: state,
            LatestState: humanReviews.LastOrDefault()?.State,
            ReviewerCount: humanReviews.Select(review => review.Actor).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            ApprovalCount: humanReviews.Count(review => review.State == "APPROVED"),
            ChangesRequestedCount: humanReviews.Count(review => review.State == "CHANGES_REQUESTED"),
            CommentedReviewCount: humanReviews.Count(review => review.State == "COMMENTED"),
            LastApprovedAt: humanReviews.LastOrDefault(review => review.State == "APPROVED")?.SubmittedAt,
            LastReviewedAt: humanReviews.LastOrDefault()?.SubmittedAt,
            UnresolvedThreadCount: unresolvedThreadCount,
            RequiresConversationResolution: requiresConversationResolution)
        {
            ApprovedReviewerIds = ApprovedReviewerIdsOf(latestByReviewer)
        };
    }

    // Numeric ids of the reviewers whose latest review is an approval, for ready-to-merge
    // notifications. Reviewers without a known id (older REST data) are skipped.
    private static IReadOnlyList<long> ApprovedReviewerIdsOf(IEnumerable<ReviewEvent> latestByReviewer) =>
        latestByReviewer
            .Where(review => review.State == "APPROVED" && review.ActorId is > 0)
            .Select(review => review.ActorId!.Value)
            .Distinct()
            .ToArray();

    public IAsyncEnumerable<PullRequestSummary> StreamPullRequestsAsync(
        RepositoryName repositoryName,
        string state,
        bool forceRefresh,
        CancellationToken cancellationToken) =>
        SelectPullRequestsAsync(
            StreamPullRequestEntriesAsync(repositoryName, state, forceRefresh, cancellationToken),
            cancellationToken);

    public async IAsyncEnumerable<PullRequestStreamEntry> StreamPullRequestEntriesAsync(
        RepositoryName repositoryName,
        string state,
        bool forceRefresh,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var scopeSelection = await GetRepositoryCacheScopeSelectionAsync(repositoryName, forceRefresh, cancellationToken);
        var scope = scopeSelection.Scope;
        var cacheKey = CreateRepositoryCacheKey(
            scope,
            repositoryName,
            "pulls",
            state);
        var transientFallbackCacheKey = cacheScopeResolver.IsPublicCacheAllowlisted(repositoryName)
            ? CreateRepositoryCacheKey(
                GitHubCachePolicy.CreatePublicRepositoryScope(),
                repositoryName,
                "pulls",
                state)
            : null;
        var refreshCache = scopeSelection.Refresh;

        var cachedPullRequests = await cache.GetAsync<IReadOnlyList<PullRequestSummary>>(cacheKey, cancellationToken);
        if (!refreshCache && cachedPullRequests is { Found: true, Value: not null })
        {
            foreach (var pullRequest in cachedPullRequests.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new PullRequestStreamEntry(pullRequest);
            }

            yield break;
        }

        var stalePullRequests = refreshCache
            ? await GetStaleSnapshotAsync<IReadOnlyList<PullRequestSummary>>(
                cacheKey,
                transientFallbackCacheKey,
                cancellationToken)
            : await GetCachedFallbackAsync<IReadOnlyList<PullRequestSummary>>(
                transientFallbackCacheKey,
                cancellationToken);
        if (!stalePullRequests.Found)
        {
            stalePullRequests = await GetStaleSnapshotAsync<IReadOnlyList<PullRequestSummary>>(
                cacheKey,
                transientFallbackCacheKey,
                cancellationToken);
        }
        var emittedStaleSnapshot = false;
        Dictionary<int, PullRequestSummary>? stalePullRequestsByNumber = null;
        if (stalePullRequests is { Found: true, Value: not null })
        {
            stalePullRequestsByNumber = new Dictionary<int, PullRequestSummary>();
            var staleRowsAreOverlay = !scopeSelection.CacheOnly;
            foreach (var stalePullRequest in stalePullRequests.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stalePullRequestsByNumber[stalePullRequest.Number] = stalePullRequest;
                yield return new PullRequestStreamEntry(stalePullRequest, staleRowsAreOverlay);
            }

            emittedStaleSnapshot = true;
        }

        if (scopeSelection.CacheOnly)
        {
            if (!emittedStaleSnapshot)
            {
                throw CreatePublicCacheUnavailableException();
            }

            yield break;
        }

        var completedPullRequestsByNumber = new Dictionary<int, PullRequestSummary>();
        var publicBaselinePullRequestsByNumber = new Dictionary<int, PullRequestSummary>();
        await using var livePullRequests = StreamPullRequestSummariesWithBaselineAsync(
            repositoryName,
            StreamPullRequestDtosAsync(repositoryName, state, scope, cancellationToken),
            enrichMergeableStateFromDetails: true,
            refreshCache,
            scope,
            stalePullRequestsByNumber,
            publicBaselinePullRequestsByNumber,
            cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            PullRequestStreamEntry livePullRequestEntry = default;
            PullRequestSummary? pullRequest = null;
            bool hasPullRequest;
            try
            {
                hasPullRequest = await livePullRequests.MoveNextAsync();
                if (hasPullRequest)
                {
                    livePullRequestEntry = livePullRequests.Current;
                    pullRequest = livePullRequestEntry.PullRequest;
                }
            }
            catch (Exception ex) when (emittedStaleSnapshot && IsTransientGitHubFailure(ex, cancellationToken))
            {
                yield break;
            }

            if (!hasPullRequest)
            {
                break;
            }

            if (pullRequest is not null)
            {
                completedPullRequestsByNumber[pullRequest.Number] = pullRequest;
                yield return livePullRequestEntry;
            }
        }

        var completedPullRequests = completedPullRequestsByNumber.Values.ToArray();
        await SetLastGoodAsync(cacheKey, completedPullRequests, cancellationToken);
        await SetCacheEntryAsync(cacheKey, completedPullRequests, CacheDurationForScope(scope), cancellationToken);
        if (!scope.IsShared)
        {
            await TrySetPublicPullRequestBaselineAsync(
                repositoryName,
                state,
                publicBaselinePullRequestsByNumber.Values.ToArray(),
                cancellationToken);
        }

        yield return new PullRequestStreamEntry(IsComplete: true);
    }

    public async Task<IReadOnlyList<PullRequestSummary>> GetPullRequestsByLabelAsync(
        RepositoryName repositoryName,
        string state,
        string label,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var normalizedLabel = label.Trim();
        if (string.IsNullOrWhiteSpace(normalizedLabel))
        {
            return await GetPullRequestsAsync(repositoryName, state, forceRefresh, cancellationToken);
        }

        var scopeSelection = await GetRepositoryCacheScopeSelectionAsync(repositoryName, forceRefresh, cancellationToken);
        var scope = scopeSelection.Scope;
        var cacheKey = CreateRepositoryCacheKey(
            scope,
            repositoryName,
            "pulls",
            state,
            "label",
            normalizedLabel);
        var transientFallbackCacheKey = CreateSharedFallbackCacheKey(
            scopeSelection.SharedFallbackScope,
            repositoryName,
            "pulls",
            state,
            "label",
            normalizedLabel);
        var refreshCache = scopeSelection.Refresh;
        return await GetOrCreateWithLastGoodFallbackAsync(
            cacheKey,
            CacheDurationForScope(scope),
            refreshCache,
            scopeSelection.CacheOnly,
            async () =>
        {
            if (scope.IsShared)
            {
                throw CreatePublicCacheUnavailableException();
            }

            var sort = state.Equals("open", StringComparison.OrdinalIgnoreCase) ? "created" : "updated";
            var direction = state.Equals("open", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";
            var issues = await GetIssuesAsync(
                repositoryName,
                state,
                label: normalizedLabel,
                milestoneNumber: null,
                sort: sort,
                direction: direction,
                scope: scope,
                cancellationToken: cancellationToken);
            var pullRequestTasks = issues
                .Where(issue => issue.PullRequest is not null)
                .ToDictionary(
                    issue => issue.Number,
                    issue => GetPullRequestDtoOrNullAsync(repositoryName, issue.Number, scope, cancellationToken));

            await Task.WhenAll(pullRequestTasks.Values);

            var pullRequestDtos = new List<GitHubPullRequestDto>(pullRequestTasks.Count);
            foreach (var task in pullRequestTasks.Values)
            {
                if (await task is { Draft: false } pullRequest)
                {
                    pullRequestDtos.Add(pullRequest);
                }
            }

            return await CreatePullRequestSummariesAsync(
                repositoryName,
                pullRequestDtos
                    .OrderBy(pullRequest => pullRequest.CreatedAt)
                    .ToArray(),
                null,
                refreshCache,
                scope,
                cancellationToken);
        },
            cancellationToken,
            transientFallbackCacheKey: transientFallbackCacheKey);
    }

    public IAsyncEnumerable<PullRequestSummary> StreamPullRequestsByLabelAsync(
        RepositoryName repositoryName,
        string state,
        string label,
        bool forceRefresh,
        CancellationToken cancellationToken) =>
        SelectPullRequestsAsync(
            StreamPullRequestEntriesByLabelAsync(repositoryName, state, label, forceRefresh, cancellationToken),
            cancellationToken);

    public async IAsyncEnumerable<PullRequestStreamEntry> StreamPullRequestEntriesByLabelAsync(
        RepositoryName repositoryName,
        string state,
        string label,
        bool forceRefresh,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var normalizedLabel = label.Trim();
        if (string.IsNullOrWhiteSpace(normalizedLabel))
        {
            await foreach (var entry in StreamPullRequestEntriesAsync(repositoryName, state, forceRefresh, cancellationToken))
            {
                yield return entry;
            }

            yield break;
        }

        var scopeSelection = await GetRepositoryCacheScopeSelectionAsync(repositoryName, forceRefresh, cancellationToken);
        var scope = scopeSelection.Scope;
        var cacheKey = CreateRepositoryCacheKey(
            scope,
            repositoryName,
            "pulls",
            state,
            "label",
            normalizedLabel);
        var transientFallbackCacheKey = CreateSharedFallbackCacheKey(
            scopeSelection.SharedFallbackScope,
            repositoryName,
            "pulls",
            state,
            "label",
            normalizedLabel);
        var refreshCache = scopeSelection.Refresh;

        var cachedPullRequests = await cache.GetAsync<IReadOnlyList<PullRequestSummary>>(cacheKey, cancellationToken);
        if (!refreshCache && cachedPullRequests is { Found: true, Value: not null })
        {
            foreach (var pullRequest in cachedPullRequests.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new PullRequestStreamEntry(pullRequest);
            }

            yield break;
        }

        var stalePullRequests = await GetStaleSnapshotAsync<IReadOnlyList<PullRequestSummary>>(
            cacheKey,
            transientFallbackCacheKey,
            cancellationToken);
        var emittedStaleSnapshot = false;
        Dictionary<int, PullRequestSummary>? stalePullRequestsByNumber = null;
        if (stalePullRequests is { Found: true, Value: not null })
        {
            stalePullRequestsByNumber = new Dictionary<int, PullRequestSummary>();
            var staleRowsAreOverlay = !scopeSelection.CacheOnly;
            foreach (var stalePullRequest in stalePullRequests.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stalePullRequestsByNumber[stalePullRequest.Number] = stalePullRequest;
                yield return new PullRequestStreamEntry(stalePullRequest, staleRowsAreOverlay);
            }

            emittedStaleSnapshot = true;
        }

        if (scopeSelection.CacheOnly)
        {
            if (!emittedStaleSnapshot)
            {
                throw CreatePublicCacheUnavailableException();
            }

            yield break;
        }

        var completedPullRequestsByNumber = new Dictionary<int, PullRequestSummary>();
        var publicBaselinePullRequestsByNumber = new Dictionary<int, PullRequestSummary>();
        await using var livePullRequests = StreamPullRequestSummariesWithBaselineAsync(
            repositoryName,
            StreamPullRequestDtosByLabelAsync(repositoryName, state, normalizedLabel, scope, cancellationToken),
            enrichMergeableStateFromDetails: false,
            refreshCache,
            scope,
            stalePullRequestsByNumber,
            publicBaselinePullRequestsByNumber,
            cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            PullRequestStreamEntry livePullRequestEntry = default;
            PullRequestSummary? pullRequest = null;
            bool hasPullRequest;
            try
            {
                hasPullRequest = await livePullRequests.MoveNextAsync();
                if (hasPullRequest)
                {
                    livePullRequestEntry = livePullRequests.Current;
                    pullRequest = livePullRequestEntry.PullRequest;
                }
            }
            catch (Exception ex) when (emittedStaleSnapshot && IsTransientGitHubFailure(ex, cancellationToken))
            {
                yield break;
            }

            if (!hasPullRequest)
            {
                break;
            }

            if (pullRequest is not null)
            {
                completedPullRequestsByNumber[pullRequest.Number] = pullRequest;
                yield return livePullRequestEntry;
            }
        }

        var completedPullRequests = completedPullRequestsByNumber.Values.ToArray();
        await SetLastGoodAsync(cacheKey, completedPullRequests, cancellationToken);
        await SetCacheEntryAsync(cacheKey, completedPullRequests, CacheDurationForScope(scope), cancellationToken);

        yield return new PullRequestStreamEntry(IsComplete: true);
    }

    public async Task<IReadOnlyList<ShipWeekIssueSummary>> GetFocusIssuesAsync(
        RepositoryName repositoryName,
        string state,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var scopeSelection = await GetRepositoryCacheScopeSelectionAsync(repositoryName, forceRefresh, cancellationToken);
        var scope = scopeSelection.Scope;
        var cacheKey = CreateRepositoryCacheKey(
            scope,
            repositoryName,
            "focus-issues",
            state);
        var transientFallbackCacheKey = CreateSharedFallbackCacheKey(
            scopeSelection.SharedFallbackScope,
            repositoryName,
            "focus-issues",
            state);
        var refreshCache = scopeSelection.Refresh;
        return await GetOrCreateWithLastGoodFallbackAsync(
            cacheKey,
            CacheDurationForScope(scope),
            refreshCache,
            scopeSelection.CacheOnly,
            async () =>
        {
            if (scope.IsShared)
            {
                throw CreatePublicCacheUnavailableException();
            }

            var currentUserLoginTask = GetCurrentUserLoginAsync(cancellationToken);
            var regressionIssuesTask = GetIssuesMatchingLabelMarkerAsync(
                repositoryName,
                state,
                RegressionLabelMarker,
                refreshCache,
                scope,
                cancellationToken);
            var ctiTeamIssuesTask = SearchIssuesByTitleMarkerAsync(
                repositoryName,
                state,
                CtiTeamTitleMarker,
                CtiTeamSearchTerm,
                scope,
                cancellationToken);

            await Task.WhenAll(regressionIssuesTask, ctiTeamIssuesTask, currentUserLoginTask);

            IReadOnlyList<GitHubIssueDto> assignedIssues = string.IsNullOrWhiteSpace(currentUserLoginTask.Result)
                ? []
                : await GetIssuesAssignedToAsync(
                    repositoryName,
                    state,
                    currentUserLoginTask.Result,
                    scope,
                    cancellationToken);

            // "Focus issues" are the actual issue cards shown in the issues dashboard. The
            // source searches can overlap and GitHub's issues APIs can return PRs, so normalize
            // them to a unique set of non-PR issues before applying linked-PR activity.
            var focusIssues = regressionIssuesTask.Result
                .Concat(ctiTeamIssuesTask.Result)
                .Concat(assignedIssues)
                .Where(issue => issue.PullRequest is null)
                .GroupBy(issue => issue.Number)
                .Select(group => group.OrderByDescending(issue => issue.UpdatedAt).First())
                .OrderByDescending(issue => issue.UpdatedAt)
                .ToArray();
            var linkedOpenPullRequestsByIssue = await GetLinkedOpenPullRequestsByFocusIssueAsync(
                repositoryName,
                focusIssues,
                scope,
                cancellationToken);
            var fetchedAt = DateTimeOffset.UtcNow;
            return focusIssues
                .Select(issue =>
                {
                    var linkedOpenPullRequests = linkedOpenPullRequestsByIssue.TryGetValue(issue.Number, out var linked)
                        ? linked
                        : [];
                    return ShipWeekIssueSummary.FromDto(repositoryName, issue, linkedOpenPullRequests) with
                    {
                        FetchedAt = fetchedAt
                    };
                })
                .OrderByDescending(issue => issue.UpdatedAt)
                .ToArray();
        },
            cancellationToken,
            transientFallbackCacheKey: transientFallbackCacheKey);
    }

    public async Task<ShipWeekLoadResult> GetShipWeekAsync(
        RepositoryName repositoryName,
        string milestoneTitle,
        string? releaseBranch,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var normalizedMilestoneTitle = milestoneTitle.Trim();
        var requestedReleaseBranch = releaseBranch?.Trim();
        var scopeSelection = await GetRepositoryCacheScopeSelectionAsync(repositoryName, forceRefresh, cancellationToken);
        var scope = scopeSelection.Scope;
        var cacheKey = CreateRepositoryCacheKey(
            scope,
            repositoryName,
            "ship-week",
            normalizedMilestoneTitle,
            requestedReleaseBranch ?? "latest-release");
        var transientFallbackCacheKey = CreateSharedFallbackCacheKey(
            scopeSelection.SharedFallbackScope,
            repositoryName,
            "ship-week",
            normalizedMilestoneTitle,
            requestedReleaseBranch ?? "latest-release");
        var refreshCache = scopeSelection.Refresh;
        return await GetOrCreateWithLastGoodFallbackAsync(
            cacheKey,
            CacheDurationForScope(scope),
            refreshCache,
            scopeSelection.CacheOnly,
            async () =>
        {
            if (scope.IsShared)
            {
                throw CreatePublicCacheUnavailableException();
            }

            var milestone = await GetMilestoneByTitleAsync(repositoryName, normalizedMilestoneTitle, scope, cancellationToken);
            if (milestone is null)
            {
                return ShipWeekLoadResult.ValidationProblem(
                    "milestone",
                    $"Milestone '{normalizedMilestoneTitle}' was not found in {repositoryName}.");
            }

            var normalizedReleaseBranch = string.IsNullOrWhiteSpace(requestedReleaseBranch)
                ? await GetLatestReleaseBranchAsync(repositoryName, scope, cancellationToken)
                : requestedReleaseBranch;

            if (string.IsNullOrWhiteSpace(normalizedReleaseBranch))
            {
                return ShipWeekLoadResult.ValidationProblem(
                    "releaseBranch",
                    $"No release/* branches were found in {repositoryName}.");
            }

            if (!string.IsNullOrWhiteSpace(requestedReleaseBranch)
                && !await BranchExistsAsync(repositoryName, normalizedReleaseBranch, scope, cancellationToken))
            {
                return ShipWeekLoadResult.ValidationProblem(
                    "releaseBranch",
                    $"Branch '{normalizedReleaseBranch}' was not found in {repositoryName}.");
            }

            var milestoneIssuesTask = GetOpenMilestoneIssuesAsync(repositoryName, milestone.Number, scope, cancellationToken);
            var releaseBranchPullRequestsTask = GetOpenPullRequestsByBaseAsync(repositoryName, normalizedReleaseBranch, scope, cancellationToken);
            await Task.WhenAll(milestoneIssuesTask, releaseBranchPullRequestsTask);

            var milestoneIssues = await milestoneIssuesTask;
            var releaseBranchPullRequestDtos = await releaseBranchPullRequestsTask;
            var draftReleaseBranchPullRequestNumbers = releaseBranchPullRequestDtos
                .Where(pullRequest => pullRequest.Draft)
                .Select(pullRequest => pullRequest.Number)
                .ToHashSet();
            var activeReleaseBranchPullRequestDtos = releaseBranchPullRequestDtos
                .Where(pullRequest => !pullRequest.Draft)
                .ToArray();
            var pullRequestDtosByNumber = activeReleaseBranchPullRequestDtos
                .GroupBy(pullRequest => pullRequest.Number)
                .ToDictionary(group => group.Key, group => group.First());
            var releaseBranchPullRequestNumbers = pullRequestDtosByNumber.Keys.ToHashSet();
            var milestonePullRequestNumbers = milestoneIssues
                .Where(issue => issue.PullRequest is not null)
                .Select(issue => issue.Number)
                .ToHashSet();

            var missingMilestonePullRequestTasks = milestonePullRequestNumbers
                .Where(number => !pullRequestDtosByNumber.ContainsKey(number))
                .Where(number => !draftReleaseBranchPullRequestNumbers.Contains(number))
                .ToDictionary(
                    number => number,
                    number => GetPullRequestDtoOrNullAsync(repositoryName, number, scope, cancellationToken));

            await Task.WhenAll(missingMilestonePullRequestTasks.Values);

            foreach (var (number, task) in missingMilestonePullRequestTasks)
            {
                if (await task is { Draft: false } pullRequest)
                {
                    pullRequestDtosByNumber[number] = pullRequest;
                }
            }

            var pullRequestSummaries = await CreatePullRequestSummariesAsync(
                repositoryName,
                pullRequestDtosByNumber.Values
                    .OrderBy(pullRequest => pullRequest.CreatedAt)
                    .ToArray(),
                releaseBranchPullRequestNumbers,
                refreshCache,
                scope,
                cancellationToken);

            var nonPullRequestIssues = milestoneIssues
                .Where(issue => issue.PullRequest is null)
                .OrderByDescending(issue => issue.UpdatedAt)
                .ToArray();
            var milestoneIssueNumbers = nonPullRequestIssues
                .Select(issue => issue.Number)
                .ToHashSet();
            var linkedOpenPullRequestsByIssue = CreateLinkedOpenPullRequestsByIssue(
                repositoryName,
                normalizedMilestoneTitle,
                pullRequestSummaries,
                milestoneIssueNumbers);
            var fetchedAt = DateTimeOffset.UtcNow;
            var shipWeekIssues = nonPullRequestIssues
                .Select(issue =>
                {
                    var linkedOpenPullRequests = linkedOpenPullRequestsByIssue.TryGetValue(issue.Number, out var linked)
                        ? linked
                        : [];
                    return ShipWeekIssueSummary.FromDto(repositoryName, issue, linkedOpenPullRequests) with
                    {
                        FetchedAt = fetchedAt
                    };
                })
                .ToArray();

            var shipWeekPullRequests = pullRequestSummaries
                .Select(pullRequest =>
                {
                    var linkedMilestoneIssueNumbers = pullRequest.LinkedIssues
                        .Where(issue => RepositoryMatches(issue.Repository, repositoryName)
                            && (MilestoneTitleMatches(issue.Milestone, normalizedMilestoneTitle)
                                || milestoneIssueNumbers.Contains(issue.Number)))
                        .Select(issue => issue.Number)
                        .Distinct()
                        .OrderBy(number => number)
                        .ToArray();
                    var inMilestone = milestonePullRequestNumbers.Contains(pullRequest.Number)
                        || MilestoneTitleMatches(pullRequest.Milestone, normalizedMilestoneTitle)
                        || linkedMilestoneIssueNumbers.Length > 0;
                    var targetsReleaseBranch = releaseBranchPullRequestNumbers.Contains(pullRequest.Number)
                        || string.Equals(pullRequest.BaseRef, normalizedReleaseBranch, StringComparison.OrdinalIgnoreCase);

                    return new ShipWeekPullRequestSummary(
                        pullRequest,
                        new ShipWeekReleaseScope(
                            InMilestone: inMilestone,
                            TargetsReleaseBranch: targetsReleaseBranch,
                            ReleaseBranchException: targetsReleaseBranch && !inMilestone,
                            MilestoneIssueNumbers: linkedMilestoneIssueNumbers));
                })
                .OrderByDescending(item => item.ReleaseScope.ReleaseBranchException)
                .ThenBy(item => item.PullRequest.CreatedAt)
                .ToArray();

            return ShipWeekLoadResult.Success(new ShipWeekResponse(
                repositoryName.ToString(),
                normalizedMilestoneTitle,
                normalizedReleaseBranch,
                shipWeekPullRequests,
                shipWeekIssues));
        },
            cancellationToken,
            result => result.Response is not null,
            transientFallbackCacheKey: transientFallbackCacheKey);
    }

    private async IAsyncEnumerable<PullRequestStreamEntry> StreamPullRequestSummariesWithBaselineAsync(
        RepositoryName repositoryName,
        IAsyncEnumerable<GitHubPullRequestDto> pullRequestDtos,
        bool enrichMergeableStateFromDetails,
        bool forceRefresh,
        GitHubCacheScope scope,
        IReadOnlyDictionary<int, PullRequestSummary>? stalePullRequestsByNumber,
        IDictionary<int, PullRequestSummary> publicBaselinePullRequestsByNumber,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var seenPullRequests = new HashSet<int>();
        var batch = new List<GitHubPullRequestDto>(PullRequestStreamBatchSize);

        await foreach (var pullRequestDto in pullRequestDtos.WithCancellation(cancellationToken))
        {
            if (!seenPullRequests.Add(pullRequestDto.Number))
            {
                continue;
            }

            var baseline = CreatePullRequestBaselineSummary(pullRequestDto);
            publicBaselinePullRequestsByNumber[baseline.Number] = baseline;
            if (stalePullRequestsByNumber?.TryGetValue(baseline.Number, out var stalePullRequest) == true)
            {
                yield return new PullRequestStreamEntry(
                    CreateStalePreservingLiveBaseline(stalePullRequest, baseline),
                    IsStale: true,
                    IsStaleRefreshOverlay: true);
            }
            else
            {
                yield return new PullRequestStreamEntry(baseline);
            }

            batch.Add(pullRequestDto);
            if (batch.Count < PullRequestStreamBatchSize)
            {
                continue;
            }

            foreach (var pullRequest in await CreatePullRequestSummariesAsync(
                repositoryName,
                batch,
                MergeableStateEnrichmentNumbers(batch, enrichMergeableStateFromDetails),
                forceRefresh,
                scope,
                cancellationToken))
            {
                yield return new PullRequestStreamEntry(pullRequest);
            }

            batch.Clear();
        }

        if (batch.Count == 0)
        {
            yield break;
        }

        foreach (var pullRequest in await CreatePullRequestSummariesAsync(
            repositoryName,
            batch,
            MergeableStateEnrichmentNumbers(batch, enrichMergeableStateFromDetails),
            forceRefresh,
            scope,
            cancellationToken))
        {
            yield return new PullRequestStreamEntry(pullRequest);
        }
    }

    private async Task TrySetPublicPullRequestBaselineAsync(
        RepositoryName repositoryName,
        string state,
        IReadOnlyList<PullRequestSummary> pullRequests,
        CancellationToken cancellationToken)
    {
        if (pullRequests.Count == 0
            || !cacheScopeResolver.IsPublicCacheAllowlisted(repositoryName))
        {
            return;
        }

        var eligibility = await cacheScopeResolver.GetPublicCacheRepositoryEligibilityOrUnverifiedAsync(
            repositoryName,
            cancellationToken);
        if (eligibility != GitHubPublicCacheRepositoryEligibility.Public)
        {
            if (eligibility == GitHubPublicCacheRepositoryEligibility.NotPublic)
            {
                await publicCacheStore.RemoveRepositoryAsync(repositoryName, cancellationToken);
            }

            return;
        }

        var publicScope = GitHubCachePolicy.CreatePublicRepositoryScope();
        var publicCacheKey = CreateRepositoryCacheKey(
            publicScope,
            repositoryName,
            "pulls",
            state);
        await SetLastGoodAsync(publicCacheKey, pullRequests, cancellationToken);
        await SetCacheEntryAsync(publicCacheKey, pullRequests, CacheDurationForScope(publicScope), cancellationToken);
        await publicCacheStore.TrackAsync(repositoryName, publicCacheKey, cancellationToken);
    }

    private async IAsyncEnumerable<GitHubPullRequestDto> StreamPullRequestDtosAsync(
        RepositoryName repositoryName,
        string state,
        GitHubCacheScope scope,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sort = state.Equals("open", StringComparison.OrdinalIgnoreCase) ? "created" : "updated";
        var direction = state.Equals("open", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";
        var url = $"repos/{repositoryName.Owner}/{repositoryName.Name}/pulls?state={Uri.EscapeDataString(state)}&sort={sort}&direction={direction}&per_page={PullRequestPageSize}";

        await foreach (var pullRequest in SendPagedGitHubRequestStreamAsync(
            url,
            GitHubJsonSerializerContext.Default.GitHubPullRequestDtoArray,
            scope.RequestAuthorization,
            cancellationToken))
        {
            if (!pullRequest.Draft)
            {
                yield return pullRequest;
            }
        }
    }

    private async IAsyncEnumerable<GitHubPullRequestDto> StreamPullRequestDtosByLabelAsync(
        RepositoryName repositoryName,
        string state,
        string label,
        GitHubCacheScope scope,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sort = state.Equals("open", StringComparison.OrdinalIgnoreCase) ? "created" : "updated";
        var direction = state.Equals("open", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";
        var issueBatch = new List<int>(PullRequestStreamBatchSize);

        await foreach (var issue in StreamIssuesAsync(
            repositoryName,
            state,
            label: label,
            sort: sort,
            direction: direction,
            scope: scope,
            cancellationToken: cancellationToken))
        {
            if (issue.PullRequest is null)
            {
                continue;
            }

            issueBatch.Add(issue.Number);
            if (issueBatch.Count < PullRequestStreamBatchSize)
            {
                continue;
            }

            foreach (var pullRequest in await GetPullRequestDtosByNumberAsync(repositoryName, issueBatch, scope, cancellationToken))
            {
                yield return pullRequest;
            }

            issueBatch.Clear();
        }

        if (issueBatch.Count == 0)
        {
            yield break;
        }

        foreach (var pullRequest in await GetPullRequestDtosByNumberAsync(repositoryName, issueBatch, scope, cancellationToken))
        {
            yield return pullRequest;
        }
    }

    private async Task<IReadOnlyList<GitHubPullRequestDto>> GetPullRequestDtosByNumberAsync(
        RepositoryName repositoryName,
        IReadOnlyList<int> numbers,
        GitHubCacheScope scope,
        CancellationToken cancellationToken)
    {
        var pullRequestTasks = numbers
            .Distinct()
            .ToDictionary(
                number => number,
                number => GetPullRequestDtoOrNullAsync(repositoryName, number, scope, cancellationToken));

        await Task.WhenAll(pullRequestTasks.Values);

        var pullRequests = new List<GitHubPullRequestDto>(pullRequestTasks.Count);
        foreach (var task in pullRequestTasks.Values)
        {
            if (await task is { Draft: false } pullRequest)
            {
                pullRequests.Add(pullRequest);
            }
        }

        return pullRequests
            .OrderBy(pullRequest => pullRequest.CreatedAt)
            .ToArray();
    }

    private async Task<IReadOnlyList<PullRequestSummary>> CreatePullRequestSummariesAsync(
        RepositoryName repositoryName,
        IReadOnlyList<GitHubPullRequestDto> pullRequestDtos,
        IReadOnlySet<int>? mergeableStateEnrichmentNumbers,
        bool forceRefresh,
        GitHubCacheScope scope,
        CancellationToken cancellationToken)
    {
        var uniquePullRequestDtos = pullRequestDtos
            .GroupBy(pullRequest => pullRequest.Number)
            .Select(group => group.First())
            .ToArray();
        var pullRequests = uniquePullRequestDtos
            .Select(PullRequestSummary.FromDto)
            .ToArray();

        if (pullRequests.Length == 0)
        {
            return [];
        }

        var reviewTasks = pullRequests.ToDictionary(
            pullRequest => pullRequest.Number,
            pullRequest => GetReviewStatusAsync(repositoryName, pullRequest.Number, forceRefresh, scope, cacheOnly: false, cancellationToken));
        var linkedIssueTasks = uniquePullRequestDtos.ToDictionary(
            pullRequest => pullRequest.Number,
            pullRequest => GetLinkedIssuesAsync(repositoryName, pullRequest.Body, forceRefresh, scope, cancellationToken));

        await Task.WhenAll(reviewTasks.Values);
        await Task.WhenAll(linkedIssueTasks.Values);

        var reviewsByPullRequest = reviewTasks.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Result);
        var detailTasks = pullRequests
            .Where(pullRequest => NeedsPullRequestDetails(
                pullRequest,
                reviewsByPullRequest[pullRequest.Number],
                mergeableStateEnrichmentNumbers))
            .ToDictionary(
                pullRequest => pullRequest.Number,
                pullRequest => GetPullRequestDetailsOrNullAsync(repositoryName, pullRequest.Number, forceRefresh, scope, cancellationToken));
        var lastCommitTasks = pullRequests
            .Where(pullRequest =>
                reviewsByPullRequest[pullRequest.Number] is { LastReviewedAt: not null } review
                && (review.State == "reviewed" || review.State == "changes_requested"))
            .ToDictionary(
                pullRequest => pullRequest.Number,
                pullRequest => GetLastCommitAtAsync(repositoryName, pullRequest.Number, forceRefresh, scope, cancellationToken));

        await Task.WhenAll(detailTasks.Values);
        await Task.WhenAll(lastCommitTasks.Values);

        var detailsByPullRequest = new Dictionary<int, PullRequestDetails?>(detailTasks.Count);
        foreach (var (number, task) in detailTasks)
        {
            detailsByPullRequest[number] = await task;
        }

        var lastCommitByPullRequest = new Dictionary<int, DateTimeOffset?>(lastCommitTasks.Count);
        foreach (var (number, task) in lastCommitTasks)
        {
            lastCommitByPullRequest[number] = await task;
        }

        var linkedIssuesByPullRequest = new Dictionary<int, IReadOnlyList<LinkedIssueSummary>>(linkedIssueTasks.Count);
        foreach (var (number, task) in linkedIssueTasks)
        {
            linkedIssuesByPullRequest[number] = await task;
        }

        var fetchedAt = DateTimeOffset.UtcNow;
        return pullRequests
            .Select(pullRequest =>
            {
                detailsByPullRequest.TryGetValue(pullRequest.Number, out var details);
                lastCommitByPullRequest.TryGetValue(pullRequest.Number, out var lastCommitAt);
                return pullRequest with
                {
                    FetchedAt = fetchedAt,
                    LinkedIssues = linkedIssuesByPullRequest[pullRequest.Number],
                    CommitCount = details?.CommitCount ?? pullRequest.CommitCount,
                    Additions = details?.Additions ?? pullRequest.Additions,
                    Deletions = details?.Deletions ?? pullRequest.Deletions,
                    ChangedFiles = details?.ChangedFiles ?? pullRequest.ChangedFiles,
                    LastCommitAt = lastCommitAt,
                    MergeableState = details?.MergeableState ?? pullRequest.MergeableState,
                    Review = reviewsByPullRequest[pullRequest.Number],
                    Checks = pullRequest.Checks
                };
            })
            .ToArray();
    }

    private static IReadOnlyList<PullRequestSummary> CreatePullRequestBaselineSummaries(
        IReadOnlyList<GitHubPullRequestDto> pullRequestDtos)
        => pullRequestDtos
            .GroupBy(pullRequest => pullRequest.Number)
            .Select(group => CreatePullRequestBaselineSummary(group.First()))
            .ToArray();

    private static PullRequestSummary CreatePullRequestBaselineSummary(GitHubPullRequestDto pullRequest) =>
        PullRequestSummary.FromDto(pullRequest) with
        {
            FetchedAt = DateTimeOffset.UtcNow,
            Checks = ChecksStatus.None
        };

    private static PullRequestSummary CreateStalePreservingLiveBaseline(
        PullRequestSummary stalePullRequest,
        PullRequestSummary liveBaseline) =>
        liveBaseline with
        {
            LinkedIssues = stalePullRequest.LinkedIssues,
            CommitCount = stalePullRequest.CommitCount,
            Additions = stalePullRequest.Additions,
            Deletions = stalePullRequest.Deletions,
            ChangedFiles = stalePullRequest.ChangedFiles,
            LastCommitAt = stalePullRequest.LastCommitAt,
            MergeableState = liveBaseline.MergeableState ?? stalePullRequest.MergeableState,
            Review = stalePullRequest.Review,
            Checks = stalePullRequest.Checks
        };

    private static IReadOnlySet<int>? MergeableStateEnrichmentNumbers(
        IReadOnlyList<GitHubPullRequestDto> pullRequests,
        bool enrichMergeableStateFromDetails) =>
        enrichMergeableStateFromDetails
            ? pullRequests.Select(pullRequest => pullRequest.Number).ToHashSet()
            : null;

    private static bool NeedsPullRequestDetails(
        PullRequestSummary pullRequest,
        ReviewStatus review,
        IReadOnlySet<int>? mergeableStateEnrichmentNumbers) =>
        review.State == "waiting"
            || (pullRequest.State.Equals("open", StringComparison.OrdinalIgnoreCase)
                && mergeableStateEnrichmentNumbers?.Contains(pullRequest.Number) == true);

    private async Task<GitHubMilestoneDto?> GetMilestoneByTitleAsync(
        RepositoryName repositoryName,
        string milestoneTitle,
        GitHubCacheScope scope,
        CancellationToken cancellationToken)
    {
        var milestones = await SendPagedGitHubRequestAsync(
            $"repos/{repositoryName.Owner}/{repositoryName.Name}/milestones?state=all&per_page=100",
            GitHubJsonSerializerContext.Default.GitHubMilestoneDtoArray,
            scope.RequestAuthorization,
            cancellationToken);
        return milestones.FirstOrDefault(milestone =>
            string.Equals(milestone.Title?.Trim(), milestoneTitle, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string?> GetLatestReleaseBranchAsync(
        RepositoryName repositoryName,
        GitHubCacheScope scope,
        CancellationToken cancellationToken)
    {
        var references = await SendGitHubRequestAsync(
            $"repos/{repositoryName.Owner}/{repositoryName.Name}/git/matching-refs/heads/release/",
            GitHubJsonSerializerContext.Default.GitHubGitReferenceDtoArray,
            scope.RequestAuthorization,
            cancellationToken);

        return references
            .Select(reference => TryGetBranchName(reference.Ref))
            .Where(branch => branch is not null)
            .Select(branch => branch!)
            .OrderByDescending(ReleaseBranchSortKey)
            .FirstOrDefault();
    }

    private static string? TryGetBranchName(string? gitReference)
    {
        const string branchPrefix = "refs/heads/";
        return gitReference?.StartsWith(branchPrefix, StringComparison.OrdinalIgnoreCase) is true
            ? gitReference[branchPrefix.Length..]
            : null;
    }

    private static (int Major, int Minor, int Patch, string Name) ReleaseBranchSortKey(string branch)
    {
        var suffix = branch["release/".Length..];
        var versionParts = suffix.Split(['.', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        return (
            Major: ParseReleaseBranchVersionPart(versionParts, 0),
            Minor: ParseReleaseBranchVersionPart(versionParts, 1),
            Patch: ParseReleaseBranchVersionPart(versionParts, 2),
            Name: branch);
    }

    private static int ParseReleaseBranchVersionPart(string[] versionParts, int index) =>
        index < versionParts.Length && int.TryParse(versionParts[index], out var value) ? value : -1;

    private async Task<bool> BranchExistsAsync(
        RepositoryName repositoryName,
        string branch,
        GitHubCacheScope scope,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendGitHubRequestAsync(
                $"repos/{repositoryName.Owner}/{repositoryName.Name}/branches/{Uri.EscapeDataString(branch)}",
                GitHubJsonSerializerContext.Default.GitHubBranchDto,
                scope.RequestAuthorization,
                cancellationToken);
            return true;
        }
        catch (GitHubApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<GitHubIssueDto>> GetOpenMilestoneIssuesAsync(
        RepositoryName repositoryName,
        int milestoneNumber,
        GitHubCacheScope scope,
        CancellationToken cancellationToken) =>
        await GetIssuesAsync(
            repositoryName,
            "open",
            label: null,
            milestoneNumber: milestoneNumber,
            sort: null,
            direction: null,
            scope: scope,
            cancellationToken: cancellationToken);

    private async Task<IReadOnlyList<GitHubIssueDto>> GetIssuesMatchingLabelMarkerAsync(
        RepositoryName repositoryName,
        string state,
        string labelMarker,
        bool forceRefresh,
        GitHubCacheScope scope,
        CancellationToken cancellationToken)
    {
        var matchingLabels = await GetLabelNamesContainingAsync(repositoryName, labelMarker, forceRefresh, scope, cancellationToken);
        if (matchingLabels.Count == 0)
        {
            return [];
        }

        var issueTasks = matchingLabels
            .ToDictionary(
                label => label,
                label => GetIssuesByLabelAsync(repositoryName, state, label, scope, cancellationToken));

        await Task.WhenAll(issueTasks.Values);

        return issueTasks.Values
            .SelectMany(task => task.Result)
            .Where(issue => issue.PullRequest is null && HasLabelContaining(issue.Labels, labelMarker))
            .GroupBy(issue => issue.Number)
            .Select(group => group.First())
            .OrderByDescending(issue => issue.UpdatedAt)
            .ToArray();
    }

    private async Task<IReadOnlyList<GitHubIssueDto>> SearchIssuesByTitleMarkerAsync(
        RepositoryName repositoryName,
        string state,
        string titleMarker,
        string searchTerm,
        GitHubCacheScope scope,
        CancellationToken cancellationToken)
    {
        var issues = await SendPagedGitHubIssueSearchRequestAsync(
            CreateIssueSearchUrl(repositoryName, state, searchTerm),
            scope.RequestAuthorization,
            cancellationToken);

        return issues
            .Where(issue => issue.PullRequest is null
                && issue.Title?.Contains(titleMarker, StringComparison.OrdinalIgnoreCase) is true)
            .ToArray();
    }

    private async Task<IReadOnlyDictionary<int, IReadOnlyList<LinkedOpenPullRequestSummary>>> GetLinkedOpenPullRequestsByFocusIssueAsync(
        RepositoryName repositoryName,
        IReadOnlyList<GitHubIssueDto> focusIssues,
        GitHubCacheScope scope,
        CancellationToken cancellationToken)
    {
        var focusIssueNodeIds = focusIssues
            .Select(issue => issue.NodeId)
            .Where(nodeId => !string.IsNullOrWhiteSpace(nodeId))
            .Select(nodeId => nodeId!)
            .Distinct(StringComparer.Ordinal)
            .Order()
            .ToArray();
        if (focusIssueNodeIds.Length == 0)
        {
            return new Dictionary<int, IReadOnlyList<LinkedOpenPullRequestSummary>>();
        }

        var token = await GetGraphQlTokenAsync(scope.RequestAuthorization, cancellationToken);
        if (token is null)
        {
            return new Dictionary<int, IReadOnlyList<LinkedOpenPullRequestSummary>>();
        }

        var linkedOpenPullRequestsByIssue = new Dictionary<int, List<LinkedOpenPullRequestSummary>>();
        // Keep this proportional to the rendered focus issues: one GraphQL batch per 20 issues,
        // reading only each issue's recent cross-reference events instead of scanning repo PRs.
        foreach (var nodeIdBatch in focusIssueNodeIds.Chunk(FocusIssueGraphQlBatchSize))
        {
            try
            {
                var issueNodes = await SendLinkedPullRequestsBatchAsync(
                    nodeIdBatch,
                    token,
                    cancellationToken);
                if (issueNodes is { Count: > 0 })
                {
                    AddLinkedOpenPullRequestsByFocusIssue(
                        repositoryName,
                        issueNodes,
                        linkedOpenPullRequestsByIssue);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsBestEffortGraphQlFailure(ex))
            {
                continue;
            }
        }

        return NormalizeLinkedOpenPullRequestsByIssue(linkedOpenPullRequestsByIssue);
    }

    private async Task<IReadOnlyList<GitHubLinkedPullRequestsIssueNodeDto?>> SendLinkedPullRequestsBatchAsync(
        IReadOnlyList<string> nodeIds,
        string token,
        CancellationToken cancellationToken)
    {
        await s_githubRequestThrottle.WaitAsync(cancellationToken);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "graphql")
            {
                Content = JsonContent.Create(
                    new GitHubLinkedPullRequestsGraphQlRequestDto
                    {
                        Query = FocusIssueLinkedPullRequestsGraphQlQuery,
                        Variables = new GitHubLinkedPullRequestsVariablesDto
                        {
                            Ids = nodeIds
                        }
                    },
                    GitHubJsonSerializerContext.Default.GitHubLinkedPullRequestsGraphQlRequestDto),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var payload = await response.Content.ReadFromJsonAsync(
                GitHubJsonSerializerContext.Default.GitHubLinkedPullRequestsResponseDto,
                cancellationToken);
            return payload?.Data?.Nodes ?? [];
        }
        finally
        {
            s_githubRequestThrottle.Release();
        }
    }

    private async Task<string?> GetGraphQlTokenAsync(
        GitHubRequestAuthorization authorization,
        CancellationToken cancellationToken) =>
        authorization switch
        {
            GitHubRequestAuthorization.Token => (await tokenProvider.GetTokenAsync(cancellationToken))?.Value,
            GitHubRequestAuthorization.PublicCacheToken => publicCacheIdentity.GetToken()?.Value,
            _ => null,
        };

    private async Task<IReadOnlyList<string>> GetLabelNamesContainingAsync(
        RepositoryName repositoryName,
        string labelMarker,
        bool forceRefresh,
        GitHubCacheScope scope,
        CancellationToken cancellationToken)
    {
        var cacheKey = CreateRepositoryCacheKey(
            scope,
            repositoryName,
            "matching-labels",
            labelMarker);
        var refreshCache = forceRefresh;
        return await GetOrRefreshCacheAsync<IReadOnlyList<string>>(
            cacheKey,
            TimeSpan.FromMinutes(5),
            refreshCache,
            cacheOnly: false,
            async () =>
        {
            var labels = await SendPagedGitHubRequestAsync(
                $"repos/{repositoryName.Owner}/{repositoryName.Name}/labels?per_page=100",
                GitHubJsonSerializerContext.Default.GitHubLabelDtoArray,
                scope.RequestAuthorization,
                cancellationToken);
            return labels
                .Select(label => label.Name)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Select(label => label!)
                .Where(label => label.Contains(labelMarker, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        },
            cancellationToken) ?? [];
    }

    private async Task<IReadOnlyList<GitHubIssueDto>> GetIssuesByLabelAsync(
        RepositoryName repositoryName,
        string state,
        string label,
        GitHubCacheScope scope,
        CancellationToken cancellationToken) =>
        await GetIssuesAsync(
            repositoryName,
            state,
            label: label,
            milestoneNumber: null,
            sort: "updated",
            direction: "desc",
            scope: scope,
            cancellationToken: cancellationToken);

    private async Task<IReadOnlyList<GitHubIssueDto>> GetIssuesAssignedToAsync(
        RepositoryName repositoryName,
        string state,
        string assignee,
        GitHubCacheScope scope,
        CancellationToken cancellationToken) =>
        await GetIssuesAsync(
            repositoryName,
            state,
            label: null,
            milestoneNumber: null,
            sort: "updated",
            direction: "desc",
            scope: scope,
            cancellationToken: cancellationToken,
            assignee: assignee);

    private async Task<IReadOnlyList<GitHubIssueDto>> GetIssuesAsync(
        RepositoryName repositoryName,
        string state,
        string? label,
        int? milestoneNumber,
        string? sort,
        string? direction,
        GitHubCacheScope scope,
        CancellationToken cancellationToken,
        string? assignee = null) =>
        await SendPagedGitHubRequestAsync(
            CreateIssuesUrl(repositoryName, state, label, milestoneNumber, sort, direction, assignee),
            GitHubJsonSerializerContext.Default.GitHubIssueDtoArray,
            scope.RequestAuthorization,
            cancellationToken);

    private static string CreateIssueSearchUrl(
        RepositoryName repositoryName,
        string state,
        string searchTerm)
    {
        var queryTerms = new List<string>
        {
            $"repo:{repositoryName.Owner}/{repositoryName.Name}",
            "is:issue"
        };

        if (!state.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            queryTerms.Add($"state:{state}");
        }

        queryTerms.Add("in:title");
        queryTerms.Add(searchTerm);

        return $"search/issues?q={Uri.EscapeDataString(string.Join(" ", queryTerms))}&sort=updated&order=desc&per_page={PullRequestPageSize}";
    }

    private async IAsyncEnumerable<GitHubIssueDto> StreamIssuesAsync(
        RepositoryName repositoryName,
        string state,
        string? label,
        string? sort,
        string? direction,
        GitHubCacheScope scope,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var issue in SendPagedGitHubRequestStreamAsync(
            CreateIssuesUrl(
                repositoryName,
                state,
                label,
                milestoneNumber: null,
                sort,
                direction,
                assignee: null),
            GitHubJsonSerializerContext.Default.GitHubIssueDtoArray,
            scope.RequestAuthorization,
            cancellationToken))
        {
            yield return issue;
        }
    }

    private static string CreateIssuesUrl(
        RepositoryName repositoryName,
        string state,
        string? label,
        int? milestoneNumber,
        string? sort,
        string? direction,
        string? assignee = null)
    {
        var queryParts = new List<string>
        {
            $"state={Uri.EscapeDataString(state)}"
        };

        if (!string.IsNullOrWhiteSpace(label))
        {
            queryParts.Add($"labels={Uri.EscapeDataString(label)}");
        }

        if (milestoneNumber is { } milestone)
        {
            queryParts.Add($"milestone={milestone}");
        }

        if (!string.IsNullOrWhiteSpace(assignee))
        {
            queryParts.Add($"assignee={Uri.EscapeDataString(assignee)}");
        }

        if (!string.IsNullOrWhiteSpace(sort))
        {
            queryParts.Add($"sort={Uri.EscapeDataString(sort)}");
        }

        if (!string.IsNullOrWhiteSpace(direction))
        {
            queryParts.Add($"direction={Uri.EscapeDataString(direction)}");
        }

        queryParts.Add($"per_page={PullRequestPageSize}");

        return $"repos/{repositoryName.Owner}/{repositoryName.Name}/issues?{string.Join("&", queryParts)}";
    }

    private async Task<IReadOnlyList<GitHubPullRequestDto>> GetOpenPullRequestsByBaseAsync(
        RepositoryName repositoryName,
        string baseRef,
        GitHubCacheScope scope,
        CancellationToken cancellationToken) =>
        await SendPagedGitHubRequestAsync(
            $"repos/{repositoryName.Owner}/{repositoryName.Name}/pulls?state=open&base={Uri.EscapeDataString(baseRef)}&sort=created&direction=asc&per_page={PullRequestPageSize}",
            GitHubJsonSerializerContext.Default.GitHubPullRequestDtoArray,
            scope.RequestAuthorization,
            cancellationToken);

    private async Task<GitHubPullRequestDto?> GetPullRequestDtoOrNullAsync(
        RepositoryName repositoryName,
        int number,
        GitHubCacheScope scope,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SendGitHubRequestAsync(
                $"repos/{repositoryName.Owner}/{repositoryName.Name}/pulls/{number}",
                GitHubJsonSerializerContext.Default.GitHubPullRequestDto,
                scope.RequestAuthorization,
                cancellationToken);
        }
        catch (GitHubApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static void AddLinkedOpenPullRequestsByFocusIssue(
        RepositoryName repositoryName,
        IReadOnlyList<GitHubLinkedPullRequestsIssueNodeDto?> issueNodes,
        IDictionary<int, List<LinkedOpenPullRequestSummary>> linkedOpenPullRequestsByIssue)
    {
        foreach (var issueNode in issueNodes)
        {
            if (issueNode is null
                || !string.Equals(issueNode.TypeName, "Issue", StringComparison.Ordinal)
                || issueNode.TimelineItems?.Nodes is not { Count: > 0 } nodes)
            {
                continue;
            }

            foreach (var node in nodes)
            {
                if (!TryCreateLinkedOpenPullRequestFromGraphQl(repositoryName, node.Source, out var pullRequest))
                {
                    continue;
                }

                AddLinkedOpenPullRequest(
                    linkedOpenPullRequestsByIssue,
                    issueNode.Number,
                    pullRequest.Number,
                    pullRequest.UpdatedAt);
            }
        }
    }

    private static bool TryCreateLinkedOpenPullRequestFromGraphQl(
        RepositoryName repositoryName,
        GitHubLinkedPullRequestsSourceDto? source,
        out LinkedOpenPullRequestSummary pullRequest)
    {
        pullRequest = default;
        if (source is null
            || !string.Equals(source.TypeName, "PullRequest", StringComparison.Ordinal)
            || !string.Equals(source.State, "OPEN", StringComparison.Ordinal)
            || source.IsDraft
            || source.UpdatedAt is not { } updatedAt)
        {
            return false;
        }

        if (!RepositoryMatches(source.Repository?.NameWithOwner ?? "", repositoryName))
        {
            return false;
        }

        pullRequest = new LinkedOpenPullRequestSummary(source.Number, updatedAt);
        return true;
    }

    private static IReadOnlyDictionary<int, IReadOnlyList<LinkedOpenPullRequestSummary>> CreateLinkedOpenPullRequestsByIssue(
        RepositoryName repositoryName,
        string milestoneTitle,
        IReadOnlyList<PullRequestSummary> pullRequests,
        IReadOnlySet<int> milestoneIssueNumbers)
    {
        var linkedOpenPullRequestsByIssue = new Dictionary<int, List<LinkedOpenPullRequestSummary>>();

        foreach (var pullRequest in pullRequests)
        {
            foreach (var issue in pullRequest.LinkedIssues)
            {
                if (!RepositoryMatches(issue.Repository, repositoryName)
                    || (!MilestoneTitleMatches(issue.Milestone, milestoneTitle)
                        && !milestoneIssueNumbers.Contains(issue.Number)))
                {
                    continue;
                }

                AddLinkedOpenPullRequest(
                    linkedOpenPullRequestsByIssue,
                    issue.Number,
                    pullRequest.Number,
                    pullRequest.UpdatedAt);
            }
        }

        return NormalizeLinkedOpenPullRequestsByIssue(linkedOpenPullRequestsByIssue);
    }

    private static void AddLinkedOpenPullRequest(
        IDictionary<int, List<LinkedOpenPullRequestSummary>> linkedOpenPullRequestsByIssue,
        int issueNumber,
        int pullRequestNumber,
        DateTimeOffset pullRequestUpdatedAt)
    {
        if (!linkedOpenPullRequestsByIssue.TryGetValue(issueNumber, out var linkedPullRequests))
        {
            linkedPullRequests = [];
            linkedOpenPullRequestsByIssue[issueNumber] = linkedPullRequests;
        }

        linkedPullRequests.Add(new LinkedOpenPullRequestSummary(pullRequestNumber, pullRequestUpdatedAt));
    }

    private static IReadOnlyDictionary<int, IReadOnlyList<LinkedOpenPullRequestSummary>> NormalizeLinkedOpenPullRequestsByIssue(
        IReadOnlyDictionary<int, List<LinkedOpenPullRequestSummary>> linkedOpenPullRequestsByIssue)
    {
        return linkedOpenPullRequestsByIssue.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<LinkedOpenPullRequestSummary>)pair.Value
                .GroupBy(pullRequest => pullRequest.Number)
                .Select(group => group.OrderByDescending(pullRequest => pullRequest.UpdatedAt).First())
                .OrderBy(pullRequest => pullRequest.Number)
                .ToArray());
    }

    private static bool RepositoryMatches(string repository, RepositoryName repositoryName) =>
        repository.Equals(repositoryName.ToString(), StringComparison.OrdinalIgnoreCase);

    private static bool RepositoryNamesEqual(RepositoryName left, RepositoryName right) =>
        left.Owner.Equals(right.Owner, StringComparison.OrdinalIgnoreCase)
        && left.Name.Equals(right.Name, StringComparison.OrdinalIgnoreCase);

    private static bool HasLabelContaining(IEnumerable<GitHubLabelDto> labels, string marker) =>
        labels.Any(label => label.Name?.Contains(marker, StringComparison.OrdinalIgnoreCase) is true);

    private static bool MilestoneTitleMatches(string? milestoneTitle, string expectedMilestoneTitle) =>
        string.Equals(milestoneTitle?.Trim(), expectedMilestoneTitle, StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<PullRequestChecksSummary>> GetPullRequestChecksAsync(
        RepositoryName repositoryName,
        IReadOnlyList<PullRequestChecksRequestItem> pullRequests,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var requestedPullRequests = pullRequests
            .Where(pullRequest => pullRequest.Number > 0 && !string.IsNullOrWhiteSpace(pullRequest.HeadSha))
            .GroupBy(
                pullRequest => (pullRequest.Number, HeadSha: pullRequest.HeadSha!.Trim()),
                pullRequest => pullRequest,
                EqualityComparer<(int Number, string HeadSha)>.Default)
            .Select(group => group.Key)
            .ToArray();

        if (requestedPullRequests.Length == 0)
        {
            return [];
        }

        var scopeSelection = await GetRepositoryCacheScopeSelectionAsync(repositoryName, forceRefresh, cancellationToken);
        var scope = scopeSelection.Scope;
        return await Task.WhenAll(requestedPullRequests.Select(
            pullRequest => GetPullRequestChecksWithThrottleAsync(
                repositoryName,
                pullRequest.Number,
                pullRequest.HeadSha,
                scopeSelection.Refresh,
                scope,
                scopeSelection.SharedFallbackScope,
                scopeSelection.CacheOnly,
                cancellationToken)));
    }

    private async Task<PullRequestChecksSummary> GetPullRequestChecksWithThrottleAsync(
        RepositoryName repositoryName,
        int number,
        string headSha,
        bool forceRefresh,
        GitHubCacheScope scope,
        GitHubCacheScope? sharedFallbackScope,
        bool cacheOnly,
        CancellationToken cancellationToken)
    {
        await s_checksFetchThrottle.WaitAsync(cancellationToken);
        try
        {
            return new PullRequestChecksSummary(
                number,
                headSha,
                await GetChecksStatusAsync(
                    repositoryName,
                    headSha,
                    forceRefresh,
                    scope,
                    CreateSharedFallbackCacheKey(sharedFallbackScope, repositoryName, "checks", headSha),
                    cacheOnly,
                    cancellationToken));
        }
        finally
        {
            s_checksFetchThrottle.Release();
        }
    }

    public async Task<ChecksStatus> GetChecksStatusAsync(
        RepositoryName repositoryName,
        string headSha,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var scopeSelection = await GetRepositoryCacheScopeSelectionAsync(
            repositoryName,
            forceRefresh,
            cancellationToken,
            allowSharedLiveFetch: true);
        return await GetChecksStatusAsync(
            repositoryName,
            headSha,
            scopeSelection.Refresh,
            scopeSelection.Scope,
            CreateSharedFallbackCacheKey(scopeSelection.SharedFallbackScope, repositoryName, "checks", headSha),
            scopeSelection.CacheOnly,
            cancellationToken);
    }

    private async Task<ChecksStatus> GetChecksStatusAsync(
        RepositoryName repositoryName,
        string headSha,
        bool forceRefresh,
        GitHubCacheScope scope,
        string? transientFallbackCacheKey,
        bool cacheOnly,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(headSha))
        {
            return ChecksStatus.None;
        }

        // Key by SHA so a fresh push naturally invalidates stale check state once
        // GitHub posts results for the new commit.
        var cacheKey = CreateRepositoryCacheKey(
            scope,
            repositoryName,
            "checks",
            headSha);
        var refreshCache = forceRefresh;
        var cachedStatus = await cache.GetAsync<ChecksStatus>(cacheKey, cancellationToken);
        var hasCachedStatus = cachedStatus is { Found: true, Value: not null };
        var hasLastGoodStatus = (await GetLastGoodAsync<ChecksStatus>(cacheKey, cancellationToken)).Found;
        var hasTransientFallbackStatus = (await GetCachedFallbackAsync<ChecksStatus>(
            transientFallbackCacheKey,
            cancellationToken)).Found;
        var hasTransientFallbackLastGoodStatus = transientFallbackCacheKey is not null
            && (await GetLastGoodAsync<ChecksStatus>(transientFallbackCacheKey, cancellationToken)).Found;
        var preserveCachedStatusOnTransientFailure = hasCachedStatus
            || hasLastGoodStatus
            || hasTransientFallbackStatus
            || hasTransientFallbackLastGoodStatus;
        var checksFetchComplete = true;
        return await GetOrCreateWithLastGoodFallbackAsync(
            cacheKey,
            CacheDurationForScope(scope),
            refreshCache,
            cacheOnly,
            async () =>
        {
            var checkRunsTask = TryGetCheckRunsAsync(repositoryName, headSha, scope, preserveCachedStatusOnTransientFailure, cancellationToken);
            var combinedStatusTask = TryGetCombinedStatusesAsync(repositoryName, headSha, scope, preserveCachedStatusOnTransientFailure, cancellationToken);
            await Task.WhenAll(checkRunsTask, combinedStatusTask);

            var checkRuns = await checkRunsTask;
            var combinedStatuses = await combinedStatusTask;
            checksFetchComplete = checkRuns.Complete && combinedStatuses.Complete;
            var rollup = MergeChecks(checkRuns.Items, combinedStatuses.Items);

            return rollup;
        },
            cancellationToken,
            storeLastGood: _ => checksFetchComplete,
            cacheDurationSelector: rollup => rollup.State switch
            {
                "pending" => PendingChecksCacheDuration,
                "failure" => FailingChecksCacheDuration,
                _ => checksFetchComplete ? CacheDurationForScope(scope) : CacheDuration,
            },
            transientFallbackCacheKey: transientFallbackCacheKey);
    }

    private static readonly TimeSpan PendingChecksCacheDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FailingChecksCacheDuration = TimeSpan.FromSeconds(20);
    private const int MaxFailingChecksTracked = 5;

    // Returns one check run per name — the most recent by started_at (tie-break completed_at, then
    // id) — mirroring how GitHub's PR check rollup ignores superseded runs from earlier suites.
    // Runs without a (non-whitespace) name are never collapsed together since name is the only
    // identity we can group on; in practice GitHub always names check runs.
    private static IReadOnlyList<GitHubCheckRunDto> DeduplicateLatestCheckRuns(IReadOnlyList<GitHubCheckRunDto> checkRuns)
    {
        if (checkRuns.Count <= 1)
        {
            return checkRuns;
        }

        var latestByName = new Dictionary<string, GitHubCheckRunDto>(StringComparer.Ordinal);
        var unnamedCount = 0;
        var hasDuplicates = false;
        foreach (var run in checkRuns)
        {
            if (string.IsNullOrWhiteSpace(run.Name))
            {
                unnamedCount++;
                continue;
            }

            if (latestByName.TryGetValue(run.Name, out var existing))
            {
                hasDuplicates = true;
                if (IsMoreRecentRun(run, existing))
                {
                    latestByName[run.Name] = run;
                }
            }
            else
            {
                latestByName[run.Name] = run;
            }
        }

        if (!hasDuplicates)
        {
            return checkRuns;
        }

        // Emit one entry per name in first-appearance order, plus any unnamed runs in place, so the
        // resulting rollup (and the capped failing-checks list) stays deterministic.
        var deduplicated = new List<GitHubCheckRunDto>(latestByName.Count + unnamedCount);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var run in checkRuns)
        {
            if (string.IsNullOrWhiteSpace(run.Name))
            {
                deduplicated.Add(run);
            }
            else if (emitted.Add(run.Name))
            {
                deduplicated.Add(latestByName[run.Name]);
            }
        }

        return deduplicated;
    }

    private static bool IsMoreRecentRun(GitHubCheckRunDto candidate, GitHubCheckRunDto existing)
    {
        var byStarted = Nullable.Compare(candidate.StartedAt, existing.StartedAt);
        if (byStarted != 0)
        {
            return byStarted > 0;
        }

        var byCompleted = Nullable.Compare(candidate.CompletedAt, existing.CompletedAt);
        if (byCompleted != 0)
        {
            return byCompleted > 0;
        }

        return candidate.Id > existing.Id;
    }

    private static GitHubGraphQlStatusCheckRollupDto? GetHeadCommitStatusCheckRollup(
        GitHubPullRequestGraphQlNodeDto pullRequest) =>
        pullRequest.Commits?.Nodes?
            .LastOrDefault(node => node?.Commit is not null)?
            .Commit?.StatusCheckRollup;

    // The PR-list GraphQL query carries only statusCheckRollup.state -- the single overall CI state
    // GitHub computes across every check context. Enumerating the rollup's contexts inline is far too
    // expensive at list scale: adding contexts(...) pushed the microsoft/aspire list query past
    // GitHub's 10s GraphQL execution timeout (measured ~7.6s without it, ~11s with it), failing the
    // whole repo. Even contexts{ totalCount } alone straddled the limit. The list only needs the
    // headline state to pick the CI colour/label, so we surface just that here -- effectively free --
    // and leave per-check counts and failing-check names to the detail/timeline path
    // (GetChecksStatusAsync), which pages the REST check-runs + combined-status for one PR on demand.
    private static ChecksStatus CreateChecksStatusFromGraphQl(GitHubGraphQlStatusCheckRollupDto? rollup)
    {
        var state = NormalizeRollupState(rollup?.State);
        return state is null
            ? ChecksStatus.None
            : ChecksStatus.None with { State = state };
    }

    // Maps GitHub's StatusState rollup enum onto the lowercase state vocabulary the rest of the app
    // uses. Returns null when there is no rollup (no CI configured) or an unrecognized value so the
    // caller falls back to ChecksStatus.None ("no checks"). EXPECTED is a status a commit is waiting
    // on, so it rolls up as pending the way GitHub's own rollup reports it.
    private static string? NormalizeRollupState(string? rollupState) =>
        rollupState?.ToUpperInvariant() switch
        {
            "SUCCESS" => "success",
            "FAILURE" or "ERROR" => "failure",
            "PENDING" or "EXPECTED" => "pending",
            _ => null,
        };

    private static ChecksStatus MergeChecks(IReadOnlyList<GitHubCheckRunDto> checkRuns, IReadOnlyList<GitHubStatusDto> statuses)
    {
        // A re-run or re-triggered workflow leaves the older check runs with the same name attached
        // to the same head SHA. GitHub's check-runs API returns every one of them (filter=latest only
        // dedupes within a single check suite, not across re-triggered suites), but GitHub's own PR
        // rollup considers only the most recent run per check name. Collapse to the latest run per
        // name first so a since-fixed re-run is not still counted as failing.
        checkRuns = DeduplicateLatestCheckRuns(checkRuns);

        var success = 0;
        var failure = 0;
        var pending = 0;
        var neutral = 0;
        var skipped = 0;
        DateTimeOffset? latestCompletedAt = null;
        // Cap the failing list while iterating so a matrix build with thousands of failing
        // contexts cannot cause unbounded allocation just to be trimmed at the end.
        var failingChecks = new List<FailingCheck>(MaxFailingChecksTracked);

        foreach (var run in checkRuns)
        {
            var status = run.Status?.ToLowerInvariant();
            var conclusion = run.Conclusion?.ToLowerInvariant();

            // Treat any not-yet-completed run as pending. status == "completed" is the only state
            // for which conclusion is guaranteed meaningful.
            if (status != "completed")
            {
                pending++;
                continue;
            }

            switch (conclusion)
            {
                case "success":
                    success++;
                    break;
                case "failure" or "timed_out" or "action_required" or "cancelled" or "startup_failure":
                    failure++;
                    if (failingChecks.Count < MaxFailingChecksTracked)
                    {
                        failingChecks.Add(new FailingCheck(
                            Name: run.Name ?? "(unnamed check)",
                            Conclusion: conclusion,
                            HtmlUrl: run.HtmlUrl));
                    }
                    break;
                case "neutral":
                    neutral++;
                    break;
                case "skipped" or "stale":
                    skipped++;
                    break;
                default:
                    // Unknown / null conclusion on a completed run — count as neutral so it
                    // does not drag the rollup down or up.
                    neutral++;
                    break;
            }

            if (run.CompletedAt is { } completedAt
                && (latestCompletedAt is null || completedAt > latestCompletedAt))
            {
                latestCompletedAt = completedAt;
            }
        }

        if (statuses.Count > 0)
        {
            foreach (var contextStatus in statuses)
            {
                var state = contextStatus.State?.ToLowerInvariant();
                switch (state)
                {
                    case "success":
                        success++;
                        break;
                    case "failure" or "error":
                        failure++;
                        if (failingChecks.Count < MaxFailingChecksTracked)
                        {
                            failingChecks.Add(new FailingCheck(
                                Name: contextStatus.Context ?? "(unnamed status)",
                                Conclusion: state,
                                HtmlUrl: contextStatus.TargetUrl));
                        }
                        break;
                    case "pending":
                        pending++;
                        break;
                    default:
                        neutral++;
                        break;
                }

                if (contextStatus.UpdatedAt is { } updatedAt
                    && (latestCompletedAt is null || updatedAt > latestCompletedAt))
                {
                    latestCompletedAt = updatedAt;
                }
            }
        }

        var total = success + failure + pending + neutral + skipped;

        // Mirrors GitHub's own check-suite conclusion: a suite with only neutral/skipped runs is
        // considered passing, so a PR with all-neutral CI should still qualify as "success" here.
        var rolledUpState =
            total == 0 ? "none" :
            failure > 0 ? "failure" :
            pending > 0 ? "pending" :
            success > 0 || neutral > 0 || skipped > 0 ? "success" :
            "none";

        return new ChecksStatus(
            State: rolledUpState,
            TotalCount: total,
            SuccessCount: success,
            FailureCount: failure,
            PendingCount: pending,
            NeutralCount: neutral,
            SkippedCount: skipped,
            CompletedAt: latestCompletedAt,
            FailingChecks: failingChecks);
    }

    private async Task<ChecksFetchResult<GitHubCheckRunDto>> TryGetCheckRunsAsync(
        RepositoryName repositoryName,
        string headSha,
        GitHubCacheScope scope,
        bool preserveTransientFailures,
        CancellationToken cancellationToken)
    {
        // The check-runs response is a wrapper object ({ total_count, check_runs[] }) rather than a
        // bare array, so we follow Link-header pagination manually instead of using
        // SendPagedGitHubRequestAsync. Without this, PRs with > 100 check runs (matrix builds in
        // monorepos) silently truncate and can produce a false "green" rollup.
        var runs = new List<GitHubCheckRunDto>();
        string? url = $"repos/{repositoryName.Owner}/{repositoryName.Name}/commits/{Uri.EscapeDataString(headSha)}/check-runs?filter=latest&per_page=100";

        try
        {
            while (url is not null)
            {
                var pageResponse = await SendGitHubPageAsync(
                    url,
                    GitHubJsonSerializerContext.Default.GitHubCheckRunsResponseDto,
                    scope.RequestAuthorization,
                    cancellationToken);
                var page = pageResponse.Value;

                if (page.CheckRuns is { Length: > 0 } pageRuns)
                {
                    runs.AddRange(pageRuns);
                }

                url = pageResponse.NextUrl;
            }
        }
        catch (Exception ex) when (preserveTransientFailures && IsTransientGitHubFailure(ex, cancellationToken))
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Checks are enrichment-only. Any failure (GitHub API errors like 404/403/5xx,
            // JsonException from unexpected payload shapes, socket errors, etc.) must degrade
            // gracefully to "no checks" instead of tearing down the entire PR list response.
            // Cancellation is intentionally re-thrown so the caller can honor it.
            return new ChecksFetchResult<GitHubCheckRunDto>([], Complete: false);
        }

        return new ChecksFetchResult<GitHubCheckRunDto>(runs, Complete: true);
    }

    private async Task<ChecksFetchResult<GitHubStatusDto>> TryGetCombinedStatusesAsync(
        RepositoryName repositoryName,
        string headSha,
        GitHubCacheScope scope,
        bool preserveTransientFailures,
        CancellationToken cancellationToken)
    {
        // Combined status defaults to per_page=30 and paginates via Link headers, identical in
        // shape to check-runs (wrapper object with a `statuses[]` array). Without paging, repos
        // that post many third-party statuses (Azure Pipelines, AppVeyor, Travis, etc.) silently
        // truncate and skew the rollup.
        var statuses = new List<GitHubStatusDto>();
        string? url = $"repos/{repositoryName.Owner}/{repositoryName.Name}/commits/{Uri.EscapeDataString(headSha)}/status?per_page=100";

        try
        {
            while (url is not null)
            {
                var pageResponse = await SendGitHubPageAsync(
                    url,
                    GitHubJsonSerializerContext.Default.GitHubCombinedStatusDto,
                    scope.RequestAuthorization,
                    cancellationToken);
                var page = pageResponse.Value;

                if (page.Statuses is { Length: > 0 } pageStatuses)
                {
                    statuses.AddRange(pageStatuses);
                }

                url = pageResponse.NextUrl;
            }
        }
        catch (Exception ex) when (preserveTransientFailures && IsTransientGitHubFailure(ex, cancellationToken))
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same enrichment-only stance as TryGetCheckRunsAsync — never let a single PR's
            // checks call break the whole list. Cancellation is intentionally re-thrown.
            return new ChecksFetchResult<GitHubStatusDto>([], Complete: false);
        }

        return new ChecksFetchResult<GitHubStatusDto>(statuses, Complete: true);
    }

    private readonly record struct ChecksFetchResult<T>(IReadOnlyList<T> Items, bool Complete);

    public async Task<ReviewStatus> GetReviewStatusAsync(
        RepositoryName repositoryName,
        int number,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var scopeSelection = await GetRepositoryCacheScopeSelectionAsync(repositoryName, forceRefresh, cancellationToken);
        return await GetReviewStatusAsync(
            repositoryName,
            number,
            scopeSelection.Refresh,
            scopeSelection.Scope,
            scopeSelection.CacheOnly,
            cancellationToken);
    }

    private async Task<ReviewStatus> GetReviewStatusAsync(
        RepositoryName repositoryName,
        int number,
        bool forceRefresh,
        GitHubCacheScope scope,
        bool cacheOnly,
        CancellationToken cancellationToken)
    {
        var cacheKey = CreateRepositoryCacheKey(
            scope,
            repositoryName,
            "reviews",
            number.ToString());
        var refreshCache = forceRefresh;
        return await GetOrRefreshCacheAsync(
            cacheKey,
            CacheDurationForScope(scope),
            refreshCache,
            cacheOnly,
            async () =>
        {
            GitHubReviewDto[] reviews;
            try
            {
                reviews = await SendGitHubRequestAsync(
                    $"repos/{repositoryName.Owner}/{repositoryName.Name}/pulls/{number}/reviews?per_page=100",
                    GitHubJsonSerializerContext.Default.GitHubReviewDtoArray,
                    scope.RequestAuthorization,
                    cancellationToken);
            }
            catch (GitHubApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Review status is enrichment-only. GitHub can return 404 when a PR becomes
                // unavailable between the list and enrichment calls, so keep the PR visible.
                return ReviewStatus.Waiting;
            }

            var humanReviews = reviews
                .Select(ReviewEvent.FromDto)
                .Where(review => !IsBotActor(review.Actor))
                .OrderBy(review => review.SubmittedAt)
                .ToArray();

            var latestByReviewer = humanReviews
                .GroupBy(review => review.Actor, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.MaxBy(review => review.SubmittedAt)!)
                .ToArray();

            // GitHub's review conclusion is based on each reviewer's latest review state, not the
            // newest review event globally. Example raw states: APPROVED, CHANGES_REQUESTED,
            // COMMENTED. A later COMMENTED review should not erase another reviewer's approval.
            var state =
                latestByReviewer.Any(review => review.State == "CHANGES_REQUESTED") ? "changes_requested" :
                latestByReviewer.Any(review => review.State == "APPROVED") ? "approved" :
                latestByReviewer.Any(review => review.State == "COMMENTED") ? "reviewed" :
                "waiting";

            // Unresolved review threads (count) need GraphQL (thread resolution is GraphQL-only) and
            // a token. They surface a unified "unresolved feedback" signal that pulls a PR out of the
            // Needs-attention queue — the author has feedback to address, so it is not reviewer-ready.
            // Fetch the count for any PR that has actually been reviewed (approved or commented), plus
            // waiting PRs the Copilot bot reviewed (its reviews are filtered out of the human review
            // state, so the PR still reads as "waiting").
            //
            // Skipped: plain awaiting-review PRs (no review yet, so no threads) and changes-requested
            // PRs (already author-blocked in the "Author response" lane). This keeps the extra GraphQL
            // calls off PRs where the count would add nothing.
            var copilotReviewed = reviews
                .Select(ReviewEvent.FromDto)
                .Any(review => IsCopilotReviewer(review.Actor));
            var shouldCountUnresolvedThreads =
                state == "approved"
                || state == "reviewed"
                || (state == "waiting" && copilotReviewed);
            var unresolvedThreadCount = shouldCountUnresolvedThreads
                ? await GetUnresolvedReviewThreadCountAsync(
                    repositoryName,
                    number,
                    scope.RequestAuthorization,
                    cancellationToken)
                : 0;

            return new ReviewStatus(
                State: state,
                LatestState: humanReviews.LastOrDefault()?.State,
                ReviewerCount: humanReviews.Select(review => review.Actor).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                ApprovalCount: humanReviews.Count(review => review.State == "APPROVED"),
                ChangesRequestedCount: humanReviews.Count(review => review.State == "CHANGES_REQUESTED"),
                CommentedReviewCount: humanReviews.Count(review => review.State == "COMMENTED"),
                LastApprovedAt: humanReviews.LastOrDefault(review => review.State == "APPROVED")?.SubmittedAt,
                LastReviewedAt: humanReviews.LastOrDefault()?.SubmittedAt,
                UnresolvedThreadCount: unresolvedThreadCount,
                // Only repos that require conversation resolution treat an unresolved thread as a
                // merge blocker; elsewhere the signal is informational and must not gate Ready to merge.
                RequiresConversationResolution: RequiresConversationResolution(repositoryName))
            {
                ApprovedReviewerIds = ApprovedReviewerIdsOf(latestByReviewer)
            };
        },
            cancellationToken) ?? ReviewStatus.Waiting;
    }

    private const string ReviewThreadsGraphQlQuery =
        "query($owner:String!,$name:String!,$number:Int!,$after:String){" +
        "repository(owner:$owner,name:$name){" +
        "pullRequest(number:$number){" +
        "reviewThreads(first:100,after:$after){pageInfo{hasNextPage endCursor}nodes{isResolved}}}}}";

    // Hard upper bound on review-thread pages (100 threads each) so this best-effort
    // enrichment stays bounded for pathological PRs. The loop normally stops earlier
    // once GitHub reports no further pages.
    private const int MaxReviewThreadPages = 20;

    private async Task<int> GetUnresolvedReviewThreadCountAsync(
        RepositoryName repositoryName,
        int number,
        GitHubRequestAuthorization authorization,
        CancellationToken cancellationToken)
    {
        var token = await GetGraphQlTokenAsync(authorization, cancellationToken);
        if (token is null)
        {
            return 0;
        }

        // Paginate so PRs with more than one page of review threads are counted
        // accurately. reviewThreads has no isResolved server-side filter and no
        // guaranteed ordering, so capping at the first 100 could undercount to zero
        // and wrongly mark an approved PR "Ready to merge".
        var unresolvedCount = 0;
        string? afterCursor = null;

        try
        {
            for (var page = 0; page < MaxReviewThreadPages; page++)
            {
                var requestBody = new GitHubGraphQlRequestDto
                {
                    Query = ReviewThreadsGraphQlQuery,
                    Variables = new GitHubReviewThreadsVariablesDto
                    {
                        Owner = repositoryName.Owner,
                        Name = repositoryName.Name,
                        Number = number,
                        After = afterCursor,
                    },
                };

                var threads = await SendReviewThreadsPageAsync(requestBody, token, cancellationToken);
                if (threads is null)
                {
                    // A mid-pagination page failed; return what we counted so far rather
                    // than 0, so a PR with already-seen unresolved threads stays blocked.
                    break;
                }

                if (threads.Nodes is not null)
                {
                    unresolvedCount += threads.Nodes.Count(node => !node.IsResolved);
                }

                // Stop unless there is a genuinely new page: a missing/empty cursor, or one
                // that does not advance, would otherwise re-count the same page and inflate
                // the total up to MaxReviewThreadPages times.
                var pageInfo = threads.PageInfo;
                if (pageInfo?.HasNextPage == true
                    && !string.IsNullOrEmpty(pageInfo.EndCursor)
                    && pageInfo.EndCursor != afterCursor)
                {
                    afterCursor = pageInfo.EndCursor;
                }
                else
                {
                    break;
                }
            }

            return unresolvedCount;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsBestEffortReviewThreadFailure(ex))
        {
            return unresolvedCount;
        }
    }

    private async Task<int> CountUnresolvedReviewThreadsFromGraphQlAsync(
        RepositoryName repositoryName,
        int number,
        GitHubReviewThreadsConnectionDto? initialThreads,
        string token,
        CancellationToken cancellationToken)
    {
        var unresolvedCount = initialThreads?.Nodes?.Count(node => !node.IsResolved) ?? 0;
        var afterCursor = initialThreads?.PageInfo?.EndCursor;
        if (initialThreads?.PageInfo?.HasNextPage != true || string.IsNullOrEmpty(afterCursor))
        {
            return unresolvedCount;
        }

        try
        {
            for (var page = 1; page < MaxReviewThreadPages; page++)
            {
                var requestBody = new GitHubGraphQlRequestDto
                {
                    Query = ReviewThreadsGraphQlQuery,
                    Variables = new GitHubReviewThreadsVariablesDto
                    {
                        Owner = repositoryName.Owner,
                        Name = repositoryName.Name,
                        Number = number,
                        After = afterCursor,
                    },
                };

                var threads = await SendReviewThreadsPageAsync(requestBody, token, cancellationToken);
                if (threads is null)
                {
                    break;
                }

                if (threads.Nodes is not null)
                {
                    unresolvedCount += threads.Nodes.Count(node => !node.IsResolved);
                }

                var pageInfo = threads.PageInfo;
                if (pageInfo?.HasNextPage == true
                    && !string.IsNullOrEmpty(pageInfo.EndCursor)
                    && pageInfo.EndCursor != afterCursor)
                {
                    afterCursor = pageInfo.EndCursor;
                }
                else
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsBestEffortReviewThreadFailure(ex))
        {
        }

        return unresolvedCount;
    }

    // Best-effort enrichment must never fail review status: tolerate transient transport
    // errors as well as a successful response whose body is malformed or not JSON
    // (e.g. a schema change or an HTML error page), while still surfacing programming bugs.
    private static bool IsBestEffortReviewThreadFailure(Exception exception) =>
        IsBestEffortGraphQlFailure(exception);

    private static bool IsBestEffortGraphQlFailure(Exception exception) =>
        IsTransientGitHubTransportFailure(exception)
            || exception is JsonException or NotSupportedException;

    private async Task<GitHubPullRequestsGraphQlConnectionDto?> SendPullRequestsGraphQlPageAsync(
        RepositoryName repositoryName,
        IReadOnlyList<string>? states,
        string orderField,
        string orderDirection,
        string? afterCursor,
        string token,
        CancellationToken cancellationToken)
    {
        await s_githubRequestThrottle.WaitAsync(cancellationToken);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "graphql")
            {
                Content = JsonContent.Create(
                    new GitHubPullRequestsGraphQlRequestDto
                    {
                        Query = PullRequestsGraphQlQuery,
                        Variables = new GitHubPullRequestsGraphQlVariablesDto
                        {
                            Owner = repositoryName.Owner,
                            Name = repositoryName.Name,
                            States = states,
                            OrderField = orderField,
                            OrderDirection = orderDirection,
                            After = afterCursor,
                        },
                    },
                    GitHubJsonSerializerContext.Default.GitHubPullRequestsGraphQlRequestDto),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var message = await ReadGitHubErrorMessageAsync(response, cancellationToken);
                throw new GitHubApiException(response.StatusCode, message);
            }

            var payload = await response.Content.ReadFromJsonAsync(
                GitHubJsonSerializerContext.Default.GitHubPullRequestsGraphQlResponseDto,
                cancellationToken);

            // GitHub GraphQL can return a partial `data` payload alongside field-level `errors`
            // when an individual sub-resolver fails (for example a single PR's statusCheckRollup
            // times out). Now that checks travel inline with the list, discarding the whole page on
            // any error would regress the per-field graceful degradation we had when checks were a
            // separate REST call: the failed field simply deserializes as null (-> "no checks" for
            // that PR) while every other PR still renders. Only treat errors as fatal when GitHub
            // returned no usable data at all (bad query, auth failure, repo not found, a top-level
            // rate-limit), which GraphQL signals with a null `data`/`repository`.
            if (payload?.Data?.Repository?.PullRequests is { } pullRequests)
            {
                return pullRequests;
            }

            if (payload?.Errors is { Count: > 0 } errors)
            {
                var message = errors
                    .Select(error => error.Message)
                    .FirstOrDefault(error => !string.IsNullOrWhiteSpace(error))
                    ?? "GitHub GraphQL returned an error.";
                throw new GitHubApiException(HttpStatusCode.BadGateway, message);
            }

            return payload?.Data?.Repository?.PullRequests;
        }
        finally
        {
            s_githubRequestThrottle.Release();
        }
    }

    private async Task<GitHubReviewThreadsConnectionDto?> SendReviewThreadsPageAsync(
        GitHubGraphQlRequestDto requestBody,
        string token,
        CancellationToken cancellationToken)
    {
        await s_githubRequestThrottle.WaitAsync(cancellationToken);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "graphql")
            {
                Content = JsonContent.Create(
                    requestBody,
                    GitHubJsonSerializerContext.Default.GitHubGraphQlRequestDto),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Unresolved-thread enrichment is best-effort; never fail review status on it.
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync(
                GitHubJsonSerializerContext.Default.GitHubReviewThreadsResponseDto,
                cancellationToken);

            return payload?.Data?.Repository?.PullRequest?.ReviewThreads;
        }
        finally
        {
            s_githubRequestThrottle.Release();
        }
    }

    public async Task<IReadOnlyList<TimelineItem>> GetPullRequestTimelineAsync(
        RepositoryName repositoryName,
        int number,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var scopeSelection = await GetRepositoryCacheScopeSelectionAsync(
            repositoryName,
            forceRefresh,
            cancellationToken,
            allowSharedLiveFetch: true);
        return await GetPullRequestTimelineAsync(
            repositoryName,
            number,
            scopeSelection.Refresh,
            scopeSelection.Scope,
            scopeSelection.CacheOnly,
            cancellationToken);
    }

    private async Task<IReadOnlyList<TimelineItem>> GetPullRequestTimelineAsync(
        RepositoryName repositoryName,
        int number,
        bool forceRefresh,
        GitHubCacheScope scope,
        bool cacheOnly,
        CancellationToken cancellationToken)
    {
        var cacheKey = CreateRepositoryCacheKey(
            scope,
            repositoryName,
            "timeline",
            number.ToString());
        var refreshCache = forceRefresh;
        return await GetOrRefreshCacheAsync<IReadOnlyList<TimelineItem>>(
            cacheKey,
            CacheDurationForScope(scope),
            refreshCache,
            cacheOnly,
            async () =>
        {
            // PRs are issues in the GitHub REST API timeline model, so this endpoint returns
            // the mixed event stream behind the GitHub.com PR timeline UI.
            // https://docs.github.com/en/rest/issues/timeline
            var url = $"repos/{repositoryName.Owner}/{repositoryName.Name}/issues/{number}/timeline?per_page=100";
            var items = new List<GitHubTimelineItemDto>();

            for (var page = 0; page < 3 && url is not null; page++)
            {
                var pageResponse = await SendGitHubPageAsync(
                    url,
                    GitHubJsonSerializerContext.Default.GitHubTimelineItemDtoArray,
                    scope.RequestAuthorization,
                    cancellationToken);

                items.AddRange(pageResponse.Value);
                url = pageResponse.NextUrl;
            }

            // "committed" timeline events only carry the raw git author name, so resolve each
            // commit's GitHub login from the commits API to keep one person from appearing twice.
            var commitAuthorLogins = await GetCommitAuthorLoginsByShaAsync(
                repositoryName,
                number,
                scope,
                cancellationToken);

            return items
                .Select(item => TimelineItem.FromDto(item, commitAuthorLogins))
                .OrderBy(item => item.OccurredAt)
                .ToArray();
        },
            cancellationToken) ?? [];
    }

    public async Task<PullRequestDetails> GetPullRequestDetailsAsync(
        RepositoryName repositoryName,
        int number,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var scopeSelection = await GetRepositoryCacheScopeSelectionAsync(
            repositoryName,
            forceRefresh,
            cancellationToken,
            allowSharedLiveFetch: true);
        return await GetPullRequestDetailsAsync(
            repositoryName,
            number,
            scopeSelection.Refresh,
            scopeSelection.Scope,
            CreateSharedFallbackCacheKey(scopeSelection.SharedFallbackScope, repositoryName, "pull", number.ToString()),
            scopeSelection.CacheOnly,
            cancellationToken);
    }

    private async Task<PullRequestDetails> GetPullRequestDetailsAsync(
        RepositoryName repositoryName,
        int number,
        bool forceRefresh,
        GitHubCacheScope scope,
        string? transientFallbackCacheKey,
        bool cacheOnly,
        CancellationToken cancellationToken)
    {
        var cacheKey = CreateRepositoryCacheKey(
            scope,
            repositoryName,
            "pull",
            number.ToString());
        var refreshCache = forceRefresh;
        return await GetOrRefreshCacheAsync(
            cacheKey,
            CacheDurationForScope(scope),
            refreshCache,
            cacheOnly,
            async () =>
        {
            var pullRequest = await SendGitHubRequestAsync(
                $"repos/{repositoryName.Owner}/{repositoryName.Name}/pulls/{number}",
                GitHubJsonSerializerContext.Default.GitHubPullRequestDto,
                scope.RequestAuthorization,
                cancellationToken);

            return PullRequestDetails.FromDto(pullRequest);
        },
            cancellationToken,
            transientFallbackCacheKey: transientFallbackCacheKey) ?? throw new GitHubApiException(HttpStatusCode.NotFound, $"Pull request #{number} was not found.");
    }

    private async Task<PullRequestDetails?> GetPullRequestDetailsOrNullAsync(
        RepositoryName repositoryName,
        int number,
        bool forceRefresh,
        GitHubCacheScope scope,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetPullRequestDetailsAsync(
                repositoryName,
                number,
                forceRefresh,
                scope,
                transientFallbackCacheKey: null,
                cacheOnly: false,
                cancellationToken);
        }
        catch (GitHubApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Detail metrics are enrichment-only. Keep the PR visible if it disappears mid-refresh.
            return null;
        }
    }

    private async Task<DateTimeOffset?> GetLastCommitAtAsync(
        RepositoryName repositoryName,
        int number,
        bool forceRefresh,
        GitHubCacheScope scope,
        CancellationToken cancellationToken)
    {
        var cacheKey = CreateRepositoryCacheKey(
            scope,
            repositoryName,
            "commits:last",
            number.ToString());
        var refreshCache = forceRefresh;
        return await GetOrRefreshCacheAsync<DateTimeOffset?>(
            cacheKey,
            CacheDurationForScope(scope),
            refreshCache,
            cacheOnly: false,
            async () =>
        {
            var url = $"repos/{repositoryName.Owner}/{repositoryName.Name}/pulls/{number}/commits?per_page=100";
            var commits = new List<GitHubPullRequestCommitDto>();

            for (var page = 0; page < 3 && url is not null; page++)
            {
                GitHubPullRequestCommitDto[] pageCommits;

                try
                {
                    var pageResponse = await SendGitHubPageAsync(
                        url,
                        GitHubJsonSerializerContext.Default.GitHubPullRequestCommitDtoArray,
                        scope.RequestAuthorization,
                        cancellationToken);
                    pageCommits = pageResponse.Value;
                    url = pageResponse.NextUrl;
                }
                catch (GitHubApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    // Commit recency is enrichment-only. Keep the PR in the list without it.
                    return null;
                }
                commits.AddRange(pageCommits);
            }

            return commits
                .Select(commit => commit.Commit?.Committer?.Date ?? commit.Commit?.Author?.Date)
                .Where(date => date is not null)
                .OrderByDescending(date => date)
                .FirstOrDefault();
        },
            cancellationToken);
    }

    // Builds a SHA -> GitHub login map from the PR commits API so the timeline can attribute
    // "committed" events to a GitHub user instead of the raw git author name. Best-effort: a
    // failure (including 404) stops enrichment and returns whatever was collected so far — empty if
    // the first page failed — so unmapped commits fall back to git names.
    private async Task<IReadOnlyDictionary<string, string>> GetCommitAuthorLoginsByShaAsync(
        RepositoryName repositoryName,
        int number,
        GitHubCacheScope scope,
        CancellationToken cancellationToken)
    {
        var loginsBySha = new Dictionary<string, string>(StringComparer.Ordinal);
        var url = $"repos/{repositoryName.Owner}/{repositoryName.Name}/pulls/{number}/commits?per_page=100";

        for (var page = 0; page < 3 && url is not null; page++)
        {
            GitHubPullRequestCommitDto[] pageCommits;

            try
            {
                var pageResponse = await SendGitHubPageAsync(
                    url,
                    GitHubJsonSerializerContext.Default.GitHubPullRequestCommitDtoArray,
                    scope.RequestAuthorization,
                    cancellationToken);
                pageCommits = pageResponse.Value;
                url = pageResponse.NextUrl;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                break;
            }

            foreach (var commit in pageCommits)
            {
                if (commit.Sha is { Length: > 0 } sha
                    && commit.Author?.Login is { Length: > 0 } login)
                {
                    loginsBySha[sha] = login;
                }
            }
        }

        return loginsBySha;
    }

    private async Task<IReadOnlyList<LinkedIssueSummary>> GetLinkedIssuesAsync(
        RepositoryName repositoryName,
        string? body,
        bool forceRefresh,
        GitHubCacheScope scope,
        CancellationToken cancellationToken)
    {
        var references = FindLinkedIssueReferences(repositoryName, body);
        if (references.Count == 0)
        {
            return [];
        }

        var issueTasks = references
            .Select(reference => GetLinkedIssueAsync(repositoryName, reference, forceRefresh, scope, cancellationToken))
            .ToArray();

        await Task.WhenAll(issueTasks);

        return issueTasks
            .Select(task => task.Result)
            .Where(issue => issue is not null)
            .Select(issue => issue!)
            .ToArray();
    }

    private async Task<LinkedIssueSummary?> GetLinkedIssueAsync(
        RepositoryName repositoryName,
        IssueReference reference,
        bool forceRefresh,
        GitHubCacheScope scope,
        CancellationToken cancellationToken)
    {
        var issueScope = await GetLinkedIssueCacheScopeAsync(
            repositoryName,
            reference.RepositoryName,
            scope,
            cancellationToken);
        return issueScope is { } resolvedScope
            ? await GetIssueAsync(reference.RepositoryName, reference.Number, forceRefresh, resolvedScope, cancellationToken)
            : null;
    }

    private async Task<GitHubCacheScope?> GetLinkedIssueCacheScopeAsync(
        RepositoryName sourceRepositoryName,
        RepositoryName linkedRepositoryName,
        GitHubCacheScope sourceScope,
        CancellationToken cancellationToken)
    {
        if (RepositoryNamesEqual(sourceRepositoryName, linkedRepositoryName))
        {
            return sourceScope;
        }

        if (sourceScope.IsShared)
        {
            return cacheScopeResolver.IsPublicCacheAllowlisted(linkedRepositoryName)
                ? GitHubCachePolicy.CreatePublicRepositoryScope()
                : null;
        }

        return await GetRepositoryCacheScopeAsync(linkedRepositoryName, cancellationToken);
    }

    private async Task<LinkedIssueSummary?> GetIssueAsync(
        RepositoryName repositoryName,
        int number,
        bool forceRefresh,
        GitHubCacheScope scope,
        CancellationToken cancellationToken)
    {
        var cacheKey = CreateRepositoryCacheKey(
            scope,
            repositoryName,
            "issue",
            number.ToString());
        var refreshCache = forceRefresh;
        return await GetOrRefreshCacheAsync(
            cacheKey,
            CacheDurationForScope(scope),
            refreshCache,
            cacheOnly: false,
            async () =>
        {
            GitHubIssueDto issue;
            try
            {
                issue = await SendGitHubRequestAsync(
                    $"repos/{repositoryName.Owner}/{repositoryName.Name}/issues/{number}",
                    GitHubJsonSerializerContext.Default.GitHubIssueDto,
                    scope.RequestAuthorization,
                    cancellationToken);
            }
            catch (GitHubApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Linked issues are enrichment-only. GitHub returns 404 for deleted, private, or
                // accidentally parsed same-repo references, so skip them instead of failing the list.
                return null;
            }
            catch (Exception ex) when (IsTransientGitHubFailure(ex, cancellationToken))
            {
                return null;
            }

            // Redirected issue lookups can start from old repo names like dotnet/aspire but return
            // canonical repository metadata. Surface the canonical org/repo when GitHub provides it.
            var issueRepositoryName = TryParseGitHubRepositoryApiUrl(issue.RepositoryUrl, out var parsedRepositoryName)
                ? parsedRepositoryName
                : repositoryName;

            return issue.PullRequest is null
                ? LinkedIssueSummary.FromDto(issueRepositoryName, issue)
                : null;
        },
            cancellationToken);
    }

    private async Task<T> SendGitHubRequestAsync<T>(
        string url,
        JsonTypeInfo<T> jsonTypeInfo,
        GitHubRequestAuthorization authorization,
        CancellationToken cancellationToken)
    {
        var page = await SendGitHubPageAsync(url, jsonTypeInfo, authorization, cancellationToken);
        return page.Value;
    }

    private async Task<IReadOnlyList<T>> SendPagedGitHubRequestAsync<T>(
        string url,
        JsonTypeInfo<T[]> jsonTypeInfo,
        GitHubRequestAuthorization authorization,
        CancellationToken cancellationToken)
    {
        var items = new List<T>();
        string? nextUrl = url;
        while (nextUrl is not null)
        {
            var page = await SendGitHubPageAsync(nextUrl, jsonTypeInfo, authorization, cancellationToken);

            items.AddRange(page.Value);
            nextUrl = page.NextUrl;
        }

        return items;
    }

    private async Task<IReadOnlyList<GitHubIssueDto>> SendPagedGitHubIssueSearchRequestAsync(
        string url,
        GitHubRequestAuthorization authorization,
        CancellationToken cancellationToken)
    {
        var items = new List<GitHubIssueDto>();
        string? nextUrl = url;
        while (nextUrl is not null)
        {
            var page = await SendGitHubPageAsync(
                nextUrl,
                GitHubJsonSerializerContext.Default.GitHubIssueSearchResponseDto,
                authorization,
                cancellationToken);

            items.AddRange(page.Value.Items);
            nextUrl = page.NextUrl;
        }

        return items;
    }

    private async IAsyncEnumerable<T> SendPagedGitHubRequestStreamAsync<T>(
        string url,
        JsonTypeInfo<T[]> jsonTypeInfo,
        GitHubRequestAuthorization authorization,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? nextUrl = url;

        while (nextUrl is not null)
        {
            var page = await SendGitHubPageAsync(nextUrl, jsonTypeInfo, authorization, cancellationToken);

            foreach (var item in page.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }

            nextUrl = page.NextUrl;
        }
    }

    private async Task<GitHubPage<T>> SendGitHubPageAsync<T>(
        string url,
        JsonTypeInfo<T> jsonTypeInfo,
        GitHubRequestAuthorization authorization,
        CancellationToken cancellationToken)
    {
        await s_githubRequestThrottle.WaitAsync(cancellationToken);
        try
        {
            using var response = await SendGitHubRequestAsync(url, authorization, cancellationToken);
            var value = await ReadGitHubJsonAsync(response, jsonTypeInfo, cancellationToken);
            return new GitHubPage<T>(value, GetNextPageUrl(response));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsTransientGitHubTransportFailure(ex))
        {
            throw new GitHubApiException(
                HttpStatusCode.ServiceUnavailable,
                "GitHub API is temporarily unavailable from the local backend. Try again shortly.");
        }
        finally
        {
            s_githubRequestThrottle.Release();
        }
    }

    private async Task<HttpResponseMessage> SendGitHubRequestAsync(
        string url,
        GitHubRequestAuthorization authorization,
        CancellationToken cancellationToken)
    {
        TokenResult? token = null;
        if (authorization == GitHubRequestAuthorization.Token)
        {
            token = await tokenProvider.GetTokenAsync(cancellationToken);
            if (token is null)
            {
                throw new GitHubApiException(
                    HttpStatusCode.Unauthorized,
                    environment.IsDevelopment()
                        ? "GitHub authentication is required. Set GITHUB_TOKEN or GH_TOKEN, run `gh auth login`, or sign in with GitHub."
                        : "GitHub authentication is required. Sign in with GitHub.");
            }
        }
        else if (authorization == GitHubRequestAuthorization.PublicCacheToken)
        {
            token = publicCacheIdentity.GetToken();
            if (token is null)
            {
                throw new GitHubApiException(
                    HttpStatusCode.ServiceUnavailable,
                    "GitHub public cache refresh is not configured with a server token.");
            }
        }

        // Follow GitHub API redirects ourselves so every redirected token request keeps the bearer token.
        for (var redirectCount = 0; redirectCount <= GitHubHttpRedirects.MaxRedirects; redirectCount++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
            }

            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!GitHubHttpRedirects.TryGetRedirectUrl(response, out var redirectUrl))
            {
                return response;
            }

            response.Dispose();
            url = redirectUrl;
        }

        throw new GitHubApiException(HttpStatusCode.BadGateway, "GitHub API returned too many redirects.");
    }

    private readonly record struct GitHubPage<T>(T Value, string? NextUrl);

    private static async Task<T> ReadGitHubJsonAsync<T>(
        HttpResponseMessage response,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var message = await ReadGitHubErrorMessageAsync(response, cancellationToken);
            throw new GitHubApiException(response.StatusCode, message);
        }

        return await response.Content.ReadFromJsonAsync(jsonTypeInfo, cancellationToken)
            ?? throw new GitHubApiException(response.StatusCode, "GitHub API returned an empty response.");
    }

    private static async Task<string> ReadGitHubErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var error = await response.Content.ReadFromJsonAsync(
            GitHubJsonSerializerContext.Default.GitHubErrorDto,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(error?.Message))
        {
            return $"GitHub API returned {(int)response.StatusCode}: {error.Message}";
        }

        return $"GitHub API returned {(int)response.StatusCode}.";
    }

    private static string? GetNextPageUrl(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Link", out var values) is false)
        {
            return null;
        }

        foreach (var value in values)
        {
            // GitHub Link header example:
            // <https://api.github.com/repositories/1/issues/2/timeline?page=2>; rel="next",
            // <https://api.github.com/repositories/1/issues/2/timeline?page=4>; rel="last"
            // https://docs.github.com/en/rest/using-the-rest-api/using-pagination-in-the-rest-api
            foreach (Match match in LinkHeaderRegex().Matches(value))
            {
                if (match.Groups["rel"].Value.Equals("next", StringComparison.OrdinalIgnoreCase))
                {
                    var absoluteUrl = match.Groups["url"].Value;
                    return absoluteUrl.StartsWith("https://api.github.com/", StringComparison.OrdinalIgnoreCase)
                        ? absoluteUrl["https://api.github.com/".Length..]
                        : null;
                }
            }
        }

        return null;
    }

    private static bool TryParseGitHubRepositoryApiUrl(string? repositoryUrl, out RepositoryName repositoryName)
    {
        repositoryName = default;
        if (string.IsNullOrWhiteSpace(repositoryUrl)
            || !Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = uri.AbsolutePath.Trim('/');
        const string reposPrefix = "repos/";
        if (!path.StartsWith(reposPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var repositoryPath = path[reposPrefix.Length..];
        return RepositoryName.TryParse(repositoryPath, out repositoryName);
    }

    private static IReadOnlyList<IssueReference> FindLinkedIssueReferences(RepositoryName repositoryName, string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        var references = new Dictionary<string, IssueReference>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in GitHubIssueUrlRegex().Matches(body))
        {
            AddIssueReference(references, repositoryName, match);
        }

        foreach (Match match in IssueReferenceRegex().Matches(body))
        {
            AddIssueReference(references, repositoryName, match);
        }

        return references.Values
            .Take(MaxLinkedIssuesPerPullRequest)
            .ToArray();
    }

    private static void AddIssueReference(
        IDictionary<string, IssueReference> references,
        RepositoryName defaultRepositoryName,
        Match match)
    {
        if (!int.TryParse(match.Groups["number"].Value, out var number) || number <= 0)
        {
            return;
        }

        var repositoryName = defaultRepositoryName;
        if (match.Groups["owner"] is { Success: true } owner
            && match.Groups["repo"] is { Success: true } repo
            && !string.IsNullOrWhiteSpace(owner.Value)
            && !string.IsNullOrWhiteSpace(repo.Value)
            && RepositoryName.TryParse($"{owner.Value}/{repo.Value}", out var parsedRepositoryName))
        {
            repositoryName = parsedRepositoryName;
        }

        references[$"{repositoryName}#{number}"] = new IssueReference(repositoryName, number);
    }

    [GeneratedRegex("<(?<url>[^>]+)>;\\s*rel=\"(?<rel>[^\"]+)\"")]
    private static partial Regex LinkHeaderRegex();

    [GeneratedRegex("https://github\\.com/(?<owner>[A-Za-z0-9._-]+)/(?<repo>[A-Za-z0-9._-]+)/issues/(?<number>[1-9][0-9]*)", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubIssueUrlRegex();

    [GeneratedRegex("(?<![A-Za-z0-9._/-])(?:(?<owner>[A-Za-z0-9._-]+)/(?<repo>[A-Za-z0-9._-]+))?#(?<number>[1-9][0-9]*)\\b")]
    private static partial Regex IssueReferenceRegex();

    private readonly record struct IssueReference(RepositoryName RepositoryName, int Number);

    private static bool IsBotActor(string actor) =>
        actor.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase)
        || s_knownBotActors.Contains(actor);

    private static bool IsCopilotReviewer(string actor) =>
        actor.Equals("copilot-pull-request-reviewer", StringComparison.OrdinalIgnoreCase)
        || actor.Equals("copilot-pull-request-reviewer[bot]", StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> s_knownBotActors = new(StringComparer.OrdinalIgnoreCase)
    {
        "Copilot",
        "dependabot",
        "dependabot-preview",
        "github-actions",
        "renovate"
    };
}
