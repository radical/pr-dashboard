// Classifies which open PRs are bot/automation-opened, per aspire-bot-shepherd.prompt.txt:
//   - normalize the author login (strip a leading "app/" prefix AND a trailing "[bot]" suffix)
//   - include if the normalized login is in the allowlist (the floor) OR the author is flagged is_bot
//   - ALWAYS drop the two Copilot apps (absolute, even if otherwise unioned in)
//   - drop PRs labeled NO-MERGE (never in scope) or "automated" (the orchestrator's own artifacts)
//   - union in any PR labeled "automation-broken", regardless of author
// Login forms vary by GitHub API surface: REST /pulls returns App bots with a trailing "[bot]"
// (e.g. "dependabot[bot]", "copilot-swe-agent[bot]") while GraphQL returns an "app/" prefix; the
// allowlist and Copilot list hold the bare names, so both affixes must be stripped before matching.
// NB (v1): PullRequestSummary does not expose is_bot, so callers pass AuthorIsBot=false; the
// allowlist is the floor and catches the User-account bots that report is_bot:false.
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

            var login = NormalizeLogin(pr.AuthorLogin);

            // Copilot apps are NEVER in scope here (they're handled by the dispatch/adopt path); this
            // exclusion is absolute and takes precedence over the automation-broken union below.
            if (CopilotApps.Contains(login, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var isBotAuthor = allow.Contains(login) || pr.AuthorIsBot;
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
                pr.Mergeable,
                pr.Review,
                pr.Labels));
        }

        return result;
    }

    private static string NormalizeLogin(string login)
    {
        if (login.StartsWith("app/", StringComparison.OrdinalIgnoreCase))
        {
            login = login[4..];
        }

        if (login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase))
        {
            login = login[..^"[bot]".Length];
        }

        return login;
    }

    // Filters open issues to those opened by a tracked bot. Mirrors the PR rules where they apply:
    // normalize the login, keep allowlisted (non-Copilot) authors, and drop the orchestrator's own
    // "automated"-stamped tracking issues.
    public static IReadOnlyList<BotIssue> ClassifyIssues(
        IReadOnlyList<BotIssue> issues,
        IReadOnlyCollection<string> allowlist)
    {
        var allow = new HashSet<string>(allowlist, StringComparer.OrdinalIgnoreCase);
        var result = new List<BotIssue>();

        foreach (var issue in issues)
        {
            var login = NormalizeLogin(issue.Author);
            if (CopilotApps.Contains(login, StringComparer.OrdinalIgnoreCase) || !allow.Contains(login))
            {
                continue;
            }

            if (issue.Labels.Any(label => string.Equals(label, "automated", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(issue with { Author = login });
        }

        return result;
    }
}
