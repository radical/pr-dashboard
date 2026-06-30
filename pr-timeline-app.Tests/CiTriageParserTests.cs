using Xunit;

namespace pr_timeline_app.Tests;

public sealed class CiTriageParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 30, 5, 30, 0, TimeSpan.Zero);

    // Real shape emitted by the Copilot CLI during validation (microsoft/aspire failing runs).
    private const string SampleJson = """
    {
      "items": [
        {
          "repository": "microsoft/aspire",
          "workflow": "CI",
          "runId": 28415842004,
          "runUrl": "https://github.com/microsoft/aspire/actions/runs/28415842004",
          "needsAction": false,
          "category": "infra",
          "confidence": "high",
          "summary": "Testcontainers could not pull the Redis image (ACR connection refused).",
          "suggestedAction": "Re-run the failed StackExchange.Redis job."
        },
        {
          "repository": "microsoft/aspire",
          "workflow": "Labeler: Cache Retention",
          "runId": 28407511335,
          "runUrl": "https://github.com/microsoft/aspire/actions/runs/28407511335",
          "needsAction": true,
          "category": "infra",
          "confidence": "high",
          "summary": "The pull-request labeler model cache was evicted.",
          "suggestedAction": "Re-run the issue-labeler model training workflow."
        }
      ]
    }
    """;

    [Fact]
    public void ParsesPlainJsonObject()
    {
        var snapshot = CiTriageRunner.ParseTriageOutput(SampleJson, Now);

        Assert.Null(snapshot.Error);
        Assert.Equal(Now, snapshot.UpdatedAt);
        Assert.Equal(2, snapshot.Items.Count);

        var ci = snapshot.Items[0];
        Assert.Equal("microsoft/aspire", ci.Repository);
        Assert.Equal("CI", ci.Workflow);
        Assert.Equal(28415842004, ci.RunId);
        Assert.False(ci.NeedsAction);
        Assert.Equal("infra", ci.Category);
        Assert.Equal("high", ci.Confidence);

        var labeler = snapshot.Items[1];
        Assert.True(labeler.NeedsAction);
        Assert.Equal("Labeler: Cache Retention", labeler.Workflow);
    }

    [Fact]
    public void ExtractsJsonFromMarkdownFence()
    {
        var fenced = "```json\n" + SampleJson + "\n```";

        var snapshot = CiTriageRunner.ParseTriageOutput(fenced, Now);

        Assert.Null(snapshot.Error);
        Assert.Equal(2, snapshot.Items.Count);
    }

    [Fact]
    public void ExtractsJsonWhenSurroundedByProse()
    {
        var noisy = "Here is the triage result:\n\n" + SampleJson + "\n\nLet me know if you need more detail.";

        var snapshot = CiTriageRunner.ParseTriageOutput(noisy, Now);

        Assert.Null(snapshot.Error);
        Assert.Equal(2, snapshot.Items.Count);
    }

    [Fact]
    public void ReturnsErrorWhenNoJsonObjectPresent()
    {
        var snapshot = CiTriageRunner.ParseTriageOutput("I could not complete the triage.", Now);

        Assert.NotNull(snapshot.Error);
        Assert.Empty(snapshot.Items);
    }

    [Fact]
    public void ReturnsErrorOnMalformedJson()
    {
        var snapshot = CiTriageRunner.ParseTriageOutput("{ \"items\": [ { \"repository\": ", Now);

        Assert.NotNull(snapshot.Error);
        Assert.Empty(snapshot.Items);
    }

    [Fact]
    public void EmptyItemsArrayParsesWithoutError()
    {
        var snapshot = CiTriageRunner.ParseTriageOutput("{ \"items\": [] }", Now);

        Assert.Null(snapshot.Error);
        Assert.Empty(snapshot.Items);
    }
}
