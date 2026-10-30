namespace PipelineTools.ReleaseTools;

static class CheckAwaitingImplementationStatusCommand
{
    public static async Task<int> RunAsync(CliOptions options)
    {
        var jiraUrl = options.Get("JiraUrl");
        var jiraCloudId = options.Require("JiraCloudId");
        var jiraTicketKey = options.Require("JiraTicketKey");
        var jiraUser = options.Require("JiraUser");
        var jiraToken = options.Require("JiraToken");
        var expectedStatusId = options.Get("ExpectedStatusId", "11505")!;
        var expectedStatusName = options.Get("ExpectedStatusName", "Awaiting implementation")!;
        var setOutputVariables = options.HasSwitch("SetOutputVariables");
        var failIfBlocked = options.HasSwitch("FailIfBlocked");

        Console.WriteLine($"Checking whether Jira ticket '{jiraTicketKey}' is in '{expectedStatusName}' [{expectedStatusId}]");

        var statusResult = await Jira.GetIssueStatusAsync(jiraCloudId, jiraTicketKey, jiraUser, jiraToken);
        if (!statusResult.Success)
        {
            Console.Error.WriteLine(statusResult.ErrorMessage);
            return 1;
        }

        var isReady =
            string.Equals(statusResult.StatusId?.Trim(), expectedStatusId.Trim(), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(statusResult.StatusName?.Trim(), expectedStatusName.Trim(), StringComparison.OrdinalIgnoreCase) ||
            Jira.IsAwaitingImplementationStatus(statusResult.StatusId, statusResult.StatusName);

        var ticketUrl = Jira.BuildTicketUrl(jiraUrl, jiraTicketKey);
        var message = isReady
            ? $"Jira ticket '{jiraTicketKey}' is in '{statusResult.StatusName}' [{statusResult.StatusId}] and is ready for Implementing."
            : failIfBlocked
                ? $"Jira ticket '{jiraTicketKey}' cannot move to Implementing because manual Jira approval is missing. The ticket is currently '{statusResult.StatusName}' [{statusResult.StatusId}] instead of '{expectedStatusName}'. Complete the Go Live Decision in Jira, then retry. Ticket: {ticketUrl}"
                : $"Jira ticket '{jiraTicketKey}' is currently '{statusResult.StatusName}' [{statusResult.StatusId}] and still needs manual Jira approval before deployment. Complete the Go Live Decision in Jira; approval will move it automatically to '{expectedStatusName}'. Ticket: {ticketUrl}";

        Console.WriteLine(message);
        if (!string.IsNullOrWhiteSpace(ticketUrl))
        {
            Console.WriteLine($"Ticket URL: {ticketUrl}");
        }

        if (setOutputVariables)
        {
            AzurePipelines.SetOutputVariable("awaitingStatusReady", isReady ? "true" : "false");
            AzurePipelines.SetOutputVariable("awaitingStatusBlocking", isReady ? "false" : "true");
            AzurePipelines.SetOutputVariable("awaitingStatusMessage", message);
            AzurePipelines.SetOutputVariable("awaitingStatusId", statusResult.StatusId);
            AzurePipelines.SetOutputVariable("awaitingStatusName", statusResult.StatusName);
        }

        if (!isReady)
        {
            if (!failIfBlocked)
            {
                Console.WriteLine($"WARNING: {message}");
                return 0;
            }

            Console.Error.WriteLine(message);
            return 1;
        }

        return 0;
    }
}
