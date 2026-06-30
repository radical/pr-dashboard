// Maps a GitHub Actions run to a "lane" using the repo's lane config. A lane is a rolling push build on
// a tracked branch (e.g. "CI · main", "CI · release/13.4") or the PR validation lane ("CI · PR").
// Scheduled / feature-branch / dispatch runs return null (dropped). Pure; the producer resolves the
// effective tracked branches (config or the repo default) before calling Resolve.
static class WorkflowLane
{
    // Returns the lane label for a run, or null when the run isn't part of a tracked lane.
    public static string? Resolve(
        string workflow,
        string @event,
        string headBranch,
        IReadOnlyCollection<string> branches,
        bool includePullRequests)
    {
        if (string.Equals(@event, "pull_request", StringComparison.OrdinalIgnoreCase))
        {
            return includePullRequests ? $"{CleanName(workflow)} \u00b7 PR" : null;
        }

        if (string.Equals(@event, "push", StringComparison.OrdinalIgnoreCase)
            && branches.Any(pattern => MatchesBranch(headBranch, pattern)))
        {
            return $"{CleanName(workflow)} \u00b7 {headBranch}";
        }

        return null;
    }

    // Matches a branch against a pattern: "*" matches anything, a trailing "*" is a prefix glob
    // ("release/*" matches "release/13.4"), otherwise an exact (case-insensitive) match.
    public static bool MatchesBranch(string branch, string pattern)
    {
        if (pattern == "*")
        {
            return true;
        }

        if (pattern.EndsWith("*", StringComparison.Ordinal))
        {
            return branch.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(branch, pattern, StringComparison.OrdinalIgnoreCase);
    }

    // Reduce a raw workflow name to a clean display name: drop any leading path, then a trailing
    // .yml/.yaml, then a trailing .lock (GitHub uses the file path as the name when a workflow has no
    // `name:` field, e.g. ".github/workflows/analyze-ci-failure.lock.yml" -> "analyze-ci-failure").
    public static string CleanName(string workflow)
    {
        var name = workflow;
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        if (name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^".yml".Length];
        }
        else if (name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^".yaml".Length];
        }

        if (name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^".lock".Length];
        }

        return name;
    }
}
