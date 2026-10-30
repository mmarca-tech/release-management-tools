using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PipelineTools.ReleaseTools;

static class CheckApprovalGateCommand
{
    public static async Task<int> RunAsync(CliOptions options)
    {
        var jiraUrl = options.Get("JiraUrl");
        var jiraCloudId = options.Require("JiraCloudId");
        var jiraTicketKey = options.Require("JiraTicketKey");
        var jiraUser = options.Require("JiraUser");
        var jiraToken = options.Require("JiraToken");
        var changeTypeFieldId = options.Get("ChangeTypeFieldId", "customfield_13303")!;
        var approvalFieldId = options.Get("ApprovalFieldId", "customfield_13301")!;
        var setOutputVariables = options.HasSwitch("SetOutputVariables");
        var failIfBlocked = options.HasSwitch("FailIfBlocked");

        Console.WriteLine($"Checking Jira approval prerequisites for Awaiting implementation on '{jiraTicketKey}'");

        var result = await ApprovalGate.TestAsync(
            jiraUrl,
            jiraCloudId,
            jiraTicketKey,
            jiraUser,
            jiraToken,
            changeTypeFieldId,
            approvalFieldId);

        var isBlocking = !result.Success && result.ApprovalRequired == true;
        var gateStatus = result.Success ? "passed" : isBlocking ? "blocked" : "error";
        var message = BuildMessage(result, jiraTicketKey, approvalFieldId, isBlocking);

        Console.WriteLine(message);
        if (!string.IsNullOrWhiteSpace(result.TicketUrl))
        {
            Console.WriteLine($"Ticket URL: {result.TicketUrl}");
        }

        if (!string.IsNullOrWhiteSpace(result.IssueUrl))
        {
            Console.WriteLine($"Issue API URL: {result.IssueUrl}");
        }

        if (setOutputVariables)
        {
            AzurePipelines.SetOutputVariable("approvalGateStatus", gateStatus);
            AzurePipelines.SetOutputVariable("approvalGateBlocking", isBlocking ? "true" : "false");
            AzurePipelines.SetOutputVariable("approvalGateRequired", result.ApprovalRequired == true ? "true" : "false");
            AzurePipelines.SetOutputVariable("approvalGateMessage", message);
            AzurePipelines.SetOutputVariable("approvalGateChangeType", result.ChangeTypeRaw);
        }

        if (!result.Success)
        {
            if (isBlocking && !failIfBlocked)
            {
                Console.WriteLine($"WARNING: {result.ErrorMessage}");
                return 0;
            }

            Console.Error.WriteLine(result.ErrorMessage);
            return 1;
        }

        return 0;
    }

    private static string BuildMessage(ApprovalGateResult result, string jiraTicketKey, string approvalFieldId, bool isBlocking)
    {
        if (result.Success)
        {
            return result.ApprovalRequired
                ? $"Approval prerequisites satisfied for Jira ticket '{jiraTicketKey}'. Change type '{result.ChangeTypeRaw}' has approvers={result.ApproverCount}, approvedApprovers={result.ApprovedApproverCount}, approvedDecisions={result.ApprovedDecisionCount}."
                : $"Approval prerequisites are not required for Jira ticket '{jiraTicketKey}' because change type is '{result.ChangeTypeRaw}'.";
        }

        return isBlocking
            ? $"Jira ticket '{jiraTicketKey}' cannot move to Awaiting implementation yet. Change type '{result.ChangeTypeRaw}' requires at least one approver in Jira field '{approvalFieldId}' and at least one approved decision before proceeding. Update the Jira ticket, then resume the pipeline."
            : result.ErrorMessage ?? "Approval gate failed.";
    }
}

