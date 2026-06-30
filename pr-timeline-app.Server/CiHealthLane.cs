// Maps a GitHub Actions run to a lane and a section using the repo's lane config:
//   - "main"      : a main-CI workflow running on a tracked push branch (e.g. "CI · main",
//                   "CI · release/13.4")
//   - "scheduled" : a schedule-triggered workflow not on the skip list (e.g. "Outerloop Tests · scheduled")
//   - null        : everything else (PRs, feature-branch pushes, dispatch, skipped scheduled)
// Pure; the producer resolves the effective tracked branches (config or repo default) first.
static class WorkflowLane
{
    public const string MainSection = "main";
    public const string ScheduledSection = "scheduled";

    public static LaneAssignment? Resolve(
        string workflow,
        string @event,
        string headBranch,
        IReadOnlyCollection<string> branches,
        IReadOnlyCollection<string> mainWorkflows,
        IReadOnlyCollection<string> skipScheduled)
    {
        var clean = CleanName(workflow);

        if (string.Equals(@event, "push", StringComparison.OrdinalIgnoreCase))
        {
            if (branches.Any(pattern => MatchesBranch(headBranch, pattern))
                && (mainWorkflows.Count == 0 || Contains(mainWorkflows, clean) || Contains(mainWorkflows, workflow)))
            {
                return new LaneAssignment($"{clean} \u00b7 {headBranch}", MainSection);
            }

            return null;
        }

        if (string.Equals(@event, "schedule", StringComparison.OrdinalIgnoreCase))
        {
            if (Contains(skipScheduled, clean) || Contains(skipScheduled, workflow))
            {
                return null;
            }

            return new LaneAssignment($"{clean} \u00b7 scheduled", ScheduledSection);
        }

        return null;
    }

    private static bool Contains(IReadOnlyCollection<string> set, string value) =>
        set.Contains(value, StringComparer.OrdinalIgnoreCase);

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

record LaneAssignment(string Lane, string Section);
