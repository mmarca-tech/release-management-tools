using System.Globalization;
using System.Text.Json.Nodes;

namespace PipelineTools.ReleaseTools;

static class TransitionJiraTicketCommand
{
    public static async Task<int> RunAsync(CliOptions options)
    {
        var jiraUrl = options.Get("JiraUrl");
        var jiraCloudId = options.Require("JiraCloudId");
        var jiraTicketKey = options.Require("JiraTicketKey");
        var jiraUser = options.Require("JiraUser");
        var jiraToken = options.Require("JiraToken");
        var targetTransitionId = options.Get("TargetTransitionId");
        var targetTransitionName = options.Get("TargetTransitionName");
        var targetStatusId = options.Get("TargetStatusId");
        var targetStatusName = options.Get("TargetStatusName");
        var comment = options.Get("Comment", "")!;
        var postImplementationStatus = options.Get("PostImplementationStatus", "")!;
        var setActualStartDate = options.HasSwitch("SetActualStartDate");
        var setActualEndDate = options.HasSwitch("SetActualEndDate");
        var actualStartDateFieldId = options.Get("ActualStartDateFieldId", "customfield_13306")!;
        var actualEndDateFieldId = options.Get("ActualEndDateFieldId", "customfield_13307")!;
        var postImplementationStatusFieldId = options.Get("PostImplementationStatusFieldId", "customfield_16629")!;

        var fieldsToUpdate = new JsonObject();
        var nowUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        if (setActualStartDate)
        {
            fieldsToUpdate[actualStartDateFieldId] = nowUtc;
        }

        if (setActualEndDate)
        {
            fieldsToUpdate[actualEndDateFieldId] = nowUtc;
        }

        if (!string.IsNullOrWhiteSpace(postImplementationStatus))
        {
            fieldsToUpdate[postImplementationStatusFieldId] = postImplementationStatus.Trim();
        }

        if (fieldsToUpdate.Count > 0)
        {
            Console.WriteLine($"Updating Jira fields on '{jiraTicketKey}' before transition...");
            var updateResult = await Jira.UpdateIssueFieldsAsync(
                jiraCloudId,
                jiraTicketKey,
                jiraUser,
                jiraToken,
                fieldsToUpdate);
            if (!updateResult.Success)
            {
                Console.Error.WriteLine($"Failed to update Jira fields for '{jiraTicketKey}': {updateResult.ErrorMessage}");
                return 1;
            }
        }

        var targetLabel = Jira.GetTransitionSelectorSummary(targetTransitionId, targetTransitionName, targetStatusId, targetStatusName);
        Console.WriteLine($"Transitioning JIRA ticket '{jiraTicketKey}' using selectors: {targetLabel}");

        var result = await Jira.InvokeIssueTransitionAsync(
            jiraUrl,
            jiraCloudId,
            jiraTicketKey,
            jiraUser,
            jiraToken,
            targetTransitionId,
            targetTransitionName,
            targetStatusId,
            targetStatusName);

        if (!result.Success)
        {
            Console.Error.WriteLine(result.ErrorMessage);
            Jira.WriteAvailableTransitions(result.AvailableTransitions);
            return 1;
        }

        Console.WriteLine($"Using transition ID: {result.TransitionId}");
        Console.WriteLine($"Matched transition '{result.TransitionName}' to status '{result.ToStatusName}' [{result.ToStatusId}]");
        Console.WriteLine($"Successfully transitioned ticket: {jiraTicketKey}");
        if (!string.IsNullOrWhiteSpace(result.TicketUrl))
        {
            Console.WriteLine($"Ticket URL: {result.TicketUrl}");
        }

        if (!string.IsNullOrWhiteSpace(comment))
        {
            var commentResult = await Jira.AddCommentAsync(jiraCloudId, jiraTicketKey, jiraUser, jiraToken, comment.Trim());
            if (!commentResult.Success)
            {
                Console.WriteLine($"WARNING: Failed to add comment to {jiraTicketKey}: {commentResult.ErrorMessage}");
            }
        }

        return 0;
    }
}
