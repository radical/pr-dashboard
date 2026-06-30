// Maps a GitHub Actions run to a "lane" — (workflow x trigger) — so that, e.g., ci.yml on push-to-main
// and ci.yml on pull_request are tracked separately (mirroring the lanes in the real CI pulse). Pure;
// the default branch is passed in by the producer (one GET /repos/{owner}/{repo} per cycle).
enum LaneTrigger
{
    Main,         // push to the repository's default branch
    PullRequest,  // pull_request
    Scheduled,    // schedule
    Other,        // feature-branch pushes, workflow_dispatch, etc. — dropped (not a health signal)
}

static class WorkflowLane
{
    public static LaneTrigger Classify(string @event, string headBranch, string defaultBranch) =>
        @event switch
        {
            "pull_request" => LaneTrigger.PullRequest,
            "schedule" => LaneTrigger.Scheduled,
            "push" when string.Equals(headBranch, defaultBranch, StringComparison.OrdinalIgnoreCase)
                => LaneTrigger.Main,
            _ => LaneTrigger.Other,
        };

    public static string Label(LaneTrigger trigger) => trigger switch
    {
        LaneTrigger.Main => "main",
        LaneTrigger.PullRequest => "PR",
        LaneTrigger.Scheduled => "scheduled",
        _ => "other",
    };

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

    public static string LaneLabel(string workflow, LaneTrigger trigger) =>
        $"{CleanName(workflow)} · {Label(trigger)}";
}
