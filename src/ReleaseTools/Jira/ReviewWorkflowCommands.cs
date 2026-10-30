namespace PipelineTools.ReleaseTools;

internal enum JiraReviewCheckpoint
{
    Review,
    Planning,
    GoLiveDecision,
    AwaitingImplementation,
    Unsupported
}

internal static class JiraReviewWorkflow
{
    internal static JiraReviewCheckpoint ResolveCheckpoint(string? statusName)
    {
        var normalizedStatus = statusName?.Trim();
        if (string.Equals(normalizedStatus, "Review", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedStatus, "Review the Change", StringComparison.OrdinalIgnoreCase))
        {
            return JiraReviewCheckpoint.Review;
        }

        if (string.Equals(normalizedStatus, "Planning", StringComparison.OrdinalIgnoreCase))
        {
            return JiraReviewCheckpoint.Planning;
        }

        if (string.Equals(normalizedStatus, "Go Live Decision", StringComparison.OrdinalIgnoreCase))
        {
            return JiraReviewCheckpoint.GoLiveDecision;
        }

        if (string.Equals(normalizedStatus, "Awaiting implementation", StringComparison.OrdinalIgnoreCase))
        {
            return JiraReviewCheckpoint.AwaitingImplementation;
        }

        return JiraReviewCheckpoint.Unsupported;
    }
}

static class CheckReviewAcknowledgementCommand
{
    public static async Task<int> RunAsync(CliOptions options)
    {
        var jiraUrl = options.Get("JiraUrl");
        var jiraCloudId = options.Require("JiraCloudId");
        var jiraTicketKey = options.Require("JiraTicketKey");
        var jiraUser = options.Require("JiraUser");
        var jiraToken = options.Require("JiraToken");

        Console.WriteLine($"Checking Jira review checkpoint for '{jiraTicketKey}'");
        var statusResult = await Jira.GetIssueStatusAsync(jiraCloudId, jiraTicketKey, jiraUser, jiraToken);
        if (!statusResult.Success)
        {
            Console.Error.WriteLine(statusResult.ErrorMessage);
            return 1;
        }

        var checkpoint = JiraReviewWorkflow.ResolveCheckpoint(statusResult.StatusName);
        if (checkpoint == JiraReviewCheckpoint.Unsupported)
        {
            Console.Error.WriteLine(
                $"Jira ticket '{jiraTicketKey}' is in unexpected status '{statusResult.StatusName ?? "<unknown>"}' [{statusResult.StatusId}]. Expected Review, Planning, Go Live Decision, or Awaiting implementation.");
            return 1;
        }

        var requiresAcknowledgement = checkpoint is JiraReviewCheckpoint.Review or JiraReviewCheckpoint.Planning;
        var ticketUrl = Jira.BuildTicketUrl(jiraUrl, jiraTicketKey);
        var message = checkpoint switch
        {
            JiraReviewCheckpoint.Review =>
                $"Jira ticket '{jiraTicketKey}' is in Review. Complete the review in Jira and move the ticket to Planning before acknowledging the review in this pipeline.",
            JiraReviewCheckpoint.Planning =>
                $"Jira ticket '{jiraTicketKey}' is in Planning. Resume this validation to confirm that someone reviewed the ticket and moved it to Planning; the pipeline will then move it to Go Live Decision.",
            _ =>
                $"Jira ticket '{jiraTicketKey}' is already in '{statusResult.StatusName}' [{statusResult.StatusId}]; the review checkpoint has been passed."
        };
        if (!string.IsNullOrWhiteSpace(ticketUrl))
        {
            message = $"{message} Ticket: {ticketUrl}";
        }

        Console.WriteLine(message);
        if (!string.IsNullOrWhiteSpace(ticketUrl))
        {
            Console.WriteLine($"Ticket URL: {ticketUrl}");
        }
        AzurePipelines.SetOutputVariable("reviewAcknowledgementBlocking", requiresAcknowledgement ? "true" : "false");
        AzurePipelines.SetOutputVariable("reviewAcknowledgementMessage", message);
        return 0;
    }
}

static class AdvanceToGoLiveDecisionCommand
{
    public static async Task<int> RunAsync(CliOptions options)
    {
        var jiraUrl = options.Get("JiraUrl");
        var jiraCloudId = options.Require("JiraCloudId");
        var jiraTicketKey = options.Require("JiraTicketKey");
        var jiraUser = options.Require("JiraUser");
        var jiraToken = options.Require("JiraToken");

        var statusResult = await Jira.GetIssueStatusAsync(jiraCloudId, jiraTicketKey, jiraUser, jiraToken);
        if (!statusResult.Success)
        {
            Console.Error.WriteLine(statusResult.ErrorMessage);
            return 1;
        }

        var checkpoint = JiraReviewWorkflow.ResolveCheckpoint(statusResult.StatusName);
        if (checkpoint is JiraReviewCheckpoint.GoLiveDecision or JiraReviewCheckpoint.AwaitingImplementation)
        {
            Console.WriteLine(
                $"Jira ticket '{jiraTicketKey}' is already in '{statusResult.StatusName}' [{statusResult.StatusId}]; no transition is needed.");
            return 0;
        }

        if (checkpoint != JiraReviewCheckpoint.Planning)
        {
            Console.Error.WriteLine(
                $"Cannot move Jira ticket '{jiraTicketKey}' to Go Live Decision from '{statusResult.StatusName ?? "<unknown>"}' [{statusResult.StatusId}]. The reviewer must move it to Planning before the pipeline acknowledgment.");
            return 1;
        }

        Console.WriteLine($"Moving Jira ticket '{jiraTicketKey}' from Planning to Go Live Decision...");
        var transitionResult = await Jira.InvokeIssueTransitionAsync(
            jiraUrl,
            jiraCloudId,
            jiraTicketKey,
            jiraUser,
            jiraToken,
            null,
            null,
            null,
            "Go Live Decision");

        if (!transitionResult.Success)
        {
            Console.Error.WriteLine(transitionResult.ErrorMessage);
            Jira.WriteAvailableTransitions(transitionResult.AvailableTransitions);
            return 1;
        }

        Console.WriteLine($"Transitioned '{jiraTicketKey}' to '{transitionResult.ToStatusName}' [{transitionResult.ToStatusId}]");
        var ticketUrl = Jira.BuildTicketUrl(jiraUrl, jiraTicketKey);
        if (!string.IsNullOrWhiteSpace(ticketUrl))
        {
            Console.WriteLine($"Ticket URL: {ticketUrl}");
        }

        var commentResult = await Jira.AddCommentAsync(
            jiraCloudId,
            jiraTicketKey,
            jiraUser,
            jiraToken,
            "Pipeline moved the ticket from Planning to Go Live Decision after the review acknowledgment. Go Live approval remains pending in Jira.");
        if (!commentResult.Success)
        {
            Console.WriteLine($"WARNING: Failed to add Go Live Decision transition comment to {jiraTicketKey}: {commentResult.ErrorMessage}");
        }

        return 0;
    }
}
