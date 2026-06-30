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
    public void IncludesAllowlistedAuthors_StrippingBotSuffix()
    {
        // REST /pulls returns App bots with a trailing "[bot]" suffix; these are the bots the block
        // exists to surface, so a bare-name allowlist must still match them.
        var prs = new[]
        {
            Pr(1, "dependabot[bot]"),
            Pr(2, "github-actions[bot]"),
            Pr(3, "octocat"),
        };

        var result = BotPrClassifier.Classify(prs, Allowlist);

        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { "dependabot", "github-actions" }, result.Select(b => b.Author).ToArray());
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
            Pr(3, "copilot-swe-agent[bot]", isBot: true), // real REST shape
        };

        Assert.Empty(BotPrClassifier.Classify(prs, Allowlist));
    }

    [Fact]
    public void AlwaysDropsCopilotApps_EvenWhenAutomationBroken()
    {
        // "Always drop Copilot" is absolute and takes precedence over the automation-broken union.
        var prs = new[] { Pr(1, "copilot-swe-agent[bot]", labels: ["automation-broken"]) };

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
