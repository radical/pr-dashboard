using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

// Copilot-produced shepherd over the bot PRs + issues. Caches by an input fingerprint: if the relevant
// bot state hasn't changed since the last run, the cached snapshot is returned without invoking the CLI.
// The fingerprint + parsing are pure static methods so they're unit-testable without a process.
sealed class BotShepherdRunner(
    CopilotCli copilot,
    CiHealthSnapshotStore store,
    IOptions<CiHealthOptions> options,
    ILogger<BotShepherdRunner> logger)
{
    // Returns a shepherd snapshot for the given bot state. Reuses the cached snapshot when the input
    // fingerprint matches (nothing relevant changed) and the cached run did not error.
    public async Task<BotShepherdSnapshot> RunAsync(
        IReadOnlyList<BotPullRequest> botPrs,
        IReadOnlyList<BotIssue> botIssues,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var fingerprint = ComputeFingerprint(botPrs, botIssues);

        var cached = await store.ReadBotShepherdAsync(cancellationToken);
        if (cached is { Error: null } && cached.Fingerprint == fingerprint)
        {
            logger.LogInformation("Bot shepherd served from cache (fingerprint unchanged).");
            return cached with { FromCache = true };
        }

        if (botPrs.Count == 0 && botIssues.Count == 0)
        {
            var empty = new BotShepherdSnapshot([], [], [], fingerprint, now, null, FromCache: false);
            await store.WriteBotShepherdAsync(empty, cancellationToken);
            return empty;
        }

        try
        {
            var maxIssues = Math.Max(1, options.Value.Triage.MaxLanes) * 6;
            var result = await copilot.RunAsync(
                outputPath => BuildPrompt(botPrs, botIssues.Take(maxIssues).ToList(), now, outputPath),
                cancellationToken);

            BotShepherdSnapshot snapshot;
            if (result.TimedOut)
            {
                snapshot = Failed(fingerprint, now, $"Shepherd timed out after {options.Value.Triage.TimeoutSeconds}s.");
            }
            else if (result.RawJson is null)
            {
                snapshot = Failed(fingerprint, now, $"Shepherd produced no result (exit {result.ExitCode}).");
            }
            else
            {
                snapshot = ParseShepherdOutput(result.RawJson, fingerprint, now);
            }

            await store.WriteBotShepherdAsync(snapshot, cancellationToken);
            logger.LogInformation("Bot shepherd written: {Queue} queue, {Notes} notes, {Groups} issue groups{Error}.",
                snapshot.WorkQueue.Count, snapshot.PrNotes.Count, snapshot.IssueGroups.Count,
                snapshot.Error is null ? "" : $", error: {snapshot.Error}");
            return snapshot;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Bot shepherd run failed.");
            var failed = Failed(fingerprint, now, ex.Message);
            await store.WriteBotShepherdAsync(failed, cancellationToken);
            return failed;
        }
    }

    private static BotShepherdSnapshot Failed(string fingerprint, DateTimeOffset now, string error) =>
        new([], [], [], fingerprint, now, error, FromCache: false);

    // Hashes the relevant bot state: PR identity + merge-readiness fields, issue identity + title +
    // labels. Anything that would change the shepherd's verdict changes the hash; cosmetic churn does not.
    internal static string ComputeFingerprint(
        IReadOnlyList<BotPullRequest> botPrs,
        IReadOnlyList<BotIssue> botIssues)
    {
        var builder = new StringBuilder();
        foreach (var pr in botPrs
            .OrderBy(p => p.Repository, StringComparer.Ordinal)
            .ThenBy(p => p.Number))
        {
            builder.Append("pr|").Append(pr.Repository).Append('#').Append(pr.Number)
                .Append('|').Append(pr.CiStatus)
                .Append('|').Append(pr.Mergeable)
                .Append('|').Append(pr.Review)
                .Append('|').Append(pr.Title)
                .Append('|').Append(string.Join(',', pr.Labels.OrderBy(l => l, StringComparer.Ordinal)))
                .Append('\n');
        }

        foreach (var issue in botIssues
            .OrderBy(i => i.Repository, StringComparer.Ordinal)
            .ThenBy(i => i.Number))
        {
            builder.Append("issue|").Append(issue.Repository).Append('#').Append(issue.Number)
                .Append('|').Append(issue.Title)
                .Append('|').Append(string.Join(',', issue.Labels.OrderBy(l => l, StringComparer.Ordinal)))
                .Append('\n');
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash);
    }

    // Parses the model's JSON object into a snapshot, stamping the computed fingerprint + timestamp.
    // Pure + static for unit testing against captured CLI output.
    internal static BotShepherdSnapshot ParseShepherdOutput(string raw, string fingerprint, DateTimeOffset now)
    {
        var json = CopilotCli.ExtractJsonObject(raw);
        if (json is null)
        {
            return Failed(fingerprint, now, "Shepherd output was not valid JSON.");
        }

        try
        {
            var payload = JsonSerializer.Deserialize(json, CiHealthJsonContext.Default.BotShepherdPayload);
            // Guard the nested Issues array too: the model can emit an issueGroups entry without one,
            // which STJ leaves null on the non-nullable property and would crash the page on render.
            var issueGroups = (payload?.IssueGroups ?? [])
                .Select(group => group.Issues is null ? group with { Issues = [] } : group)
                .ToList();
            return new BotShepherdSnapshot(
                payload?.WorkQueue ?? [],
                payload?.PrNotes ?? [],
                issueGroups,
                fingerprint,
                now,
                null,
                FromCache: false);
        }
        catch (JsonException)
        {
            return Failed(fingerprint, now, "Shepherd output was not valid JSON.");
        }
    }

    private static string BuildPrompt(
        IReadOnlyList<BotPullRequest> botPrs,
        IReadOnlyList<BotIssue> botIssues,
        DateTimeOffset now,
        string outputPath)
    {
        var prs = new StringBuilder();
        foreach (var pr in botPrs)
        {
            prs.AppendLine(
                $"- {pr.Repository}#{pr.Number} \"{pr.Title}\" author={pr.Author} ci={pr.CiStatus} " +
                $"mergeable={pr.Mergeable} review={pr.Review} ageDays={AgeDays(pr.CreatedAt, now)} " +
                $"labels=[{string.Join(',', pr.Labels)}] url={pr.HtmlUrl}");
        }

        var issues = new StringBuilder();
        foreach (var issue in botIssues)
        {
            issues.AppendLine(
                $"- {issue.Repository}#{issue.Number} \"{issue.Title}\" author={issue.Author} " +
                $"ageDays={AgeDays(issue.CreatedAt, now)} labels=[{string.Join(',', issue.Labels)}] url={issue.HtmlUrl}");
        }

        return $$"""
You are a bot/automation PR shepherd for a CI dashboard, modeled on the microsoft/aspire bot-shepherd
triage. You are given the OPEN bot/automation PRs and auto-opened issues for several repos, with their
merge-readiness state and age. Drive each toward merged or closed. The inline data is usually enough;
you MAY use `gh` to look closer at a specific PR/issue, but keep it cheap and do not clone or build.

Produce three things:

1. A prioritized work queue: the few items that need action now, most important first. Mark
   humanOnly=true when the action is a merge, a servicing/release decision, or an infra step (e.g.
   mirroring dependencies to internal feeds) — things an agent must not do. Mark humanOnly=false when it
   is a code change an agent could make (fix a failing required check, resolve a simple conflict).
   Merge is ALWAYS human.

2. A per-PR note for every PR: which bucket it belongs to and a one-line why + action.
   bucket = "easy-win" (green + mergeable, just needs review/merge), "stuck" (conflicting, changes
   requested, or approved-but-blocked — needs a human decision), "broken" (failing checks), or
   "pending" (checks still running).

3. Issue groups: cluster the auto-opened issues by recurring theme (e.g. all "[Deployment E2E]" nightly
   dupes, or all "PR Documentation Check" infra failures). For each group give a severity
   ("broken" | "attention" | "standing"), a one-line summary, and a recommendation (e.g. "consolidate to
   the newest and close the rest"). Put genuinely standalone/long-lived issues in their own group with
   severity "standing".

When done, write ONE JSON object (and nothing else) to the file:
{{outputPath}}

using your file tools. Use exactly this schema (camelCase keys); repositories and numbers MUST match the
input exactly:

{
  "workQueue": [
    { "repository": "owner/repo", "kind": "pr|issue", "number": 1, "htmlUrl": "string",
      "title": "string", "humanOnly": true, "action": "one sentence" }
  ],
  "prNotes": [
    { "repository": "owner/repo", "number": 1, "bucket": "easy-win|stuck|broken|pending",
      "why": "one sentence", "action": "one sentence" }
  ],
  "issueGroups": [
    { "theme": "string", "severity": "broken|attention|standing", "summary": "one sentence",
      "recommendation": "one sentence",
      "issues": [ { "repository": "owner/repo", "number": 1, "htmlUrl": "string", "title": "string" } ] }
  ]
}

Open bot/automation PRs:
{{(prs.Length == 0 ? "(none)" : prs.ToString())}}

Open auto-opened issues:
{{(issues.Length == 0 ? "(none)" : issues.ToString())}}
""";
    }

    private static int AgeDays(DateTimeOffset created, DateTimeOffset now) =>
        Math.Max(0, (int)(now - created).TotalDays);
}
