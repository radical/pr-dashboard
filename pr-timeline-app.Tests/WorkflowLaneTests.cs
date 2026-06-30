using Xunit;

namespace pr_timeline_app.Tests;

public sealed class WorkflowLaneTests
{
    [Theory]
    [InlineData("pull_request", "feature", "main", "PullRequest")]
    [InlineData("schedule", "main", "main", "Scheduled")]
    [InlineData("push", "main", "main", "Main")]
    [InlineData("push", "Main", "main", "Main")]            // case-insensitive branch match
    [InlineData("push", "feature/x", "main", "Other")]      // non-default push
    [InlineData("workflow_dispatch", "main", "main", "Other")]
    public void Classify_MapsEventAndBranchToTrigger(string @event, string headBranch, string defaultBranch, string expected)
    {
        Assert.Equal(expected, WorkflowLane.Classify(@event, headBranch, defaultBranch).ToString());
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

    [Theory]
    [InlineData("CI", "Main", "CI \u00b7 main")]
    [InlineData("CI", "PullRequest", "CI \u00b7 PR")]
    [InlineData(".github/workflows/tests-outerloop.yml", "Scheduled", "tests-outerloop \u00b7 scheduled")]
    public void LaneLabel_CombinesCleanNameAndTriggerLabel(string workflow, string trigger, string expected)
    {
        Assert.Equal(expected, WorkflowLane.LaneLabel(workflow, Enum.Parse<LaneTrigger>(trigger)));
    }
}
