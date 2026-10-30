using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PipelineTools.ReleaseTools;

static class ApprovalGate
{
    public static async Task<ApprovalGateResult> TestAsync(
        string? jiraUrl,
        string jiraCloudId,
        string jiraTicketKey,
        string jiraUser,
        string jiraToken,
        string changeTypeFieldId,
        string approvalFieldId)
    {
        var issueFieldsResult = await Jira.GetIssueFieldsAsync(
            jiraCloudId,
            jiraTicketKey,
            jiraUser,
            jiraToken,
            [changeTypeFieldId, approvalFieldId]);

        if (!issueFieldsResult.Success)
        {
            return ApprovalGateResult.Failed(
                jiraTicketKey,
                issueFieldsResult.IssueUrl,
                Jira.BuildTicketUrl(jiraUrl, jiraTicketKey),
                changeTypeFieldId,
                approvalFieldId,
                null,
                null,
                null,
                ApprovalSummary.Empty,
                issueFieldsResult.ErrorMessage,
                issueFieldsResult.StatusCode);
        }

        var changeTypeFieldValue = GetIssueFieldValue(issueFieldsResult.Fields, changeTypeFieldId);
        var approvalFieldValue = GetIssueFieldValue(issueFieldsResult.Fields, approvalFieldId);
        var changeTypeInfo = ResolveChangeTypeInfo(changeTypeFieldValue);
        var approvalSummary = GetApprovalSummary(approvalFieldValue);

        if (string.IsNullOrWhiteSpace(changeTypeInfo.Category))
        {
            return ApprovalGateResult.Failed(
                jiraTicketKey,
                issueFieldsResult.IssueUrl,
                Jira.BuildTicketUrl(jiraUrl, jiraTicketKey),
                changeTypeFieldId,
                approvalFieldId,
                changeTypeInfo.RawValue,
                null,
                null,
                approvalSummary,
                $"Unable to determine change type for '{jiraTicketKey}' from Jira field '{changeTypeFieldId}'.",
                null);
        }

        var approvalRequired = changeTypeInfo.Category is "normal" or "major";
        if (!approvalRequired)
        {
            return ApprovalGateResult.Passed(
                jiraTicketKey,
                issueFieldsResult.IssueUrl,
                Jira.BuildTicketUrl(jiraUrl, jiraTicketKey),
                changeTypeFieldId,
                approvalFieldId,
                changeTypeInfo.RawValue,
                changeTypeInfo.Category,
                false,
                approvalSummary);
        }

        if (approvalSummary.ApproverCount <= 0)
        {
            return ApprovalGateResult.Failed(
                jiraTicketKey,
                issueFieldsResult.IssueUrl,
                Jira.BuildTicketUrl(jiraUrl, jiraTicketKey),
                changeTypeFieldId,
                approvalFieldId,
                changeTypeInfo.RawValue,
                changeTypeInfo.Category,
                true,
                approvalSummary,
                $"Jira ticket '{jiraTicketKey}' has change type '{changeTypeInfo.RawValue}' and requires at least one approver in Jira field '{approvalFieldId}' before transitioning to Awaiting implementation.",
                null);
        }

        if (!approvalSummary.HasApprovedDecision)
        {
            return ApprovalGateResult.Failed(
                jiraTicketKey,
                issueFieldsResult.IssueUrl,
                Jira.BuildTicketUrl(jiraUrl, jiraTicketKey),
                changeTypeFieldId,
                approvalFieldId,
                changeTypeInfo.RawValue,
                changeTypeInfo.Category,
                true,
                approvalSummary,
                $"Jira ticket '{jiraTicketKey}' has change type '{changeTypeInfo.RawValue}' and requires at least one approved approver in Jira field '{approvalFieldId}' before transitioning to Awaiting implementation.",
                null);
        }

        return ApprovalGateResult.Passed(
            jiraTicketKey,
            issueFieldsResult.IssueUrl,
            Jira.BuildTicketUrl(jiraUrl, jiraTicketKey),
            changeTypeFieldId,
            approvalFieldId,
            changeTypeInfo.RawValue,
            changeTypeInfo.Category,
            true,
            approvalSummary);
    }

    private static JsonNode? GetIssueFieldValue(JsonNode? fields, string fieldId) => JsonSupport.GetProperty(fields, fieldId);

    internal static ChangeTypeInfo ResolveChangeTypeInfo(JsonNode? changeTypeFieldValue)
    {
        var rawValue = JsonSupport.FirstNonEmptyString(
            JsonSupport.GetProperty(changeTypeFieldValue, "value"),
            JsonSupport.GetProperty(changeTypeFieldValue, "name"),
            JsonSupport.GetProperty(changeTypeFieldValue, "displayName"),
            changeTypeFieldValue);

        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return new ChangeTypeInfo(null, null);
        }

        var category = rawValue.Trim().ToLowerInvariant() switch
        {
            "standard" => "standard",
            "standard - routine change" => "standard",
            "normal" => "normal",
            "normal - controlled change with approval" => "normal",
            "major" => "major",
            "major - requires bank cab" => "major",
            "emergency" => "emergency",
            _ => null
        };

        return new ChangeTypeInfo(rawValue, category);
    }

    internal static ApprovalSummary GetApprovalSummary(JsonNode? approvalFieldValue)
    {
        var approvalRecords = GetApprovalRecords(approvalFieldValue);
        var approvalCount = 0;
        var approverCount = 0;
        var approvedApproverCount = 0;
        var approvedDecisionCount = 0;

        foreach (var approvalRecord in approvalRecords)
        {
            if (approvalRecord is null)
            {
                continue;
            }

            approvalCount++;
            var approvers = new List<JsonNode?>();
            if (JsonSupport.GetProperty(approvalRecord, "approvers") is JsonArray approverArray)
            {
                approvers.AddRange(approverArray);
            }
            else if (JsonSupport.GetProperty(approvalRecord, "approver") is not null)
            {
                approvers.Add(approvalRecord);
            }

            foreach (var approver in approvers)
            {
                if (approver is null)
                {
                    continue;
                }

                approverCount++;
                var approverDecision = JsonSupport.FirstNonEmptyString(
                    JsonSupport.GetProperty(approver, "approverDecision"),
                    JsonSupport.GetProperty(approver, "approvalDecision"),
                    JsonSupport.GetProperty(approver, "decision"),
                    JsonSupport.GetProperty(approver, "status"));

                if (IsApproved(approverDecision))
                {
                    approvedApproverCount++;
                }
            }

            var finalDecision = JsonSupport.FirstNonEmptyString(
                JsonSupport.GetProperty(approvalRecord, "finalDecision"),
                JsonSupport.GetProperty(approvalRecord, "decision"),
                JsonSupport.GetProperty(approvalRecord, "status"));

            if (IsApproved(finalDecision))
            {
                approvedDecisionCount++;
            }
        }

        return new ApprovalSummary(
            approvalCount,
            approverCount,
            approvedApproverCount,
            approvedDecisionCount,
            approvedApproverCount > 0 || approvedDecisionCount > 0);
    }

    private static List<JsonNode?> GetApprovalRecords(JsonNode? approvalFieldValue)
    {
        if (approvalFieldValue is null)
        {
            return [];
        }

        if (JsonSupport.GetProperty(approvalFieldValue, "values") is JsonArray values)
        {
            return values.ToList();
        }

        if (approvalFieldValue is JsonArray array)
        {
            return array.ToList();
        }

        return [approvalFieldValue];
    }

    private static bool IsApproved(string? decision) =>
        decision?.Trim().ToLowerInvariant() is "approved" or "approve";
}

