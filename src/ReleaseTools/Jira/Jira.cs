using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PipelineTools.ReleaseTools;

static class Jira
{
    public static JiraApiContext GetContext(string jiraCloudId, string jiraUser, string jiraToken)
    {
        if (string.IsNullOrWhiteSpace(jiraCloudId))
        {
            throw new CliException("JiraCloudId is mandatory.");
        }

        var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{jiraUser}:{jiraToken}"));
        return new JiraApiContext(
            $"https://api.atlassian.com/ex/jira/{jiraCloudId}",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = $"Basic {auth}",
                ["Content-Type"] = "application/json"
            });
    }

    public static async Task<JiraIssueTransitionResult> InvokeIssueTransitionAsync(
        string? jiraUrl,
        string jiraCloudId,
        string jiraTicketKey,
        string jiraUser,
        string jiraToken,
        string? targetTransitionId,
        string? targetTransitionName,
        string? targetStatusId,
        string? targetStatusName)
    {
        var transitionsResult = await GetIssueTransitionsAsync(jiraCloudId, jiraTicketKey, jiraUser, jiraToken);
        if (!transitionsResult.Success)
        {
            return JiraIssueTransitionResult.Failed(
                jiraTicketKey,
                transitionsResult.TransitionUrl,
                BuildTicketUrl(jiraUrl, jiraTicketKey),
                transitionsResult.AvailableTransitions,
                transitionsResult.StatusCode,
                transitionsResult.ErrorMessage);
        }

        var targetTransition = ResolveTransition(
            transitionsResult.AvailableTransitions,
            targetTransitionId,
            targetTransitionName,
            targetStatusId,
            targetStatusName);

        if (targetTransition is null)
        {
            var targetLabel = GetTransitionSelectorSummary(targetTransitionId, targetTransitionName, targetStatusId, targetStatusName);
            var message = transitionsResult.AvailableTransitions.Count == 0
                ? $"No available transitions were returned for '{jiraTicketKey}'. Jira returns an empty transitions list when the current user cannot transition the issue or cannot access the transition. Check the release-process Jira bot permissions, issue visibility, and workflow transition restrictions."
                : $"No transition matching any provided selector ({targetLabel}) is available for '{jiraTicketKey}'.";

            return JiraIssueTransitionResult.Failed(
                jiraTicketKey,
                transitionsResult.TransitionUrl,
                BuildTicketUrl(jiraUrl, jiraTicketKey),
                transitionsResult.AvailableTransitions,
                null,
                message);
        }

        var body = new JsonObject
        {
            ["transition"] = new JsonObject
            {
                ["id"] = targetTransition.Id
            }
        };

        var postResult = await HttpSupport.PostJsonAsync(transitionsResult.TransitionUrl, transitionsResult.Headers, body);
        if (!postResult.Success)
        {
            return new JiraIssueTransitionResult(
                false,
                jiraTicketKey,
                transitionsResult.TransitionUrl,
                BuildTicketUrl(jiraUrl, jiraTicketKey),
                targetTransition,
                transitionsResult.AvailableTransitions,
                postResult.StatusCode,
                postResult.ErrorMessage);
        }

        return new JiraIssueTransitionResult(
            true,
            jiraTicketKey,
            transitionsResult.TransitionUrl,
            BuildTicketUrl(jiraUrl, jiraTicketKey),
            targetTransition,
            transitionsResult.AvailableTransitions,
            null,
            null);
    }

    public static async Task<JiraIssueFieldsResult> GetIssueFieldsAsync(
        string jiraCloudId,
        string jiraTicketKey,
        string jiraUser,
        string jiraToken,
        IReadOnlyCollection<string> fieldIds)
    {
        var resolvedFieldIds = fieldIds
            .Where(field => !string.IsNullOrWhiteSpace(field))
            .Select(field => field.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (resolvedFieldIds.Length == 0)
        {
            throw new CliException("At least one Jira field ID must be provided.");
        }

        var context = GetContext(jiraCloudId, jiraUser, jiraToken);
        var issueKey = Uri.EscapeDataString(jiraTicketKey);
        var fields = Uri.EscapeDataString(string.Join(",", resolvedFieldIds));
        var issueUrl = $"{context.BaseUrl}/rest/api/3/issue/{issueKey}?fields={fields}";

        var result = await HttpSupport.GetJsonAsync(issueUrl, context.Headers);
        if (!result.Success)
        {
            return new JiraIssueFieldsResult(false, issueUrl, null, null, result.StatusCode, result.ErrorMessage);
        }

        var fieldsNode = JsonSupport.GetProperty(result.Json, "fields");
        return new JiraIssueFieldsResult(true, issueUrl, result.Json, fieldsNode, null, null);
    }

    public static async Task<JiraTransitionsResult> GetIssueTransitionsAsync(
        string jiraCloudId,
        string jiraTicketKey,
        string jiraUser,
        string jiraToken)
    {
        var context = GetContext(jiraCloudId, jiraUser, jiraToken);
        var transitionUrl = $"{context.BaseUrl}/rest/api/3/issue/{jiraTicketKey}/transitions";
        var result = await HttpSupport.GetJsonAsync(transitionUrl, context.Headers);
        if (!result.Success)
        {
            return new JiraTransitionsResult(false, context.Headers, transitionUrl, [], result.StatusCode, result.ErrorMessage);
        }

        var transitions = new List<JiraTransition>();
        if (JsonSupport.GetProperty(result.Json, "transitions") is JsonArray array)
        {
            foreach (var item in array)
            {
                transitions.Add(new JiraTransition(
                    JsonSupport.GetString(JsonSupport.GetProperty(item, "id")),
                    JsonSupport.GetString(JsonSupport.GetProperty(item, "name")),
                    JsonSupport.GetString(JsonSupport.GetProperty(JsonSupport.GetProperty(item, "to"), "id")),
                    JsonSupport.GetString(JsonSupport.GetProperty(JsonSupport.GetProperty(item, "to"), "name")),
                    item?.DeepClone()));
            }
        }

        return new JiraTransitionsResult(true, context.Headers, transitionUrl, transitions, null, null);
    }

    public static async Task<HttpJsonResult> CreateIssueAsync(
        string jiraCloudId,
        string jiraUser,
        string jiraToken,
        JsonObject payload)
    {
        var context = GetContext(jiraCloudId, jiraUser, jiraToken);
        return await HttpSupport.PostJsonAsync($"{context.BaseUrl}/rest/api/2/issue", context.Headers, payload);
    }

    public static async Task<HttpJsonResult> LinkIssuesAsync(
        string jiraCloudId,
        string jiraUser,
        string jiraToken,
        JsonObject payload)
    {
        var context = GetContext(jiraCloudId, jiraUser, jiraToken);
        return await HttpSupport.PostJsonAsync($"{context.BaseUrl}/rest/api/2/issueLink", context.Headers, payload);
    }

    public static async Task<HttpJsonResult> UpdateIssueFieldsAsync(
        string jiraCloudId,
        string jiraTicketKey,
        string jiraUser,
        string jiraToken,
        JsonObject fields)
    {
        var context = GetContext(jiraCloudId, jiraUser, jiraToken);
        var issueKey = Uri.EscapeDataString(jiraTicketKey);
        var payload = new JsonObject { ["fields"] = fields };
        return await HttpSupport.PutJsonAsync($"{context.BaseUrl}/rest/api/2/issue/{issueKey}", context.Headers, payload);
    }

    public static async Task<HttpJsonResult> AddCommentAsync(
        string jiraCloudId,
        string jiraTicketKey,
        string jiraUser,
        string jiraToken,
        string commentBody)
    {
        var context = GetContext(jiraCloudId, jiraUser, jiraToken);
        var issueKey = Uri.EscapeDataString(jiraTicketKey);
        var payload = new JsonObject { ["body"] = commentBody };
        return await HttpSupport.PostJsonAsync($"{context.BaseUrl}/rest/api/2/issue/{issueKey}/comment", context.Headers, payload);
    }

    public static async Task<JiraIssueStatusResult> GetIssueStatusAsync(
        string jiraCloudId,
        string jiraTicketKey,
        string jiraUser,
        string jiraToken)
    {
        var fieldsResult = await GetIssueFieldsAsync(jiraCloudId, jiraTicketKey, jiraUser, jiraToken, ["status"]);
        if (!fieldsResult.Success)
        {
            return new JiraIssueStatusResult(false, fieldsResult.IssueUrl, null, null, fieldsResult.StatusCode, fieldsResult.ErrorMessage);
        }

        var statusNode = JsonSupport.GetProperty(fieldsResult.Fields, "status");
        return new JiraIssueStatusResult(
            true,
            fieldsResult.IssueUrl,
            JsonSupport.GetString(JsonSupport.GetProperty(statusNode, "id")),
            JsonSupport.GetString(JsonSupport.GetProperty(statusNode, "name")),
            null,
            null);
    }

    public static bool IsAwaitingImplementationStatus(string? statusId, string? statusName)
    {
        if (string.Equals(statusId?.Trim(), "11505", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(statusName?.Trim(), "Awaiting implementation", StringComparison.OrdinalIgnoreCase);
    }

    public static string? BuildTicketUrl(string? jiraUrl, string jiraTicketKey) =>
        string.IsNullOrWhiteSpace(jiraUrl) ? null : $"{jiraUrl.TrimEnd('/')}/browse/{jiraTicketKey}";

    public static string GetTransitionSelectorSummary(
        string? targetTransitionId,
        string? targetTransitionName,
        string? targetStatusId,
        string? targetStatusName)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(targetTransitionId))
        {
            parts.Add($"transition ID '{targetTransitionId}'");
        }

        if (!string.IsNullOrWhiteSpace(targetTransitionName))
        {
            parts.Add($"transition name '{targetTransitionName}'");
        }

        if (!string.IsNullOrWhiteSpace(targetStatusId))
        {
            parts.Add($"target status ID '{targetStatusId}'");
        }

        if (!string.IsNullOrWhiteSpace(targetStatusName))
        {
            parts.Add($"target status name '{targetStatusName}'");
        }

        return parts.Count == 0 ? "<none>" : string.Join(", ", parts);
    }

    public static void WriteAvailableTransitions(IReadOnlyCollection<JiraTransition> transitions)
    {
        if (transitions.Count == 0)
        {
            Console.WriteLine("No available transitions were returned.");
            return;
        }

        Console.WriteLine("Available transitions:");
        foreach (var transition in transitions)
        {
            Console.WriteLine($"  - ID: {transition.Id}, Name: {transition.Name}, ToStatus: {transition.ToStatusName ?? "<unknown>"} [{transition.ToStatusId ?? "<unknown>"}]");
        }
    }

    internal static JiraTransition? ResolveTransition(
        IReadOnlyCollection<JiraTransition> transitions,
        string? targetTransitionId,
        string? targetTransitionName,
        string? targetStatusId,
        string? targetStatusName)
    {
        var hasAnySelector = !(string.IsNullOrWhiteSpace(targetTransitionId) &&
            string.IsNullOrWhiteSpace(targetTransitionName) &&
            string.IsNullOrWhiteSpace(targetStatusId) &&
            string.IsNullOrWhiteSpace(targetStatusName));

        if (!hasAnySelector)
        {
            throw new CliException("At least one transition selector must be provided.");
        }

        var normalizedTransitionName = string.IsNullOrWhiteSpace(targetTransitionName) ? null : targetTransitionName.Trim();
        var normalizedStatusName = string.IsNullOrWhiteSpace(targetStatusName) ? null : targetStatusName.Trim();

        if (!string.IsNullOrWhiteSpace(targetTransitionId))
        {
            var matches = transitions.Where(t => t.Id == targetTransitionId).ToList();
            if (matches.Count == 1)
            {
                return matches[0];
            }
        }

        if (!string.IsNullOrWhiteSpace(targetStatusId))
        {
            var matches = transitions.Where(t => t.ToStatusId == targetStatusId).ToList();
            if (matches.Count == 1)
            {
                return matches[0];
            }

            if (matches.Count > 1 && !string.IsNullOrWhiteSpace(normalizedTransitionName))
            {
                var refined = matches.Where(t => string.Equals(t.Name, normalizedTransitionName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (refined.Count == 1)
                {
                    return refined[0];
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(normalizedStatusName))
        {
            var matches = transitions.Where(t => string.Equals(t.ToStatusName, normalizedStatusName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1)
            {
                return matches[0];
            }

            if (matches.Count > 1 && !string.IsNullOrWhiteSpace(normalizedTransitionName))
            {
                var refined = matches.Where(t => string.Equals(t.Name, normalizedTransitionName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (refined.Count == 1)
                {
                    return refined[0];
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(normalizedTransitionName))
        {
            var matches = transitions.Where(t => string.Equals(t.Name, normalizedTransitionName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1)
            {
                return matches[0];
            }
        }

        return null;
    }
}

