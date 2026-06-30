using Xunit;

namespace pr_timeline_app.Tests;

public sealed class WorkflowLaneTests
{
    private static readonly string[] Branches = ["main", "release/*"];
    private static readonly string[] MainWorkflows = ["CI", "Outerloop Tests"];
    private static readonly string[] NoSkip = [];

    [Fact]
    public void Resolve_MainWorkflowOnTrackedBranch_IsMainLane()
    {
        var a = WorkflowLane.Resolve("CI", "push", "main", Branches, MainWorkflows, NoSkip);
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
    public void Resolve_ScheduledMainWorkflow_IsMainSection()
    {
        // Outerloop is scheduled but configured as a main workflow -> main section, no trigger suffix.
        var a = WorkflowLane.Resolve("Outerloop Tests", "schedule", "main", Branches, MainWorkflows, NoSkip);
        Assert.Equal("Outerloop Tests", a!.Lane);
        Assert.Equal("main", a.Section);
    }

    [Fact]
    public void Resolve_OtherScheduledWorkflow_IsScheduledSection()
    {
        var a = WorkflowLane.Resolve("Quarantined Tests", "schedule", "main", Branches, MainWorkflows, NoSkip);
        Assert.Equal("Quarantined Tests", a!.Lane);
        Assert.Equal("scheduled", a.Section);
    }

    [Fact]
    public void Resolve_NonMainWorkflowOnTrackedBranch_IsDropped()
    {
        Assert.Null(WorkflowLane.Resolve("Markdownlint", "push", "main", Branches, MainWorkflows, NoSkip));
    }

    [Fact]
    public void Resolve_ScheduledOnSkipList_IsDropped()
    {
        Assert.Null(WorkflowLane.Resolve("Noisy Cron", "schedule", "main", Branches, MainWorkflows, ["Noisy Cron"]));
    }

    [Theory]
    [InlineData("pull_request", "feature")]
    [InlineData("push", "feature/x")]
    [InlineData("workflow_dispatch", "main")]
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
    [InlineData("Agentic Maintenance (microsoft/aspire.dev)", "Agentic Maintenance (microsoft/aspire.dev)")]
    public void CleanName_OnlyStripsActualPaths(string raw, string expected)
    {
        Assert.Equal(expected, WorkflowLane.CleanName(raw));
    }
}
