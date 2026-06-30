// Maps a GitHub Actions run to a lane and a section using the repo's lane config:
//   - "main"      : a configured main workflow (e.g. CI on a tracked push branch -> "CI · main",
//                   "CI · release/13.4"; or a curated scheduled run like "Outerloop Tests")
//   - "scheduled" : any other schedule-triggered workflow not on the skip list (e.g. "Deployment E2E Tests")
//   - null        : everything else (PRs, feature-branch pushes, dispatch, skipped scheduled)
// Push lanes carry a "· {branch}" suffix to distinguish branches; schedule-triggered lanes carry no
// suffix (the section already conveys the trigger). Pure; the producer resolves the effective tracked
// branches (config or repo default) first.
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
        // Empty MainWorkflows => any workflow on a tracked branch is a main lane.
        var isMain = mainWorkflows.Count == 0 || Contains(mainWorkflows, clean) || Contains(mainWorkflows, workflow);

        if (isMain)
        {
            if (string.Equals(@event, "push", StringComparison.OrdinalIgnoreCase))
            {
                return branches.Any(pattern => MatchesBranch(headBranch, pattern))
                    ? new LaneAssignment($"{clean} \u00b7 {headBranch}", MainSection)
                    : null;
            }

            if (string.Equals(@event, "schedule", StringComparison.OrdinalIgnoreCase))
            {
                // Schedule-triggered main lane (e.g. the curated Outerloop run). No trigger suffix: the
                // Main section already conveys it, and "X · scheduled" under Main CI reads as misplaced.
                return new LaneAssignment(clean, MainSection);
            }

            return null;
        }

        if (string.Equals(@event, "schedule", StringComparison.OrdinalIgnoreCase)
            && !Contains(skipScheduled, clean) && !Contains(skipScheduled, workflow))
        {
            // No "· scheduled" suffix — every lane in the Scheduled section is schedule-triggered, so it
            // would be redundant.
            return new LaneAssignment(clean, ScheduledSection);
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

    // Reduce a raw workflow name to a clean display name. GitHub only uses the file PATH as the name
    // when a workflow has no `name:` field, and such a path always ends in .yml/.yaml — so only then do
    // we strip the directory + extension (+ a trailing .lock). A real `name:` is left untouched, even
    // when it legitimately contains '/' (e.g. "Agentic Maintenance (microsoft/aspire.dev)").
    public static string CleanName(string workflow)
    {
        var isPath = workflow.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
            || workflow.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase);
        if (!isPath)
        {
            return workflow;
        }

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
