// Copilot-produced "shepherd" judgment over the bot/automation PRs and issues — the layer the
// deterministic state buckets can't provide: a prioritized work queue, per-PR why/action prose, and
// recurring-issue clustering. Mirrors microsoft/aspire#18285. Produced on demand (CopilotCli) and cached
// by an input Fingerprint so a re-run with nothing relevant changed is a no-op.

// One prioritized action. HumanOnly marks merge/servicing/infra decisions an agent must not take;
// unmarked items are a code change an agent could make (fix a required check, resolve a conflict).
record BotWorkQueueItem(
    string Repository,
    string Kind,        // "pr" | "issue"
    int Number,
    string HtmlUrl,
    string Title,
    bool HumanOnly,
    string Action);

// Per-PR enrichment keyed by repository+number so the frontend's deterministic buckets can show the
// model's "why it's here / what to do" prose. Bucket mirrors the deterministic bucketing the UI computes.
record BotPrNote(
    string Repository,
    int Number,
    string Bucket,      // "easy-win" | "stuck" | "broken" | "pending"
    string Why,
    string Action);

// A cluster of related auto-opened issues (e.g. the daily [Deployment E2E] dupes) with a consolidation
// recommendation. Severity drives the card tone.
record BotIssueGroup(
    string Theme,
    string Severity,    // "broken" | "attention" | "standing"
    string Summary,
    string Recommendation,
    IReadOnlyList<BotIssueRef> Issues);

record BotIssueRef(
    string Repository,
    int Number,
    string HtmlUrl,
    string Title);

// One shepherd pass. Fingerprint is the hash of the relevant bot state it was computed from; the
// endpoint compares it against the current state to decide whether to re-run. FromCache is set on the
// response when a cached snapshot was served unchanged (not persisted). Error is non-null when the run
// could not produce a result.
record BotShepherdSnapshot(
    IReadOnlyList<BotWorkQueueItem> WorkQueue,
    IReadOnlyList<BotPrNote> PrNotes,
    IReadOnlyList<BotIssueGroup> IssueGroups,
    string Fingerprint,
    DateTimeOffset UpdatedAt,
    string? Error,
    bool FromCache);

// Parse target for the JSON object the model emits (the snapshot's computed/runtime fields are added by
// the runner, not the model).
record BotShepherdPayload(
    IReadOnlyList<BotWorkQueueItem> WorkQueue,
    IReadOnlyList<BotPrNote> PrNotes,
    IReadOnlyList<BotIssueGroup> IssueGroups);
