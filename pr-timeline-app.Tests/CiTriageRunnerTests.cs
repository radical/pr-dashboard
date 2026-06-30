using Xunit;

namespace pr_timeline_app.Tests;

public sealed class CiTriageRunnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 30, 5, 30, 0, TimeSpan.Zero);

    private static FailingWorkflow Failing(
        long runId, string workflow = "CI", string lane = "GH CI — main", int streak = 1,
        string repo = "microsoft/aspire") =>
        new(repo, workflow, lane, "main", Now.AddHours(-3), streak, runId,
            $"https://github.com/{repo}/actions/runs/{runId}", LikelyReal: streak >= 3, LinkedIssue: null);

    private const string SampleJson = """
    {
      "items": [
        {
          "repository": "microsoft/aspire",
          "workflow": "CI",
          "runId": 100,
          "needsAction": false,
          "category": "infra",
          "confidence": "high",
          "summary": "Testcontainers could not pull the Redis image (ACR connection refused).",
          "suggestedAction": "Re-run the failed job.",
          "sameRootCauseAsPrevious": false
        }
      ]
    }
    """;

    [Fact]
    public void ParsesPayloadFromPlainJson()
    {
        var payload = CiTriageRunner.ParseTriagePayload(SampleJson);

        Assert.NotNull(payload);
        var item = Assert.Single(payload!.Items);
        Assert.Equal("CI", item.Workflow);
        Assert.Equal(100, item.RunId);
        Assert.False(item.NeedsAction);
        Assert.Equal("infra", item.Category);
    }

    [Fact]
    public void ParsesPayloadFromMarkdownFence()
    {
        var payload = CiTriageRunner.ParseTriagePayload("```json\n" + SampleJson + "\n```");
        Assert.NotNull(payload);
        Assert.Single(payload!.Items);
    }

    [Fact]
    public void ParsePayloadReturnsNullOnGarbage()
    {
        Assert.Null(CiTriageRunner.ParseTriagePayload("could not triage"));
        Assert.Null(CiTriageRunner.ParseTriagePayload("{ \"items\": [ {"));
    }

    [Fact]
    public void BuildItems_JoinsVerdictToLaneAndStamps()
    {
        var payload = CiTriageRunner.ParseTriagePayload(SampleJson)!;
        var failing = new[] { Failing(100) };

        var items = CiTriageRunner.BuildItems(failing, payload, new Dictionary<string, CiTriageItem>(), "gpt-5-mini", Now);

        var item = Assert.Single(items);
        Assert.Equal("GH CI — main", item.Lane);
        Assert.Equal(100, item.RunId);
        Assert.Equal("gpt-5-mini", item.Model);
        Assert.Equal(Now, item.TriagedAt);
        Assert.Equal(1, item.RecurringBuilds); // first time seen
    }

    [Fact]
    public void BuildItems_DropsVerdictsThatDoNotMatchAFailingRun()
    {
        var payload = CiTriageRunner.ParseTriagePayload(SampleJson)!;
        // Failing run id differs from the verdict's runId (100) -> no join.
        var items = CiTriageRunner.BuildItems([Failing(999)], payload, new Dictionary<string, CiTriageItem>(), "auto", Now);
        Assert.Empty(items);
    }

    [Fact]
    public void BuildItems_ExtendsRecurrenceWhenSameRootCause()
    {
        const string sameCauseJson = """
        { "items": [ { "repository": "microsoft/aspire", "workflow": "CI", "runId": 200,
          "needsAction": true, "category": "infra", "confidence": "high",
          "summary": "Same ACR connection refused.", "suggestedAction": "none",
          "sameRootCauseAsPrevious": true } ] }
        """;
        var payload = CiTriageRunner.ParseTriagePayload(sameCauseJson)!;
        var priorByLane = new Dictionary<string, CiTriageItem>
        {
            ["microsoft/aspire\nGH CI — main"] = new(
                "microsoft/aspire", "CI", "GH CI — main", 100, "url", Now.AddHours(-2), 1,
                true, "infra", "high", "ACR connection refused.", "none",
                SameRootCauseAsPrevious: false, RecurringBuilds: 3, TriagedAt: Now.AddHours(-1), Model: "auto"),
        };

        var items = CiTriageRunner.BuildItems([Failing(200)], payload, priorByLane, "auto", Now);

        Assert.Equal(4, Assert.Single(items).RecurringBuilds); // 3 -> 4
    }

    [Fact]
    public void BuildItems_ResetsRecurrenceWhenDifferentRootCause()
    {
        const string newCauseJson = """
        { "items": [ { "repository": "microsoft/aspire", "workflow": "CI", "runId": 200,
          "needsAction": true, "category": "real-failure", "confidence": "high",
          "summary": "A unit test regressed.", "suggestedAction": "Fix the test.",
          "sameRootCauseAsPrevious": false } ] }
        """;
        var payload = CiTriageRunner.ParseTriagePayload(newCauseJson)!;
        var priorByLane = new Dictionary<string, CiTriageItem>
        {
            ["microsoft/aspire\nGH CI — main"] = new(
                "microsoft/aspire", "CI", "GH CI — main", 100, "url", Now.AddHours(-2), 1,
                true, "infra", "high", "ACR connection refused.", "none",
                SameRootCauseAsPrevious: false, RecurringBuilds: 5, TriagedAt: Now.AddHours(-1), Model: "auto"),
        };

        var items = CiTriageRunner.BuildItems([Failing(200)], payload, priorByLane, "auto", Now);

        Assert.Equal(1, Assert.Single(items).RecurringBuilds); // reset
    }

    [Fact]
    public void AppendHistory_PrunesByAgeAndCount_AndKeepsRunUrl()
    {
        var history = new CiTriageHistory(new(), Now);
        const string lane = "GH CI — main";
        var key = $"microsoft/aspire\n{lane}";

        // Seed an old entry (20 days ago) that should be pruned by the 14-day window.
        history.ByLane[key] = [new CiTriageHistoryEntry(1, "u1", true, "infra", "old", 1, Now.AddDays(-20))];

        var item = new CiTriageItem(
            "microsoft/aspire", "CI", lane, 2, "https://run/2", Now.AddHours(-1), 1,
            true, "infra", "high", "fresh", "none", SameRootCauseAsPrevious: false, RecurringBuilds: 1,
            TriagedAt: Now, Model: "auto");

        CiTriageRunner.AppendHistory(history, [item], perLane: 30, retentionDays: 14, now: Now);

        var entries = history.ByLane[key];
        var kept = Assert.Single(entries);            // the 20-day-old entry was pruned
        Assert.Equal(2, kept.RunId);
        Assert.Equal("https://run/2", kept.RunUrl);   // run link retained for future lookback
    }
}
