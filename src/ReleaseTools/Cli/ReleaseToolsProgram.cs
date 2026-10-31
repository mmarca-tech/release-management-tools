using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PipelineTools.ReleaseTools;

public static class ReleaseToolsProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            WriteHelp();
            return args.Length == 0 ? 1 : 0;
        }

        var command = args[0].Trim().ToLowerInvariant();
        var options = CliOptions.Parse(args.Skip(1));

        try
        {
            return command switch
            {
                "export-jira-secrets" => ExportJiraSecrets(options),
                "generate-release-notes" => await ReleaseNotesCommand.RunAsync(options),
                "create-release-ticket" => await CreateReleaseTicketCommand.RunAsync(options),
                "transition-jira-ticket" => await TransitionJiraTicketCommand.RunAsync(options),
                "check-jira-awaiting-implementation-approval" => await CheckApprovalGateCommand.RunAsync(options),
                "check-jira-awaiting-implementation-status" => await CheckAwaitingImplementationStatusCommand.RunAsync(options),
                "check-jira-review-acknowledgement" => await CheckReviewAcknowledgementCommand.RunAsync(options),
                "advance-jira-to-go-live-decision" => await AdvanceToGoLiveDecisionCommand.RunAsync(options),
                "update-released-tickets" => await UpdateReleasedTicketsCommand.RunAsync(options),
                "update-stakeholders" => await UpdateStakeholdersCommand.RunAsync(options),
                "validate" => await ValidateCommand.RunAsync(options),
                "validate-repository" or "detect-bump" or "create-version-tag" => await VersionTagProgram.RunAsync(args),
                _ => throw new CliException($"Unknown command '{args[0]}'.")
            };
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int ExportJiraSecrets(CliOptions options)
    {
        var jiraCloudId = options.Require("JiraCloudId");
        if (string.IsNullOrWhiteSpace(jiraCloudId))
        {
            throw new CliException("Missing required secret 'release-process-jira-cloud-id'. JiraCloudId is mandatory.");
        }

        AzurePipelines.SetOutputVariable("jiraBotEmail", options.Get("JiraUser"), isSecret: true);
        AzurePipelines.SetOutputVariable("jiraBotToken", options.Get("JiraToken"), isSecret: true);
        AzurePipelines.SetOutputVariable("jiraCloudId", jiraCloudId);
        AzurePipelines.SetOutputVariable("releaseProcessWebhookUrl", options.Get("WebhookUrl"), isSecret: true);
        return 0;
    }

    private static bool IsHelp(string value) =>
        value.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("help", StringComparison.OrdinalIgnoreCase);

    private static void WriteHelp()
    {
        Console.WriteLine("Usage: dotnet run --file ReleaseProcess.cs -- <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  create-release-ticket");
        Console.WriteLine("  generate-release-notes");
        Console.WriteLine("  transition-jira-ticket");
        Console.WriteLine("  check-jira-awaiting-implementation-approval");
        Console.WriteLine("  check-jira-awaiting-implementation-status");
        Console.WriteLine("  check-jira-review-acknowledgement");
        Console.WriteLine("  advance-jira-to-go-live-decision");
        Console.WriteLine("  update-released-tickets");
        Console.WriteLine("  update-stakeholders");
        Console.WriteLine("  export-jira-secrets");
        Console.WriteLine("  validate");
        Console.WriteLine("  validate-repository");
        Console.WriteLine("  detect-bump");
        Console.WriteLine("  create-version-tag");
        Console.WriteLine();
        Console.WriteLine("Release notes / ticket creation: -TargetCommit <commit> pins the deployed source commit.");
        Console.WriteLine("Defaults to HEAD for direct invocation; an explicit target must match the source checkout.");
        Console.WriteLine("Ticket creation: -ReporterSource ManualApproval resolves the creation approver to Jira by email.");
        Console.WriteLine("Default ReporterSource is Configured, using -Reporter or Jira's authenticated service account.");
    }
}
