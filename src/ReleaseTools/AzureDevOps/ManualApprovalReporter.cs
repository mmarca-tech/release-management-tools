using System.Net.Mail;
using System.Text.Json.Nodes;

namespace PipelineTools.ReleaseTools;

static class ManualApprovalReporter
{
    private const string Stage = "release_process_create_ticket";
    private const string ApprovalJob = Stage + ".approve_release_ticket";

    public static async Task<string> ResolveAsync(
        string projectBaseUrl, string buildId, int stageAttempt,
        string jiraCloudId, string jiraUser, string jiraToken, string jiraProjectKey,
        Func<string, IReadOnlyDictionary<string, string>, Task<HttpJsonResult>>? getJson = null)
    {
        getJson ??= HttpSupport.GetJsonAsync;
        var token = Environment.GetEnvironmentVariable("SYSTEM_ACCESSTOKEN");
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(buildId) || stageAttempt < 1
            || string.IsNullOrWhiteSpace(jiraProjectKey))
        {
            throw new CliException("Resolving the manual approval reporter requires SYSTEM_ACCESSTOKEN, BUILD_BUILDID, a valid SYSTEM_STAGEATTEMPT and the Jira project key.");
        }

        var headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" };
        var timeline = await GetRequiredAsync(
            $"{projectBaseUrl}/_apis/build/builds/{Uri.EscapeDataString(buildId)}/timeline?api-version=7.1",
            headers, "creation approval timeline", getJson);
        var approvalId = ResolveApprovalId(timeline, stageAttempt);
        var approvals = await GetRequiredAsync(
            $"{projectBaseUrl}/_apis/pipelines/approvals?approvalIds={approvalId}&$expand=steps&api-version=7.1",
            headers, "creation approval identity", getJson);
        var email = ResolveApproverEmail(approvals, approvalId);
        var jiraContext = Jira.GetContext(jiraCloudId, jiraUser, jiraToken);
        // Assignable-user search needs only the project's Assign issues permission; global user search
        // returns an empty list unless the service account has Browse users and groups.
        var users = await GetRequiredAsync(
            $"{jiraContext.BaseUrl}/rest/api/3/user/assignable/search?project={Uri.EscapeDataString(jiraProjectKey)}&query={Uri.EscapeDataString(email)}&maxResults=1000",
            jiraContext.Headers, "Jira reporter account search", getJson);
        return ResolveJiraAccount(users, email);
    }

    internal static string ResolveApprovalId(JsonNode timeline, int stageAttempt)
    {
        var records = timeline["records"]?.AsArray()?.OfType<JsonObject>().ToArray() ?? [];
        var stages = records.Where(record => Text(record, "type") == "Stage"
            && Text(record, "identifier") == Stage
            && record["attempt"]?.GetValue<int>() == stageAttempt).ToArray();
        if (stages.Length != 1)
        {
            throw new CliException("Could not uniquely locate the current ticket-creation stage attempt.");
        }

        var stageId = Text(stages[0], "id");
        var phases = records.Where(record => Text(record, "identifier") == ApprovalJob
            && Text(record, "parentId") == stageId).ToArray();
        if (phases.Length == 0)
        {
            throw new CliException("Could not locate the ticket-creation manual approval job.");
        }

        var latestAttempt = phases.Max(record => record["attempt"]?.GetValue<int>() ?? 0);
        var currentPhases = phases.Where(record => record["attempt"]?.GetValue<int>() == latestAttempt).ToArray();
        if (currentPhases.Length != 1 || Text(currentPhases[0], "result") != "succeeded")
        {
            throw new CliException("The current ticket-creation manual approval job did not succeed uniquely.");
        }

        var phaseId = Text(currentPhases[0], "id");
        var jobs = records.Where(record => Text(record, "parentId") == phaseId
            && Text(record, "type") == "Job").Select(record => Text(record, "id")).ToHashSet();
        var tasks = records.Where(record => jobs.Contains(Text(record, "parentId"))
            && Text(record["task"], "name") == "ManualValidation"
            && record["attempt"]?.GetValue<int>() == latestAttempt).ToArray();
        if (tasks.Length != 1 || Text(tasks[0], "state") != "completed"
            || Text(tasks[0], "result") != "succeeded"
            || !Guid.TryParse(Text(tasks[0], "identifier"), out var approvalId))
        {
            throw new CliException("Could not uniquely resolve a completed ticket-creation manual validation approval.");
        }

        return approvalId.ToString();
    }

    internal static string ResolveApproverEmail(JsonNode approvals, string approvalId)
    {
        var matches = approvals["value"]?.AsArray()?.OfType<JsonObject>()
            .Where(approval => string.Equals(Text(approval, "id"), approvalId, StringComparison.OrdinalIgnoreCase))
            .ToArray() ?? [];
        if (matches.Length != 1 || Text(matches[0], "status") != "approved")
        {
            throw new CliException("The ticket-creation approval is missing, ambiguous or not approved.");
        }

        var steps = matches[0]["steps"]?.AsArray()?.OfType<JsonObject>()
            .Where(step => Text(step, "status") == "approved").ToArray() ?? [];
        var emails = steps.Select(step => Text(step["actualApprover"], "uniqueName")).ToArray();
        if (emails.Length == 0 || emails.Any(email => !MailAddress.TryCreate(email, out var address)
            || !string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase))
            || emails.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
        {
            throw new CliException("The ticket-creation approval must identify exactly one actual approver with a valid email address.");
        }

        return emails[0];
    }

    internal static string ResolveJiraAccount(JsonNode users, string email)
    {
        if (users is not JsonArray results || results.Count >= 1000)
        {
            throw new CliException("Jira reporter search returned an invalid or incomplete result set.");
        }

        var humans = results.OfType<JsonObject>().Where(user => user["active"]?.GetValue<bool>() == true
            && Text(user, "accountType") is "" or "atlassian"
            && !string.IsNullOrWhiteSpace(Text(user, "accountId"))).ToArray();
        var visibleMatches = humans
            .Where(user => string.Equals(Text(user, "emailAddress"), email, StringComparison.OrdinalIgnoreCase)).ToArray();
        // Jira Cloud omits emailAddress when the user's profile hides it, but the email query still finds the account.
        var hiddenMatches = humans.Where(user => string.IsNullOrEmpty(Text(user, "emailAddress"))).ToArray();
        var accounts = visibleMatches.Length > 0 ? visibleMatches : hiddenMatches;
        if (accounts.Length != 1)
        {
            throw new CliException($"Could not match the manual approver to exactly one active Jira account by email (search returned {results.Count} user(s), {visibleMatches.Length} with matching visible email, {hiddenMatches.Length} with hidden email). Check that the approver's Azure DevOps email matches a Jira account that is assignable in the release project and that the Jira service account has Assign issues permission there. No reporter fallback is used for manual approvals.");
        }

        return Text(accounts[0], "accountId");
    }

    private static async Task<JsonNode> GetRequiredAsync(
        string url, IReadOnlyDictionary<string, string> headers, string operation,
        Func<string, IReadOnlyDictionary<string, string>, Task<HttpJsonResult>> getJson)
    {
        var result = await getJson(url, headers);
        if (!result.Success || result.Json is null)
        {
            throw new CliException($"Failed to read {operation} (HTTP {result.StatusCode?.ToString() ?? "unknown"}). Check service-account permissions.");
        }

        return result.Json;
    }

    private static string Text(JsonNode? node, string field) => node?[field]?.GetValue<string>() ?? "";
}
