using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PipelineTools.ReleaseTools;

sealed record ChangeTypeInfo(string? RawValue, string? Category);


sealed record ApprovalSummary(
    int ApprovalCount,
    int ApproverCount,
    int ApprovedApproverCount,
    int ApprovedDecisionCount,
    bool HasApprovedDecision)
{
    public static readonly ApprovalSummary Empty = new(0, 0, 0, 0, false);
}


sealed record ApprovalGateResult(
    bool Success,
    string JiraTicketKey,
    string? IssueUrl,
    string? TicketUrl,
    string ChangeTypeFieldId,
    string ApprovalFieldId,
    string? ChangeTypeRaw,
    string? ChangeTypeCategory,
    bool ApprovalRequired,
    int ApprovalCount,
    int ApproverCount,
    int ApprovedApproverCount,
    int ApprovedDecisionCount,
    string? ErrorMessage,
    int? StatusCode)
{
    public static ApprovalGateResult Passed(
        string jiraTicketKey,
        string? issueUrl,
        string? ticketUrl,
        string changeTypeFieldId,
        string approvalFieldId,
        string? changeTypeRaw,
        string? changeTypeCategory,
        bool approvalRequired,
        ApprovalSummary summary) =>
        new(
            true,
            jiraTicketKey,
            issueUrl,
            ticketUrl,
            changeTypeFieldId,
            approvalFieldId,
            changeTypeRaw,
            changeTypeCategory,
            approvalRequired,
            summary.ApprovalCount,
            summary.ApproverCount,
            summary.ApprovedApproverCount,
            summary.ApprovedDecisionCount,
            null,
            null);

    public static ApprovalGateResult Failed(
        string jiraTicketKey,
        string? issueUrl,
        string? ticketUrl,
        string changeTypeFieldId,
        string approvalFieldId,
        string? changeTypeRaw,
        string? changeTypeCategory,
        bool? approvalRequired,
        ApprovalSummary summary,
        string? errorMessage,
        int? statusCode) =>
        new(
            false,
            jiraTicketKey,
            issueUrl,
            ticketUrl,
            changeTypeFieldId,
            approvalFieldId,
            changeTypeRaw,
            changeTypeCategory,
            approvalRequired ?? false,
            summary.ApprovalCount,
            summary.ApproverCount,
            summary.ApprovedApproverCount,
            summary.ApprovedDecisionCount,
            errorMessage,
            statusCode);
}

