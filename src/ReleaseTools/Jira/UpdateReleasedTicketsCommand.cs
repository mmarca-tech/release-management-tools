using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PipelineTools.ReleaseTools;

static class UpdateReleasedTicketsCommand
{
    public static async Task<int> RunAsync(CliOptions options)
    {
        var jiraUrl = options.Get("JiraUrl");
        var jiraCloudId = options.Require("JiraCloudId");
        var implementedTickets = options.Get("ImplementedTickets", "")!;
        var jiraTicketKey = options.Require("JiraTicketKey");
        var jiraUser = options.Require("JiraUser");
        var jiraToken = options.Require("JiraToken");
        var postImplementationStatus = options.Get(
            "PostImplementationStatus",
            "Completed successfully via automated CI/CD deployment.")!;
        var setActualEndDate = !options.HasSwitch("SkipActualEndDate");
        var actualEndDateFieldId = options.Get("ActualEndDateFieldId", "customfield_13307")!;
        var postImplementationStatusFieldId = options.Get("PostImplementationStatusFieldId", "customfield_16629")!;

        Console.WriteLine("Updating released JIRA tickets");
        Console.WriteLine($"Implemented tickets: {implementedTickets}");

        if (string.IsNullOrWhiteSpace(jiraCloudId))
        {
            throw new CliException("JiraCloudId is mandatory.");
        }

        var tickets = Regex.Split(implementedTickets, @"[,\s]+")
            .Select(ticket => ticket.Trim())
            .Where(ticket => ticket.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var successCount = 0;
        var failCount = 0;
        var releaseTicketSucceeded = false;
        var releaseTicketAttempted = false;

        if (tickets.Length == 0)
        {
            Console.WriteLine("No implemented tickets to update. Continuing with release ticket transition.");
        }
        else
        {
            Console.WriteLine($"Found {tickets.Length} ticket(s) to transition to Done");

            foreach (var ticketKey in tickets)
            {
                Console.WriteLine();
                Console.WriteLine("----------------------------------------");
                Console.WriteLine($"Processing ticket: {ticketKey}");

                JiraIssueTransitionResult result;
                try
                {
                    result = await Jira.InvokeIssueTransitionAsync(
                        jiraUrl,
                        jiraCloudId,
                        ticketKey,
                        jiraUser,
                        jiraToken,
                        null,
                        null,
                        null,
                        "Done");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"WARNING: Unexpected error while transitioning linked JIRA ticket {ticketKey}: {ex.Message}");
                    failCount++;
                    continue;
                }

                if (result.Success)
                {
                    Console.WriteLine($"Using transition ID: {result.TransitionId}");
                    Console.WriteLine($"Successfully transitioned ticket to Done: {ticketKey}");
                    if (!string.IsNullOrWhiteSpace(result.TicketUrl))
                    {
                        Console.WriteLine($"Ticket URL: {result.TicketUrl}");
                    }

                    successCount++;
                }
                else
                {
                    Console.WriteLine($"WARNING: Failed to transition JIRA ticket: {ticketKey}. Status code: {result.StatusCode}");
                    Console.WriteLine($"WARNING: Error details: {result.ErrorMessage}");
                    Jira.WriteAvailableTransitions(result.AvailableTransitions);
                    failCount++;
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("========================================");
        Console.WriteLine("Ticket update summary:");
        Console.WriteLine($"  - Total tickets: {tickets.Length}");
        Console.WriteLine($"  - Successfully transitioned: {successCount}");
        Console.WriteLine($"  - Failed: {failCount}");
        Console.WriteLine("========================================");

        if (failCount > 0)
        {
            Console.WriteLine("WARNING: Some linked tickets failed to transition. The pipeline will not fail; please check the logs above.");
        }

        if (!string.IsNullOrWhiteSpace(jiraTicketKey))
        {
            releaseTicketAttempted = true;
            Console.WriteLine();
            Console.WriteLine("========================================");
            Console.WriteLine($"Transitioning release ticket to Completed: {jiraTicketKey}");
            Console.WriteLine("========================================");

            var fieldsToUpdate = new JsonObject();
            if (setActualEndDate)
            {
                fieldsToUpdate[actualEndDateFieldId] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            }

            if (!string.IsNullOrWhiteSpace(postImplementationStatus))
            {
                fieldsToUpdate[postImplementationStatusFieldId] = postImplementationStatus.Trim();
            }

            if (fieldsToUpdate.Count > 0)
            {
                var updateResult = await Jira.UpdateIssueFieldsAsync(
                    jiraCloudId,
                    jiraTicketKey,
                    jiraUser,
                    jiraToken,
                    fieldsToUpdate);
                if (!updateResult.Success)
                {
                    Console.WriteLine($"WARNING: Failed to update completion fields on {jiraTicketKey}: {updateResult.ErrorMessage}");
                }
            }

            var releaseResult = await Jira.InvokeIssueTransitionAsync(
                jiraUrl,
                jiraCloudId,
                jiraTicketKey,
                jiraUser,
                jiraToken,
                null,
                null,
                "11501",
                "Completed");

            if (releaseResult.Success)
            {
                Console.WriteLine($"Using transition ID: {releaseResult.TransitionId}");
                Console.WriteLine($"Successfully transitioned release ticket to Completed: {jiraTicketKey}");
                if (!string.IsNullOrWhiteSpace(releaseResult.TicketUrl))
                {
                    Console.WriteLine($"Ticket URL: {releaseResult.TicketUrl}");
                }

                var commentResult = await Jira.AddCommentAsync(
                    jiraCloudId,
                    jiraTicketKey,
                    jiraUser,
                    jiraToken,
                    "Pipeline marked the release as Completed after successful live deployment.");
                if (!commentResult.Success)
                {
                    Console.WriteLine($"WARNING: Failed to add completion comment to {jiraTicketKey}: {commentResult.ErrorMessage}");
                }

                releaseTicketSucceeded = true;
            }
            else
            {
                Console.WriteLine($"WARNING: Failed to transition release ticket: {jiraTicketKey}. Status code: {releaseResult.StatusCode}");
                Console.WriteLine($"WARNING: Error details: {releaseResult.ErrorMessage}");
                Jira.WriteAvailableTransitions(releaseResult.AvailableTransitions);
            }
        }
        else
        {
            Console.WriteLine("WARNING: No JiraTicketKey provided. Skipping release ticket transition.");
        }

        Console.WriteLine();
        var exitCode = ResolveExitCode(releaseTicketAttempted, releaseTicketSucceeded);
        if (exitCode != 0)
        {
            Console.Error.WriteLine("Ticket update completed with failures.");
            return exitCode;
        }

        Console.WriteLine("Ticket update completed.");
        return 0;
    }

    /// <summary>
    /// Fails the job only when the release ticket Completed transition was required
    /// but did not succeed. Linked ticket Done transition failures are warnings only.
    /// </summary>
    internal static int ResolveExitCode(bool releaseTicketAttempted, bool releaseTicketSucceeded)
    {
        if (releaseTicketAttempted && !releaseTicketSucceeded)
        {
            return 1;
        }

        return 0;
    }
}
