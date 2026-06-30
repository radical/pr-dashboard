using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;

// Shared invocation path for the Copilot CLI ("for now", before the SDK). Both the CI failure triage
// and the bot shepherd build a prompt, run `copilot ... --output-format json -p ...`, and read back the
// JSON object the model is instructed to write into a temp dir we grant it via --add-dir. Centralizing
// it here means the eventual swap to the Copilot SDK touches one place, not every feature that uses it.
sealed class CopilotCli(IOptions<CiHealthOptions> options, ILogger<CopilotCli> logger)
{
    private const string OutputFileName = "output.json";

    // Runs the CLI for one prompt. The caller receives the temp output path (via buildPrompt) so it can
    // instruct the model to write its JSON there; the file content is returned verbatim for parsing.
    public async Task<CopilotCliResult> RunAsync(Func<string, string> buildPrompt, CancellationToken cancellationToken)
    {
        var config = options.Value.Triage;
        var workDir = Directory.CreateTempSubdirectory("copilot-cli");
        try
        {
            var outputPath = Path.Combine(workDir.FullName, OutputFileName);
            var prompt = buildPrompt(outputPath);
            var (exitCode, stderr, timedOut) = await RunProcessAsync(config, workDir.FullName, prompt, cancellationToken);
            string? raw = File.Exists(outputPath) ? await File.ReadAllTextAsync(outputPath, cancellationToken) : null;

            if (timedOut)
            {
                logger.LogWarning("Copilot CLI timed out after {Timeout}s.", config.TimeoutSeconds);
            }
            else if (raw is null)
            {
                logger.LogWarning("Copilot CLI produced no output file (exit {ExitCode}). stderr: {Stderr}", exitCode, Truncate(stderr));
            }

            return new CopilotCliResult(raw, timedOut, exitCode, stderr);
        }
        finally
        {
            try { workDir.Delete(recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    // Extracts the outermost { ... } JSON object from raw model output, tolerating a ```json fence or
    // surrounding prose. Pure + static so feature parsers (and their tests) can reuse it.
    public static string? ExtractJsonObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : null;
    }

    private async Task<(int ExitCode, string Stderr, bool TimedOut)> RunProcessAsync(
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
        if (!string.IsNullOrWhiteSpace(config.Model))
        {
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(config.Model);
        }
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(prompt);

        using var process = new Process { StartInfo = startInfo };
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            return (process.ExitCode, stderr.ToString(), false);
        }
        catch (OperationCanceledException)
        {
            // Either our timeout fired or the caller cancelled (client disconnect). Kill the tree in
            // both cases so we never orphan the long-running CLI (it can spawn gh/other tools). Rethrow
            // on genuine caller cancellation; report a timeout otherwise.
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch { /* already gone */ }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return (-1, stderr.ToString(), true);
        }
    }

    private static string Truncate(string value) =>
        value.Length <= 500 ? value : value[..500] + "…";
}

record CopilotCliResult(string? RawJson, bool TimedOut, int ExitCode, string Stderr);
