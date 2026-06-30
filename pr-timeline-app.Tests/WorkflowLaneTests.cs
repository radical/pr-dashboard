using Xunit;

namespace pr_timeline_app.Tests;

public sealed class WorkflowLaneTests
{
    private static readonly string[] Branches = ["main", "release/*"];
    private static readonly string[] MainWorkflows = ["CI"];
    private static readonly string[] NoSkip = [];

    [Fact]
    public void Resolve_MainWorkflowOnTrackedBranch_IsMainLane()
    {
        var a = WorkflowLane.Resolve("CI", "push", "main", Branches, MainWorkflows, NoSkip);
        Assert.NotNull(a);
        Assert.Equal("CI \u00b7 main", a!.Lane);
        Assert.Equal("main", a.Section);
    }

    [Fact]
    public void Resolve_MainWorkflowOnReleaseGlob_KeepsBranchInLabel()
    {
        var a = WorkflowLane.Resolve("CI", "push", "release/13.4", Branches, MainWorkflows, NoSkip);
        Assert.Equal("CI \u00b7 release/13.4", a!.Lane);
        Assert.Equal("main", a.Section);
    }

    [Fact]
    public void Resolve_NonMainWorkflowOnTrackedBranch_IsDropped()
    {
        // Only the configured main workflow(s) form main lanes.
        Assert.Null(WorkflowLane.Resolve("Markdownlint", "push", "main", Branches, MainWorkflows, NoSkip));
    }

    [Fact]
    public void Resolve_EmptyMainWorkflows_AllowsAnyWorkflowOnTrackedBranch()
    {
        var a = WorkflowLane.Resolve("Build and Test", "push", "main", Branches, [], NoSkip);
        Assert.Equal("Build and Test \u00b7 main", a!.Lane);
        Assert.Equal("main", a.Section);
    }

    [Fact]
    public void Resolve_ScheduledWorkflow_IsScheduledLane()
    {
        var a = WorkflowLane.Resolve("Outerloop Tests", "schedule", "main", Branches, MainWorkflows, NoSkip);
        Assert.Equal("Outerloop Tests \u00b7 scheduled", a!.Lane);
        Assert.Equal("scheduled", a.Section);
    }

    [Fact]
    public void Resolve_ScheduledOnSkipList_IsDropped()
    {
        Assert.Null(WorkflowLane.Resolve("Agentic Maintenance", "schedule", "main", Branches, MainWorkflows, ["Agentic Maintenance"]));
    }

    [Theory]
    [InlineData("pull_request", "feature")]  // PR dropped
    [InlineData("push", "feature/x")]        // untracked branch dropped
    [InlineData("workflow_dispatch", "main")] // dispatch dropped
    public void Resolve_DropsUntrackedRuns(string @event, string headBranch)
    {
        Assert.Null(WorkflowLane.Resolve("CI", @event, headBranch, Branches, MainWorkflows, NoSkip));
    }

    [Theory]
    [InlineData("release/13.4", "release/*", true)]
    [InlineData("main", "release/*", false)]
    [InlineData("Main", "main", true)]
    [InlineData("anything", "*", true)]
    public void MatchesBranch_HandlesGlobAndExact(string branch, string pattern, bool expected)
    {
        Assert.Equal(expected, WorkflowLane.MatchesBranch(branch, pattern));
    }

    [Theory]
    [InlineData(".github/workflows/analyze-ci-failure.lock.yml", "analyze-ci-failure")]
    [InlineData(".github/workflows/ci.yml", "ci")]
    [InlineData("Build and Test", "Build and Test")]
    public void CleanName_StripsPathAndKnownSuffixes(string raw, string expected)
    {
        Assert.Equal(expected, WorkflowLane.CleanName(raw));
    }
}
