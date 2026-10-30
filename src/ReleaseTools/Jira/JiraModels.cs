using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PipelineTools.ReleaseTools;

sealed record JiraApiContext(string BaseUrl, IReadOnlyDictionary<string, string> Headers);


sealed record JiraTransition(string? Id, string? Name, string? ToStatusId, string? ToStatusName, JsonNode? Raw);


sealed record JiraTransitionsResult(
    bool Success,
    IReadOnlyDictionary<string, string> Headers,
    string TransitionUrl,
    IReadOnlyList<JiraTransition> AvailableTransitions,
    int? StatusCode,
    string? ErrorMessage);


sealed record JiraIssueFieldsResult(
    bool Success,
    string IssueUrl,
    JsonNode? Issue,
    JsonNode? Fields,
    int? StatusCode,
    string? ErrorMessage);


sealed record JiraIssueStatusResult(
    bool Success,
    string? IssueUrl,
    string? StatusId,
    string? StatusName,
    int? StatusCode,
    string? ErrorMessage);


sealed record JiraIssueTransitionResult(
    bool Success,
    string JiraTicketKey,
    string TransitionUrl,
    string? TicketUrl,
    JiraTransition? Transition,
    IReadOnlyList<JiraTransition> AvailableTransitions,
    int? StatusCode,
    string? ErrorMessage)
{
    public string? TransitionId => Transition?.Id;
    public string? TransitionName => Transition?.Name;
    public string? ToStatusId => Transition?.ToStatusId;
    public string? ToStatusName => Transition?.ToStatusName;

    public static JiraIssueTransitionResult Failed(
        string jiraTicketKey,
        string transitionUrl,
        string? ticketUrl,
        IReadOnlyList<JiraTransition> availableTransitions,
        int? statusCode,
        string? errorMessage) =>
        new(false, jiraTicketKey, transitionUrl, ticketUrl, null, availableTransitions, statusCode, errorMessage);
}

