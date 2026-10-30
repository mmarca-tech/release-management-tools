using PipelineTools.ReleaseTools;

namespace PipelineTools.ReleaseTools.Tests;

public class JiraResolveTransitionTests
{
    private static readonly JiraTransition[] Transitions =
    [
        new("1", "Start Progress", "10", "In Progress", null),
        new("2", "Done", "20", "Done", null),
        new("3", "Complete Release", "11501", "Completed", null),
        new("4", "Alternate Complete", "11501", "Completed", null),
        new("5", "Fail Change", "14505", "Failed", null)
    ];

    [Fact]
    public void ResolveTransition_prefers_transition_id()
    {
        var match = Jira.ResolveTransition(Transitions, "5", null, null, null);
        Assert.Equal("5", match?.Id);
    }

    [Fact]
    public void ResolveTransition_matches_unique_status_id()
    {
        var match = Jira.ResolveTransition(Transitions, null, null, "14505", null);
        Assert.Equal("5", match?.Id);
    }

    [Fact]
    public void ResolveTransition_ambiguous_status_id_without_name_returns_null()
    {
        var match = Jira.ResolveTransition(Transitions, null, null, "11501", null);
        Assert.Null(match);
    }

    [Fact]
    public void ResolveTransition_ambiguous_status_id_refined_by_transition_name()
    {
        var match = Jira.ResolveTransition(Transitions, null, "Complete Release", "11501", null);
        Assert.Equal("3", match?.Id);
    }

    [Fact]
    public void ResolveTransition_matches_unique_status_name()
    {
        var match = Jira.ResolveTransition(Transitions, null, null, null, "Failed");
        Assert.Equal("5", match?.Id);
    }

    [Fact]
    public void ResolveTransition_requires_at_least_one_selector()
    {
        Assert.Throws<CliException>(() => Jira.ResolveTransition(Transitions, null, null, null, null));
    }
}
