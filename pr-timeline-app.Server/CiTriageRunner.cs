using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

// Produces a CI failure triage by shelling out to the Copilot CLI ("for now", before the SDK). Builds a
// prompt from the currently-failing lanes, runs `copilot -p ... --output-format json`, and reads the JSON
// the model is instructed to write into a temp dir we grant it access to via --add-dir. The parsing is a
// pure static method so it can be unit-tested against real captured CLI output without spawning a process.
sealed class CiTriageRunner(
    CiHealthSnapshotStore store,
    IOptions<CiHealthOptions> options,
    ILogger<CiTriageRunner> logger)
{
    private const string OutputFileName = "triage-output.json";

    // Runs triage over the failing set, persists the snapshot, and returns it. A configurable cap bounds
    // how many lanes are investigated so the request/credit budget stays predictable.
    public async Task<CiTriageSnapshot> RunAsync(
        IReadOnlyList<FailingWorkflow> failing,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var config = options.Value.Triage;

        if (failing.Count == 0)
        {
            var empty = new CiTriageSnapshot([], now, null);
            await store.WriteTriageAsync(empty, cancellationToken);
            return empty;
        }

        var subset = failing.Take(Math.Max(1, config.MaxLanes)).ToList();
        var workDir = Directory.CreateTempSubdirectory("ci-triage");
        try
        {
            var outputPath = Path.Combine(workDir.FullName, OutputFileName);
            var prompt = BuildPrompt(subset, outputPath);

            var (exitCode, stdout, stderr, timedOut) = await RunCopilotAsync(config, workDir.FullName, prompt, cancellationToken);

            string? raw = File.Exists(outputPath) ? await File.ReadAllTextAsync(outputPath, cancellationToken) : null;

            CiTriageSnapshot snapshot;
            if (timedOut)
            {
                snapshot = new CiTriageSnapshot([], now, $"Triage timed out after {config.TimeoutSeconds}s.");
            }
            else if (raw is null)
            {
                logger.LogWarning("CI triage produced no output file (exit {ExitCode}). stderr: {Stderr}", exitCode, Truncate(stderr));
                snapshot = new CiTriageSnapshot([], now, $"Triage produced no result (exit {exitCode}).");
            }
            else
            {
                snapshot = ParseTriageOutput(raw, now);
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
        finally
        {
            try { workDir.Delete(recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    // Parses the model's JSON object (optionally wrapped in a ```json fence) into a snapshot. Pure +
    // static so tests can drive it with real captured CLI output. Tolerant of surrounding prose by
    // extracting the outermost { ... } before deserializing.
    internal static CiTriageSnapshot ParseTriageOutput(string raw, DateTimeOffset now)
    {
        var json = ExtractJsonObject(raw);
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

    private static string? ExtractJsonObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : null;
    }

    private async Task<(int ExitCode, string Stdout, string Stderr, bool TimedOut)> RunCopilotAsync(
        CiTriageOptions config,
        string workDir,
        string prompt,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = config.Command,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // ArgumentList avoids shell quoting/injection entirely — the prompt is passed verbatim.
        startInfo.ArgumentList.Add("--allow-all-tools");
        startInfo.ArgumentList.Add("--add-dir");
        startInfo.ArgumentList.Add(workDir);
        startInfo.ArgumentList.Add("--output-format");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(prompt);

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            return (process.ExitCode, stdout.ToString(), stderr.ToString(), false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return (-1, stdout.ToString(), stderr.ToString(), true);
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

    private static string Truncate(string value) =>
        value.Length <= 500 ? value : value[..500] + "…";
}
