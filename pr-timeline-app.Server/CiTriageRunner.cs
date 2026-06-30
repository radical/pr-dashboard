using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

// Per-failure triage of the red-at-tip lanes. Shells out to the Copilot CLI (CiTriageRunner -> CopilotCli)
// with a lighter model to answer "is this real / does it need action, and why". Cost-gated: only lanes
// whose latest run id changed since the last verdict are sent to the model; unchanged ones reuse the
// stored verdict for free. Keeps a per-lane history so it can tell the model "here's the prior verdict"
// and compute how many consecutive builds have failed the same way (feeds the issue body + notifications).
sealed class CiTriageRunner(
    CopilotCli copilot,
    CiHealthSnapshotStore store,
    IOptions<CiHealthOptions> options,
    TimeProvider timeProvider,
    ILogger<CiTriageRunner> logger)
{
    private static string LaneKey(string repository, string lane) => $"{repository}\n{lane}";

    // Triages the failing set, persists the snapshot + history, and returns the snapshot. Reuses prior
    // verdicts for lanes whose run id is unchanged; only NEW failing runs hit the model (capped per run).
    public async Task<CiTriageSnapshot> RunAsync(
        IReadOnlyList<FailingWorkflow> failing,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (failing.Count == 0)
        {
            var empty = new CiTriageSnapshot([], now, null);
            await store.WriteTriageAsync(empty, cancellationToken);
            return empty;
        }

        var config = options.Value.Triage;
        var prior = await store.ReadTriageAsync(cancellationToken);
        // Only reuse verdicts that carry the current schema (non-empty Lane). This invalidates any
        // verdict written before a shape change so the gate re-triages it rather than serving a stale row.
        var currentVerdicts = (prior?.Items ?? []).Where(item => !string.IsNullOrEmpty(item.Lane)).ToList();
        var priorByRun = currentVerdicts
            .ToDictionary(item => (item.Repository, item.Workflow, item.RunId));
        var priorByLane = currentVerdicts
            .GroupBy(item => LaneKey(item.Repository, item.Lane))
            .ToDictionary(group => group.Key, group => group.OrderByDescending(i => i.TriagedAt).First());

        // Cost gate: reuse the verdict when the failing run id is unchanged; only triage new runs.
        var reused = new List<CiTriageItem>();
        var toTriage = new List<FailingWorkflow>();
        foreach (var lane in failing)
        {
            if (priorByRun.TryGetValue((lane.Repository, lane.Workflow, lane.LastRunId), out var verdict))
            {
                reused.Add(verdict);
            }
            else
            {
                toTriage.Add(lane);
            }
        }

        if (toTriage.Count == 0)
        {
            var unchanged = new CiTriageSnapshot(reused, now, null);
            await store.WriteTriageAsync(unchanged, cancellationToken);
            logger.LogInformation("CI triage: no new failing runs; reused {Count} verdict(s).", reused.Count);
            return unchanged;
        }

        var batch = toTriage.Take(Math.Max(1, config.MaxLanes)).ToList();
        try
        {
            var result = await copilot.RunAsync(
                outputPath => BuildPrompt(batch, priorByLane, outputPath),
                cancellationToken);

            if (result.TimedOut)
            {
                return await PersistAsync(reused, now, $"Triage timed out after {config.TimeoutSeconds}s.", cancellationToken);
            }

            if (result.RawJson is null)
            {
                return await PersistAsync(reused, now, $"Triage produced no result (exit {result.ExitCode}).", cancellationToken);
            }

            var payload = ParseTriagePayload(result.RawJson);
            if (payload is null)
            {
                return await PersistAsync(reused, now, "Triage output was not valid JSON.", cancellationToken);
            }

            var history = await store.ReadTriageHistoryAsync(cancellationToken)
                ?? new CiTriageHistory(new(), now);
            var fresh = BuildItems(batch, payload, priorByLane, config.Model, now);
            AppendHistory(history, fresh, config.HistoryPerLane);

            var merged = reused.Concat(fresh).ToList();
            var snapshot = new CiTriageSnapshot(merged, now, null);
            await store.WriteTriageAsync(snapshot, cancellationToken);
            await store.WriteTriageHistoryAsync(history with { UpdatedAt = now }, cancellationToken);
            logger.LogInformation("CI triage written: {New} new, {Reused} reused.", fresh.Count, reused.Count);
            return snapshot;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "CI triage run failed.");
            return await PersistAsync(reused, now, ex.Message, cancellationToken);
        }
    }

    private async Task<CiTriageSnapshot> PersistAsync(
        IReadOnlyList<CiTriageItem> items, DateTimeOffset now, string? error, CancellationToken cancellationToken)
    {
        var snapshot = new CiTriageSnapshot(items, now, error);
        await store.WriteTriageAsync(snapshot, cancellationToken);
        return snapshot;
    }

    // Pure parse of the model's JSON object (optionally ```json-fenced) into the payload DTO.
    internal static CiTriagePayload? ParseTriagePayload(string raw)
    {
        var json = CopilotCli.ExtractJsonObject(raw);
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, CiHealthJsonContext.Default.CiTriagePayload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Joins each model verdict to its failing lane, stamps identity + model + timestamp, and computes the
    // recurring-build count: when the model says it's the same root cause as the prior verdict, extend the
    // prior lane's count; otherwise reset to 1. Pure + static so it's unit-testable.
    internal static IReadOnlyList<CiTriageItem> BuildItems(
        IReadOnlyList<FailingWorkflow> failing,
        CiTriagePayload payload,
        IReadOnlyDictionary<string, CiTriageItem> priorByLane,
        string model,
        DateTimeOffset now)
    {
        var verdicts = payload.Items.ToDictionary(item => (item.Repository, item.Workflow, item.RunId));
        var items = new List<CiTriageItem>();

        foreach (var lane in failing)
        {
            if (!verdicts.TryGetValue((lane.Repository, lane.Workflow, lane.LastRunId), out var v))
            {
                continue;
            }

            priorByLane.TryGetValue(LaneKey(lane.Repository, lane.Lane), out var priorLane);
            var recurring = v.SameRootCauseAsPrevious && priorLane is not null
                ? priorLane.RecurringBuilds + 1
                : 1;

            items.Add(new CiTriageItem(
                lane.Repository,
                lane.Workflow,
                lane.Lane,
                lane.LastRunId,
                lane.LastRunUrl,
                lane.FailingSince,
                lane.Streak,
                v.NeedsAction,
                v.Category,
                v.Confidence,
                v.Summary,
                v.SuggestedAction,
                v.SameRootCauseAsPrevious,
                recurring,
                now,
                model));
        }

        return items;
    }

    private static void AppendHistory(CiTriageHistory history, IReadOnlyList<CiTriageItem> items, int perLane)
    {
        foreach (var item in items)
        {
            var key = LaneKey(item.Repository, item.Lane);
            if (!history.ByLane.TryGetValue(key, out var entries))
            {
                entries = [];
                history.ByLane[key] = entries;
            }

            // Replace any existing entry for this run id (idempotent), then keep newest-first, bounded.
            entries.RemoveAll(e => e.RunId == item.RunId);
            entries.Insert(0, new CiTriageHistoryEntry(
                item.RunId, item.NeedsAction, item.Category, item.Summary, item.RecurringBuilds, item.TriagedAt));
            if (entries.Count > perLane)
            {
                entries.RemoveRange(perLane, entries.Count - perLane);
            }
        }
    }

    private string BuildPrompt(
        IReadOnlyList<FailingWorkflow> failing,
        IReadOnlyDictionary<string, CiTriageItem> priorByLane,
        string outputPath)
    {
        var lanes = new StringBuilder();
        var index = 1;
        foreach (var f in failing)
        {
            lanes.AppendLine(
                $"{index}. repository={f.Repository} workflow=\"{f.Workflow}\" runId={f.LastRunId} " +
                $"runUrl={f.LastRunUrl} lane=\"{f.Lane}\" streak={f.Streak} failingSince={f.FailingSince:o}");
            if (priorByLane.TryGetValue(LaneKey(f.Repository, f.Lane), out var prior))
            {
                lanes.AppendLine(
                    $"   previousVerdict: category={prior.Category} needsAction={prior.NeedsAction} " +
                    $"recurringBuilds={prior.RecurringBuilds} summary=\"{prior.Summary}\"");
            }
            index++;
        }

        return $$"""
You are a CI triage assistant for a PR/CI dashboard. You are given workflows that are RED at tip (the
most recent run on a tracked branch failed). For EACH item, investigate the failing run with the `gh`
CLI (e.g. `gh run view <runId> --repo <repo> --log-failed`), decide whether it needs human/agent action,
and classify it.

Keep it cheap: look at the failed job/step names and the tail of the failed logs; do not clone or build.
A dependabot/automated dependency bump or a transient infra error (registry/network blip, runner
shortage) usually does NOT need action; a real product/test regression or a broken required check does.

Some items include a `previousVerdict` from the last time this lane was triaged. Compare the current
failure to it: set sameRootCauseAsPrevious=true ONLY if the current failure has the same root cause
(e.g. the same network/registry error, the same failing test). Otherwise set it false.

When done, write ONE JSON object (and nothing else) to the file:
{{outputPath}}

using your file tools. Use exactly this schema (camelCase keys); repository, workflow, and runId MUST
match the input exactly:

{
  "items": [
    {
      "repository": "owner/repo",
      "workflow": "string",
      "runId": 123,
      "needsAction": true,
      "category": "real-failure | flaky | infra | external-dependency | noise",
      "confidence": "high | medium | low",
      "summary": "one sentence: what failed and why",
      "suggestedAction": "one sentence: what to do, or 'none'",
      "sameRootCauseAsPrevious": false
    }
  ]
}

Failing workflows to triage:

{{lanes}}
""";
    }
}
