using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

// Produces a CI failure triage by shelling out to the Copilot CLI ("for now", before the SDK). Builds a
// prompt from the currently-failing lanes, runs the CLI via the shared CopilotCli helper, and parses the
// JSON the model writes back. The parsing is a pure static method so it can be unit-tested against real
// captured CLI output without spawning a process.
sealed class CiTriageRunner(
    CopilotCli copilot,
    CiHealthSnapshotStore store,
    IOptions<CiHealthOptions> options,
    ILogger<CiTriageRunner> logger)
{
    // Runs triage over the failing set, persists the snapshot, and returns it. A configurable cap bounds
    // how many lanes are investigated so the request/credit budget stays predictable.
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

        var subset = failing.Take(Math.Max(1, options.Value.Triage.MaxLanes)).ToList();
        try
        {
            var result = await copilot.RunAsync(outputPath => BuildPrompt(subset, outputPath), cancellationToken);

            CiTriageSnapshot snapshot;
            if (result.TimedOut)
            {
                snapshot = new CiTriageSnapshot([], now, $"Triage timed out after {options.Value.Triage.TimeoutSeconds}s.");
            }
            else if (result.RawJson is null)
            {
                snapshot = new CiTriageSnapshot([], now, $"Triage produced no result (exit {result.ExitCode}).");
            }
            else
            {
                snapshot = ParseTriageOutput(result.RawJson, now);
            }

            await store.WriteTriageAsync(snapshot, cancellationToken);
            logger.LogInformation("CI triage written: {Items} item(s){Error}.",
                snapshot.Items.Count, snapshot.Error is null ? "" : $", error: {snapshot.Error}");
            return snapshot;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "CI triage run failed.");
            var failed = new CiTriageSnapshot([], now, ex.Message);
            await store.WriteTriageAsync(failed, cancellationToken);
            return failed;
        }
    }

    // Parses the model's JSON object (optionally wrapped in a ```json fence) into a snapshot. Pure +
    // static so tests can drive it with real captured CLI output.
    internal static CiTriageSnapshot ParseTriageOutput(string raw, DateTimeOffset now)
    {
        var json = CopilotCli.ExtractJsonObject(raw);
        if (json is null)
        {
            return new CiTriageSnapshot([], now, "Triage output was not valid JSON.");
        }

        try
        {
            var payload = JsonSerializer.Deserialize(json, CiHealthJsonContext.Default.CiTriagePayload);
            return new CiTriageSnapshot(payload?.Items ?? [], now, null);
        }
        catch (JsonException)
        {
            return new CiTriageSnapshot([], now, "Triage output was not valid JSON.");
        }
    }

    private static string BuildPrompt(IReadOnlyList<FailingWorkflow> failing, string outputPath)
    {
        var lanes = new StringBuilder();
        var index = 1;
        foreach (var f in failing)
        {
            lanes.AppendLine(
                $"{index}. repository={f.Repository} workflow=\"{f.Workflow}\" runId={f.LastRunId} " +
                $"runUrl={f.LastRunUrl} lane=\"{f.Lane}\" streak={f.Streak} failingSince={f.FailingSince:o}");
            index++;
        }

        return $$"""
You are a CI triage assistant for a PR/CI dashboard. You are given a list of workflows that are
currently RED at tip (the most recent run on a tracked branch failed). For EACH item, investigate the
failing run using the `gh` CLI (e.g. `gh run view <runId> --repo <repo> --log-failed`, `gh run view
<runId> --repo <repo>`), determine whether it needs human action, and classify it.

Keep investigation cheap: look at the failed job/step names and the tail of the failed logs; do not
clone or build anything. If a run is a dependabot/automated dependency update or a transient infra
error (registry/network blip, runner shortage), set needsAction accordingly.

When done, write ONE JSON object (and nothing else) to the file:
{{outputPath}}

using your file tools. Use exactly this schema (camelCase keys):

{
  "items": [
    {
      "repository": "owner/repo",
      "workflow": "string",
      "runId": 123,
      "runUrl": "string",
      "needsAction": true,
      "category": "real-failure | flaky | infra | external-dependency | noise",
      "confidence": "high | medium | low",
      "summary": "one sentence: what failed and why",
      "suggestedAction": "one sentence: what a maintainer should do, or 'none'"
    }
  ]
}

Failing workflows to triage:

{{lanes}}
""";
    }
}
