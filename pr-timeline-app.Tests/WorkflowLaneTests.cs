using Xunit;

namespace pr_timeline_app.Tests;

public sealed class WorkflowLaneTests
{
    private static readonly string[] AspireBranches = ["main", "release/*"];

    [Theory]
    [InlineData("CI", "push", "main", "CI \u00b7 main")]              // rolling on a tracked branch
    [InlineData("CI", "push", "release/13.4", "CI \u00b7 release/13.4")] // glob match keeps the branch
    [InlineData("CI", "pull_request", "feature", "CI \u00b7 PR")]    // PR lane included
    public void Resolve_ReturnsLaneForTrackedRuns(string workflow, string @event, string headBranch, string expected)
    {
        Assert.Equal(expected, WorkflowLane.Resolve(workflow, @event, headBranch, AspireBranches, includePullRequests: true));
    }

    [Theory]
    [InlineData("CI", "push", "feature/x")]          // push to an untracked branch
    [InlineData("CI", "schedule", "main")]           // scheduled run
    [InlineData("CI", "workflow_dispatch", "main")]  // manual dispatch
    public void Resolve_DropsUntrackedRuns(string workflow, string @event, string headBranch)
    {
        Assert.Null(WorkflowLane.Resolve(workflow, @event, headBranch, AspireBranches, includePullRequests: true));
    }

    [Fact]
    public void Resolve_DropsPrLaneWhenDisabled()
    {
        Assert.Null(WorkflowLane.Resolve("CI", "pull_request", "feature", AspireBranches, includePullRequests: false));
    }

    [Theory]
    [InlineData("release/13.4", "release/*", true)]
    [InlineData("release/next", "release/*", true)]
    [InlineData("main", "release/*", false)]
    [InlineData("main", "main", true)]
    [InlineData("Main", "main", true)]               // case-insensitive exact
    [InlineData("anything", "*", true)]
    public void MatchesBranch_HandlesGlobAndExact(string branch, string pattern, bool expected)
    {
        Assert.Equal(expected, WorkflowLane.MatchesBranch(branch, pattern));
    }

    [Theory]
    [InlineData("CI", "CI")]
    [InlineData(".github/workflows/analyze-ci-failure.lock.yml", "analyze-ci-failure")]
    [InlineData(".github/workflows/ci.yml", "ci")]
    [InlineData("Build and Test", "Build and Test")]
    [InlineData("deployment-tests.yaml", "deployment-tests")]
    public void CleanName_StripsPathAndKnownSuffixes(string raw, string expected)
    {
        Assert.Equal(expected, WorkflowLane.CleanName(raw));
    }
}
