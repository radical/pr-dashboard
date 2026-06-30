// LLM triage of the currently-failing workflows. "For now" this is produced by shelling out to the
// Copilot CLI (CiTriageRunner); a later iteration moves to the Copilot SDK. The shape is the contract
// the dashboard renders and the model is asked to emit, so it stays stable across that swap.
//
//   NeedsAction : true when a human should look (real failure / evicted cache), false for transient
//                 infra blips, external dependency bumps, or noise.
//   Category    : "real-failure" | "flaky" | "infra" | "external-dependency" | "noise"
//   Confidence  : "high" | "medium" | "low"
record CiTriageItem(
    string Repository,
    string Workflow,
    long RunId,
    string RunUrl,
    bool NeedsAction,
    string Category,
    string Confidence,
    string Summary,
    string SuggestedAction);

// One triage pass over the failing set. Error is non-null when the run could not produce items (CLI
// missing, timed out, or unparseable output) so the page can show why instead of silently empty.
record CiTriageSnapshot(
    IReadOnlyList<CiTriageItem> Items,
    DateTimeOffset UpdatedAt,
    string? Error);

// Parse target for the JSON object the model emits: { "items": [ ...CiTriageItem... ] }.
record CiTriagePayload(IReadOnlyList<CiTriageItem> Items);
