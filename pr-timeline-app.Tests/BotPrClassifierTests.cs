using Xunit;

namespace pr_timeline_app.Tests;

public sealed class BotPrClassifierTests
{
    private static readonly string[] Allowlist =
        ["dependabot", "dotnet-maestro", "github-actions", "aspire-repo-bot"];

    private static CandidatePullRequest Pr(
        int number, string login, bool isBot = false, string[]? labels = null) =>
        new(
            Repository: "microsoft/aspire",
            Number: number,
            Title: $"PR {number}",
            AuthorLogin: login,
            AuthorIsBot: isBot,
            HtmlUrl: $"https://github.com/microsoft/aspire/pull/{number}",
            CiStatus: "passing",
            Labels: labels ?? []);

    [Fact]
    public void IncludesAllowlistedAuthors_StrippingAppPrefix()
    {
        var prs = new[] { Pr(1, "app/dependabot"), Pr(2, "octocat") };

        var result = BotPrClassifier.Classify(prs, Allowlist);

        var bot = Assert.Single(result);
        Assert.Equal(1, bot.Number);
        Assert.Equal("dependabot", bot.Author); // app/ prefix stripped
    }

    [Fact]
    public void IncludesIsBotAuthorsNotInAllowlist()
    {
        var prs = new[] { Pr(1, "some-random-bot", isBot: true) };

        var result = BotPrClassifier.Classify(prs, Allowlist);

        Assert.Single(result);
    }

    [Fact]
    public void AlwaysDropsCopilotApps_EvenWhenIsBot()
    {
        var prs = new[]
        {
            Pr(1, "app/copilot-swe-agent", isBot: true),
            Pr(2, "app/copilot-pull-request-reviewer", isBot: true),
        };

        Assert.Empty(BotPrClassifier.Classify(prs, Allowlist));
    }

    [Fact]
    public void DropsNoMergeAndAutomatedLabels()
    {
        var prs = new[]
        {
            Pr(1, "dependabot", labels: ["NO-MERGE"]),
            Pr(2, "dependabot", labels: ["automated"]),
            Pr(3, "dependabot"),
        };

        var result = BotPrClassifier.Classify(prs, Allowlist);

        var bot = Assert.Single(result);
        Assert.Equal(3, bot.Number);
    }

    [Fact]
    public void UnionsInAutomationBrokenRegardlessOfAuthor()
    {
        var prs = new[] { Pr(1, "human-dev", labels: ["automation-broken"]) };

        var result = BotPrClassifier.Classify(prs, Allowlist);

        Assert.Single(result);
    }

    [Fact]
    public void AutomationBrokenStillDroppedWhenNoMerge()
    {
        var prs = new[] { Pr(1, "human-dev", labels: ["automation-broken", "NO-MERGE"]) };

        Assert.Empty(BotPrClassifier.Classify(prs, Allowlist));
    }
}
