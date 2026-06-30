using Xunit;

namespace pr_timeline_app.Tests;

public sealed class BotShepherdRunnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 30, 6, 0, 0, TimeSpan.Zero);

    private static BotPullRequest Pr(
        int number, string ci = "passing", string mergeable = "mergeable", string review = "review_required",
        string? title = null, string[]? labels = null, string repo = "microsoft/aspire") =>
        new(repo, number, title ?? $"PR {number}", "dependabot",
            $"https://github.com/{repo}/pull/{number}", ci, mergeable, review,
            labels ?? [], Now.AddDays(-3), Now.AddDays(-1));

    private static BotIssue Issue(int number, string? title = null, string[]? labels = null, string repo = "microsoft/aspire") =>
        new(repo, number, title ?? $"issue {number}", "github-actions",
            $"https://github.com/{repo}/issues/{number}", labels ?? [], Now.AddDays(-5), Now.AddDays(-2));

    [Fact]
    public void Fingerprint_IsStableForSameInput()
    {
        var prs = new[] { Pr(1), Pr(2) };
        var issues = new[] { Issue(10) };

        var a = BotShepherdRunner.ComputeFingerprint(prs, issues);
        var b = BotShepherdRunner.ComputeFingerprint(prs, issues);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Fingerprint_IsOrderIndependent()
    {
        var a = BotShepherdRunner.ComputeFingerprint([Pr(1), Pr(2)], [Issue(10), Issue(11)]);
        var b = BotShepherdRunner.ComputeFingerprint([Pr(2), Pr(1)], [Issue(11), Issue(10)]);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Fingerprint_ChangesWhenPrStateChanges()
    {
        var before = BotShepherdRunner.ComputeFingerprint([Pr(1, ci: "passing")], []);
        var after = BotShepherdRunner.ComputeFingerprint([Pr(1, ci: "failing")], []);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Fingerprint_ChangesWhenIssueSetChanges()
    {
        var before = BotShepherdRunner.ComputeFingerprint([], [Issue(10)]);
        var after = BotShepherdRunner.ComputeFingerprint([], [Issue(10), Issue(11)]);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Fingerprint_ChangesWhenIssueTitleChanges()
    {
        var before = BotShepherdRunner.ComputeFingerprint([], [Issue(10, title: "[Deployment E2E] failed")]);
        var after = BotShepherdRunner.ComputeFingerprint([], [Issue(10, title: "[Deployment E2E] failed again")]);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Fingerprint_IgnoresLabelOrder()
    {
        var a = BotShepherdRunner.ComputeFingerprint([Pr(1, labels: ["a", "b"])], []);
        var b = BotShepherdRunner.ComputeFingerprint([Pr(1, labels: ["b", "a"])], []);

        Assert.Equal(a, b);
    }

    private const string SampleJson = """
    {
      "workQueue": [
        { "repository": "microsoft/aspire", "kind": "pr", "number": 18549, "htmlUrl": "https://x/1",
          "title": "arcade bump", "humanOnly": true, "action": "review & merge", "ageDays": 0 }
      ],
      "prNotes": [
        { "repository": "microsoft/aspire", "number": 18549, "bucket": "easy-win",
          "why": "green + mergeable", "action": "review & merge" }
      ],
      "issueGroups": [
        { "theme": "[Deployment E2E] nightly", "severity": "attention",
          "summary": "recurring nightly break", "recommendation": "consolidate to newest, close dupes",
          "issues": [ { "repository": "microsoft/aspire", "number": 18550, "htmlUrl": "https://x/2", "title": "fail", "ageDays": 1 } ] }
      ]
    }
    """;

    [Fact]
    public void ParsesShepherdJson_StampsFingerprintAndTimestamp()
    {
        var snapshot = BotShepherdRunner.ParseShepherdOutput(SampleJson, "FP123", Now);

        Assert.Null(snapshot.Error);
        Assert.False(snapshot.FromCache);
        Assert.Equal("FP123", snapshot.Fingerprint);
        Assert.Equal(Now, snapshot.UpdatedAt);

        var queued = Assert.Single(snapshot.WorkQueue);
        Assert.Equal(18549, queued.Number);
        Assert.True(queued.HumanOnly);

        var note = Assert.Single(snapshot.PrNotes);
        Assert.Equal("easy-win", note.Bucket);

        var group = Assert.Single(snapshot.IssueGroups);
        Assert.Equal("attention", group.Severity);
        Assert.Single(group.Issues);
    }

    [Fact]
    public void ParsesShepherdJson_FromMarkdownFence()
    {
        var snapshot = BotShepherdRunner.ParseShepherdOutput("```json\n" + SampleJson + "\n```", "FP", Now);

        Assert.Null(snapshot.Error);
        Assert.Single(snapshot.WorkQueue);
    }

    [Fact]
    public void ReturnsErrorOnMalformedJson()
    {
        var snapshot = BotShepherdRunner.ParseShepherdOutput("{ \"workQueue\": [ {", "FP", Now);

        Assert.NotNull(snapshot.Error);
        Assert.Empty(snapshot.WorkQueue);
        Assert.Equal("FP", snapshot.Fingerprint);
    }

    [Fact]
    public void ReturnsErrorWhenNoJsonObject()
    {
        var snapshot = BotShepherdRunner.ParseShepherdOutput("could not shepherd", "FP", Now);

        Assert.NotNull(snapshot.Error);
        Assert.Empty(snapshot.IssueGroups);
    }
}
