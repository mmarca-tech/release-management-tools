using System.Text.Json.Nodes;
using PipelineTools.ReleaseTools;

namespace PipelineTools.ReleaseTools.Tests;

public class CreateTicketFieldResolverTests
{
    [Fact]
    public void CreateJiraUserReference_uses_account_id()
    {
        var resolved = CreateReleaseTicketCommand.CreateJiraUserReference("account-id");

        Assert.Equal("account-id", resolved["id"]?.GetValue<string>());
    }

    [Fact]
    public void CreateJiraUserReferenceArray_wraps_account_id_for_multi_user_field()
    {
        var resolved = CreateReleaseTicketCommand.CreateJiraUserReferenceArray("account-id");

        Assert.Equal("account-id", resolved[0]?["id"]?.GetValue<string>());
        Assert.Equal("[{\"id\":\"account-id\"}]", resolved.ToJsonString());
    }

    [Theory]
    [InlineData("Normal")]
    [InlineData("Major")]
    public void ResolvePostCreateRoute_moves_approval_changes_through_required_workflow(string changeType)
    {
        var route = CreateReleaseTicketCommand.ResolvePostCreateRoute(changeType);

        var step = Assert.Single(route.Steps);
        Assert.Equal("Review the Change", step.TargetTransitionName);
        Assert.Equal("Review", step.TargetStatusName);
        Assert.Contains("review acknowledgment", route.Comment, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Standard")]
    [InlineData("Emergency")]
    public void ResolvePostCreateRoute_moves_non_approval_changes_directly_to_awaiting_implementation(string changeType)
    {
        var route = CreateReleaseTicketCommand.ResolvePostCreateRoute(changeType);

        var step = Assert.Single(route.Steps);
        Assert.Equal("11505", step.TargetStatusId);
        Assert.Equal("Awaiting implementation", step.TargetStatusName);
    }

    [Theory]
    [InlineData("Standard", "Standard - Routine change")]
    [InlineData("Normal", "Normal - Controlled change with approval")]
    [InlineData("Major", "Major - requires Bank CAB")]
    [InlineData("Emergency", "Emergency")]
    public void ResolveChangeTypeJiraValue_maps_aliases(string input, string expected)
    {
        Assert.Equal(expected, CreateReleaseTicketCommand.ResolveChangeTypeJiraValue(input));
    }

    [Theory]
    [InlineData("Release", "Release")]
    [InlineData("Fix via SQL script", "Fix via SQL script")]
    [InlineData("3rd Party release/maintenance", "3rd Party release/maintenance")]
    public void ResolveChangeReasonJiraValue_maps_aliases(string input, string expected)
    {
        Assert.Equal(expected, CreateReleaseTicketCommand.ResolveChangeReasonJiraValue(input));
    }

    [Theory]
    [InlineData("Low", "Low")]
    [InlineData("Critical", "Critical")]
    public void ResolveChangeRiskJiraValue_maps_aliases(string input, string expected)
    {
        Assert.Equal(expected, CreateReleaseTicketCommand.ResolveChangeRiskJiraValue(input));
    }

    [Theory]
    [InlineData("Minor / Localized", "Minor / Localized")]
    [InlineData("Extensive / Widespread", "Extensive / Widespread")]
    public void ResolveChangeImpactJiraValue_maps_aliases(string input, string expected)
    {
        Assert.Equal(expected, CreateReleaseTicketCommand.ResolveChangeImpactJiraValue(input));
    }

    [Fact]
    public void ResolveChangeTypeJiraValue_rejects_unknown()
    {
        Assert.Throws<CliException>(() => CreateReleaseTicketCommand.ResolveChangeTypeJiraValue("Nope"));
    }

    [Fact]
    public void ResolvePlannedDateTimeUtcCore_parses_date_only_as_utc_midnight()
    {
        var fallback = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        var resolved = CreateReleaseTicketCommand.ResolvePlannedDateTimeUtcCore("2026-08-03", fallback, "PlannedStartDate");
        Assert.Equal(new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero), resolved);
    }

    [Fact]
    public void ResolvePlannedDateTimeUtcCore_empty_uses_default()
    {
        var fallback = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        var resolved = CreateReleaseTicketCommand.ResolvePlannedDateTimeUtcCore("", fallback, "PlannedStartDate");
        Assert.Equal(fallback.ToUniversalTime(), resolved);
    }

    [Fact]
    public void GetDefaultPlannedWindowUtc_end_is_one_hour_after_start()
    {
        var (start, end) = CreateReleaseTicketCommand.GetDefaultPlannedWindowUtc();
        Assert.Equal(TimeSpan.FromHours(1), end - start);
        Assert.Equal(0, start.Minute);
        Assert.Equal(0, start.Second);
    }

    [Fact]
    public void ResolveChangeTypeJiraValue_empty_defaults_to_Normal()
    {
        Assert.Equal(
            "Normal - Controlled change with approval",
            CreateReleaseTicketCommand.ResolveChangeTypeJiraValue(""));
    }

    [Fact]
    public void AddReporterField_sets_reporter_account_id()
    {
        var fields = new JsonObject();

        CreateReleaseTicketCommand.AddReporterField(fields, " jira-account-id ");

        Assert.Equal("jira-account-id", fields["reporter"]?["id"]?.GetValue<string>());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void AddReporterField_leaves_reporter_unset_when_empty(string reporter)
    {
        var fields = new JsonObject();

        CreateReleaseTicketCommand.AddReporterField(fields, reporter);

        Assert.Null(fields["reporter"]);
    }

    [Theory]
    [InlineData("Review", "Review")]
    [InlineData("Review the Change", "Review")]
    [InlineData("Planning", "Planning")]
    [InlineData("Go Live Decision", "GoLiveDecision")]
    [InlineData("Awaiting implementation", "AwaitingImplementation")]
    [InlineData("Declined", "Unsupported")]
    public void JiraReviewWorkflow_resolves_review_checkpoint(string statusName, string expected)
    {
        Assert.Equal(expected, JiraReviewWorkflow.ResolveCheckpoint(statusName).ToString());
    }

    [Theory]
    [InlineData("11505", "Awaiting implementation", true)]
    [InlineData("11507", "Implementing", false)]
    [InlineData(null, "Awaiting implementation", true)]
    public void IsAwaitingImplementationStatus_matches_id_or_name(string? statusId, string? statusName, bool expected)
    {
        Assert.Equal(expected, Jira.IsAwaitingImplementationStatus(statusId, statusName));
    }
}
