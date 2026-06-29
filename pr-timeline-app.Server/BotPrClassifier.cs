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
