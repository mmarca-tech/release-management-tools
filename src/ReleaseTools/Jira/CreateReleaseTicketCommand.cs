using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PipelineTools.ReleaseTools;

static class CreateReleaseTicketCommand
{
    public static async Task<int> RunAsync(CliOptions options)
    {
        var environment = options.Get("Environment", "")!;
        var jiraUrl = options.Get("JiraUrl");
        var jiraCloudId = options.Require("JiraCloudId");
        var projectKey = options.Get("ProjectKey", "")!;
        var jiraUser = options.Require("JiraUser");
        var jiraToken = options.Require("JiraToken");
        var pipelineName = options.Get("PipelineName", "")!;
        var ticketPrefix = options.Get("TicketPrefix", "")!;
        var affectedServices = options.Get("AffectedServices", "")!;
        var affectedComponents = options.Get("AffectedComponents", "")!;
        var teamCmdb = options.Get("TeamCMDB", "")!;
        var assignee = options.Get("Assignee", "")!;
        var approver = options.Get("Approver", "")!;
        var reporter = options.Get("Reporter", "")!;
        var reporterSource = options.Get("ReporterSource", "Configured")!;
        if (reporterSource is not ("Configured" or "ManualApproval"))
        {
            throw new CliException("ReporterSource must be Configured or ManualApproval.");
        }
        var componentName = options.Get("ComponentName", "")!;
        var templateRepo = options.Get("TemplateRepo", "")!;
        var sourceRepoName = options.Get("SourceRepoName");
        var adoCollectionUri = options.Get("AdoCollectionUri");
        var adoProjectName = options.Get("AdoProjectName");
        var adoBuildTag = options.Get("AdoBuildTag", "liveDeployed")!;
        var jiraIssueType = options.Get("JiraIssueType", "[System] Change")!;
        var jiraPriority = options.Get("JiraPriority", "Medium")!;
        var jiraIssueLinkTypeId = options.Get("JiraIssueLinkTypeId", "10633")!;
        var changeType = options.Get("ChangeType", "Normal")!;
        var plannedStartDate = options.Get("PlannedStartDate", "")!;
        var plannedEndDate = options.Get("PlannedEndDate", "")!;
        var changeReason = options.Get("ChangeReason", "Release")!;
        var changeRisk = options.Get("ChangeRisk", "Low")!;
        var changeImpact = options.Get("ChangeImpact", "Minor / Localized")!;
        var serviceImpact = options.Get(
            "ServiceImpact",
            "No significant customer-facing impact expected during the automated CI/CD deployment window. Reviewer should update if impact differs.")!;
        var skipPostCreateTransition = options.HasSwitch("SkipReviewTransition");

        Console.WriteLine($"Creating JIRA release ticket for environment: {environment}");
        if (string.IsNullOrWhiteSpace(jiraCloudId))
        {
            throw new CliException("JiraCloudId is mandatory.");
        }

        Console.WriteLine("Generating release notes content...");
        var sourceRepoPath = ResolveSourceRepositoryPath(templateRepo, sourceRepoName);
        var releaseNotes = await ReleaseNotesCommand.GenerateAsync(
            environment, sourceRepoPath, 0, ticketPrefix, false, options.Get("TargetCommit"));

        if (releaseNotes.OutputLines.Count < 2)
        {
            throw new CliException("Failed to extract release notes content.");
        }

        var implementsIssues = ExtractImplementationTickets(releaseNotes.OutputLines, ticketPrefix);
        var latestTag = releaseNotes.OutputLines[0].Replace("Chosen env tag: ", "", StringComparison.Ordinal).Trim();
        var releaseNotesMarkdown = string.Join("\r\n", releaseNotes.OutputLines.Skip(1)).Trim();
        if (string.IsNullOrWhiteSpace(releaseNotesMarkdown))
        {
            throw new CliException("Failed to extract release notes content.");
        }

        Console.WriteLine($"Latest tag: {latestTag}");

        var jiraDescription = Regex.Replace(releaseNotesMarkdown, @"\*\*(.*?)\*\*", "h4. $1");
        if (!string.IsNullOrWhiteSpace(ticketPrefix))
        {
            var jiraTicketLinkPattern = $@"\[(({Regex.Escape(ticketPrefix)})-\d+)\]\(([^)]+)\)";
            jiraDescription = Regex.Replace(jiraDescription, jiraTicketLinkPattern, "[$1|$3]", RegexOptions.IgnoreCase);
        }

        var envUpper = environment.ToUpperInvariant();
        var hasPlannedStartOverride = !string.IsNullOrWhiteSpace(plannedStartDate);
        var hasPlannedEndOverride = !string.IsNullOrWhiteSpace(plannedEndDate);

        DateTimeOffset resolvedStartDateTimeUtc;
        DateTimeOffset resolvedEndDateTimeUtc;
        if (!hasPlannedStartOverride && !hasPlannedEndOverride)
        {
            (resolvedStartDateTimeUtc, resolvedEndDateTimeUtc) = GetDefaultPlannedWindowUtc();
        }
        else if (hasPlannedStartOverride && hasPlannedEndOverride)
        {
            var fallback = GetDefaultPlannedDateTimeUtc();
            resolvedStartDateTimeUtc = ResolvePlannedDateTimeUtc(plannedStartDate, fallback, "PlannedStartDate");
            resolvedEndDateTimeUtc = ResolvePlannedDateTimeUtc(plannedEndDate, fallback, "PlannedEndDate");
        }
        else if (hasPlannedStartOverride)
        {
            resolvedStartDateTimeUtc = ResolvePlannedDateTimeUtc(plannedStartDate, GetDefaultPlannedDateTimeUtc(), "PlannedStartDate");
            resolvedEndDateTimeUtc = resolvedStartDateTimeUtc.AddHours(1);
        }
        else
        {
            resolvedEndDateTimeUtc = ResolvePlannedDateTimeUtc(plannedEndDate, GetDefaultPlannedDateTimeUtc(), "PlannedEndDate");
            resolvedStartDateTimeUtc = resolvedEndDateTimeUtc.AddHours(-1);
        }

        if (resolvedEndDateTimeUtc < resolvedStartDateTimeUtc)
        {
            throw new CliException($"PlannedEndDate '{plannedEndDate}' cannot be earlier than PlannedStartDate '{plannedStartDate}'.");
        }

        var resolvedChangeType = ResolveChangeTypeJiraValue(changeType);
        var resolvedChangeReason = ResolveChangeReasonJiraValue(changeReason);
        var resolvedChangeRisk = ResolveChangeRiskJiraValue(changeRisk);
        var resolvedChangeImpact = ResolveChangeImpactJiraValue(changeImpact);
        var resolvedAffectedServices = ConvertToJiraFieldObjectArray(affectedServices, "id", "AffectedServices");
        var resolvedAffectedComponents = ConvertToJiraFieldObjectArray(affectedComponents, "objectId", "AffectedComponents");
        var resolvedTeamCmdb = ConvertToJiraFieldObjectArray(teamCmdb, "objectId", "TeamCMDB");
        var resolvedAssignee = ResolveRequiredString(assignee, "Assignee");
        var resolvedApprover = ResolveRequiredString(approver, "Approver");

        var startDate = resolvedStartDateTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var endDate = resolvedEndDateTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var buildId = Environment.GetEnvironmentVariable("BUILD_BUILDID") ?? "";
        var summary = $"{componentName} {envUpper} Release - from {latestTag}";

        var collectionUri = !string.IsNullOrWhiteSpace(adoCollectionUri)
            ? adoCollectionUri
            : Environment.GetEnvironmentVariable("SYSTEM_COLLECTIONURI");
        var projectName = !string.IsNullOrWhiteSpace(adoProjectName)
            ? adoProjectName
            : Environment.GetEnvironmentVariable("SYSTEM_TEAMPROJECT");

        if (string.IsNullOrWhiteSpace(collectionUri) || string.IsNullOrWhiteSpace(projectName))
        {
            throw new CliException("Unable to resolve Azure DevOps project URL. Expected AdoCollectionUri/AdoProjectName parameters or SYSTEM_COLLECTIONURI/SYSTEM_TEAMPROJECT environment variables.");
        }

        collectionUri = collectionUri.TrimEnd('/');
        var adoProjectBaseUrl = $"{collectionUri}/{Uri.EscapeDataString(projectName)}";
        if (reporterSource == "ManualApproval")
        {
            if (!int.TryParse(Environment.GetEnvironmentVariable("SYSTEM_STAGEATTEMPT"), out var stageAttempt))
            {
                throw new CliException("SYSTEM_STAGEATTEMPT is required to resolve the current manual approval reporter.");
            }

            reporter = await ManualApprovalReporter.ResolveAsync(
                adoProjectBaseUrl, buildId, stageAttempt, jiraCloudId, jiraUser, jiraToken, projectKey);
            Console.WriteLine("Resolved Jira reporter from the ticket-creation manual approver.");
        }

        var pipelineUrl = string.IsNullOrWhiteSpace(buildId)
            ? adoProjectBaseUrl
            : $"{adoProjectBaseUrl}/_build/results?buildId={buildId}";
        var implementationPlan =
            $"Automatic - CI/CD via pipeline {pipelineUrl}. Stakeholders informed via Teams release notifications. Monitor service health and key business KPIs after deployment.";

        await LogLatestBuildAsync(adoProjectBaseUrl, pipelineName, adoBuildTag);

        var ticketFields = new JsonObject
        {
            ["project"] = new JsonObject { ["key"] = projectKey },
            ["issuetype"] = new JsonObject { ["name"] = jiraIssueType },
            ["summary"] = summary,
            ["description"] = $"Planned update of {componentName} {envUpper} environment.\n\n{jiraDescription}",
            ["priority"] = new JsonObject { ["name"] = jiraPriority },
            ["customfield_13305"] = new JsonObject { ["value"] = resolvedChangeReason },
            ["customfield_13303"] = new JsonObject { ["value"] = resolvedChangeType },
            ["customfield_13304"] = new JsonObject { ["value"] = resolvedChangeRisk },
            ["customfield_13302"] = new JsonObject { ["value"] = resolvedChangeImpact },
            ["customfield_15721"] = startDate,
            ["customfield_15722"] = endDate,
            ["customfield_15716"] = resolvedAffectedServices,
            ["customfield_15998"] = resolvedAffectedComponents,
            ["customfield_15718"] = implementationPlan,
            ["customfield_15719"] = $"[Redeploy from Build {buildId}]({adoProjectBaseUrl}/_build/results?buildId={buildId})",
            ["customfield_15720"] = $"[Test Results for Build {buildId}]({adoProjectBaseUrl}/_build/results?buildId={buildId}&view=ms.vss-test-web.build-test-results-tab)",
            ["customfield_11101"] = jiraDescription,
            ["customfield_18878"] = serviceImpact.Trim(),
            ["assignee"] = CreateJiraUserReference(resolvedAssignee),
            ["customfield_13301"] = CreateJiraUserReferenceArray(resolvedApprover),
            ["customfield_16442"] = resolvedTeamCmdb
        };
        AddReporterField(ticketFields, reporter);

        var ticketPayload = new JsonObject
        {
            ["fields"] = ticketFields
        };

        Console.WriteLine("Creating JIRA issue...");
        var jiraApiBaseUrl = $"https://api.atlassian.com/ex/jira/{jiraCloudId}";
        var createIssueUrl = $"{jiraApiBaseUrl}/rest/api/2/issue";
        var jsonPayload = ticketPayload.ToJsonString(JsonSupport.Compact);
        Console.WriteLine($"JIRA API base URL: {jiraApiBaseUrl}");
        Console.WriteLine($"JIRA create issue URL: {createIssueUrl}");
        Console.WriteLine($"JIRA request context: project={projectKey}, issueType={jiraIssueType}");
        Console.WriteLine($"Resolved change metadata: changeType='{resolvedChangeType}', changeReason='{resolvedChangeReason}', changeRisk='{resolvedChangeRisk}', changeImpact='{resolvedChangeImpact}', plannedStartDate='{startDate}', plannedEndDate='{endDate}'");
        Console.WriteLine($"Resolved Jira field inputs: affectedServices={resolvedAffectedServices.Count} item(s), affectedComponents={resolvedAffectedComponents.Count} item(s), teamCMDB={resolvedTeamCmdb.Count} item(s), assignee='{resolvedAssignee}', approver='{resolvedApprover}'");
        Console.WriteLine($"JIRA issue payload: {jsonPayload}");

        var createResult = await Jira.CreateIssueAsync(jiraCloudId, jiraUser, jiraToken, ticketPayload);
        if (!createResult.Success)
        {
            Console.Error.WriteLine($"Failed to create JIRA issue: {createResult.ErrorMessage}");
            return 1;
        }

        var createdKey = JsonSupport.GetString(JsonSupport.GetProperty(createResult.Json, "key"));
        if (string.IsNullOrWhiteSpace(createdKey))
        {
            Console.Error.WriteLine("Failed to create JIRA issue: Jira response did not include an issue key.");
            return 1;
        }

        Console.WriteLine($"JIRA issue created successfully: {createdKey}");
        var jiraBrowseBaseUrl = string.IsNullOrWhiteSpace(jiraUrl) ? "" : jiraUrl.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(jiraBrowseBaseUrl))
        {
            Console.WriteLine($"URL: {jiraBrowseBaseUrl}/browse/{createdKey}");
        }

        AzurePipelines.SetOutputVariable("JiraTicketKey", createdKey);
        AzurePipelines.SetOutputVariable("JiraTicketUrl", string.IsNullOrWhiteSpace(jiraBrowseBaseUrl) ? "" : $"{jiraBrowseBaseUrl}/browse/{createdKey}");
        AzurePipelines.SetOutputVariable("PlannedStartDate", startDate);
        AzurePipelines.SetOutputVariable("PlannedEndDate", endDate);
        // Named ReleaseTag (not PackageVersion): Azure Pipelines job variables become
        // process env vars, and MSBuild treats PackageVersion as a NuGet version during dotnet run.
        AzurePipelines.SetOutputVariable("ReleaseTag", latestTag);
        AzurePipelines.SetOutputVariable("ImplementsIssues", string.Join(",", implementsIssues));

        foreach (var issueKey in implementsIssues)
        {
            var linkBody = new JsonObject
            {
                ["type"] = new JsonObject { ["id"] = jiraIssueLinkTypeId },
                ["inwardIssue"] = new JsonObject { ["key"] = createdKey },
                ["outwardIssue"] = new JsonObject { ["key"] = issueKey }
            };

            var linkResult = await Jira.LinkIssuesAsync(jiraCloudId, jiraUser, jiraToken, linkBody);
            if (linkResult.Success)
            {
                Console.WriteLine($"Linked {issueKey} to {createdKey}");
            }
            else
            {
                Console.WriteLine($"WARNING: Failed to link {issueKey} to {createdKey}: {linkResult.ErrorMessage}");
            }
        }

        var createComment = $"Pipeline created this ITSM change ticket from build {buildId} for {componentName} {envUpper} (tag {latestTag}).";
        var commentResult = await Jira.AddCommentAsync(jiraCloudId, createdKey, jiraUser, jiraToken, createComment);
        if (!commentResult.Success)
        {
            Console.WriteLine($"WARNING: Failed to add creation comment to {createdKey}: {commentResult.ErrorMessage}");
        }

        if (!skipPostCreateTransition)
        {
            await ApplyPostCreateTransitionAsync(
                jiraUrl,
                jiraCloudId,
                createdKey,
                jiraUser,
                jiraToken,
                changeType);
        }

        return 0;
    }

    private static async Task ApplyPostCreateTransitionAsync(
        string? jiraUrl,
        string jiraCloudId,
        string jiraTicketKey,
        string jiraUser,
        string jiraToken,
        string changeType)
    {
        var route = ResolvePostCreateRoute(changeType);
        foreach (var step in route.Steps)
        {
            Console.WriteLine($"Applying post-create transition for '{jiraTicketKey}' to '{step.TargetStatusName}'...");
            var transitionResult = await Jira.InvokeIssueTransitionAsync(
                jiraUrl,
                jiraCloudId,
                jiraTicketKey,
                jiraUser,
                jiraToken,
                null,
                step.TargetTransitionName,
                step.TargetStatusId,
                step.TargetStatusName);

            if (!transitionResult.Success)
            {
                Console.WriteLine($"WARNING: Failed to transition '{jiraTicketKey}' to '{step.TargetStatusName}': {transitionResult.ErrorMessage}");
                Jira.WriteAvailableTransitions(transitionResult.AvailableTransitions);
                return;
            }

            Console.WriteLine($"Transitioned '{jiraTicketKey}' to '{transitionResult.ToStatusName}' [{transitionResult.ToStatusId}]");
        }

        var commentResult = await Jira.AddCommentAsync(jiraCloudId, jiraTicketKey, jiraUser, jiraToken, route.Comment);
        if (!commentResult.Success)
        {
            Console.WriteLine($"WARNING: Failed to add post-create transition comment to {jiraTicketKey}: {commentResult.ErrorMessage}");
        }
    }

    internal static PostCreateRoute ResolvePostCreateRoute(string changeType)
    {
        var changeTypeInfo = ApprovalGate.ResolveChangeTypeInfo(new JsonObject { ["value"] = ResolveChangeTypeJiraValue(changeType) });
        if (changeTypeInfo.Category is "normal" or "major")
        {
            return new PostCreateRoute(
                [new("Review the Change", null, "Review")],
                "Pipeline moved the ticket to Review. Reviewer should complete the review and move the ticket to Planning. The pipeline will pause for a review acknowledgment, then move the ticket to Go Live Decision. The configured approver should complete the decision in Jira.");
        }

        return new PostCreateRoute(
            [new(null, "11505", "Awaiting implementation")],
            $"Change type '{changeTypeInfo.RawValue}' does not require approval. Pipeline transitioned the ticket directly to Awaiting implementation.");
    }

    internal sealed record PostCreateRoute(IReadOnlyList<PostCreateTransitionStep> Steps, string Comment);

    internal sealed record PostCreateTransitionStep(
        string? TargetTransitionName,
        string? TargetStatusId,
        string TargetStatusName);

    private static string? ResolveSourceRepositoryPath(string templateRepo, string? sourceRepoName)
    {
        var buildSourcesDirectory = Environment.GetEnvironmentVariable("BUILD_SOURCESDIRECTORY");
        if (string.IsNullOrWhiteSpace(buildSourcesDirectory) || !Directory.Exists(buildSourcesDirectory))
        {
            return null;
        }

        Console.WriteLine($"BUILD_SOURCESDIRECTORY: {buildSourcesDirectory}");
        Console.WriteLine($"Looking for SourceRepoName: {sourceRepoName}");
        Console.WriteLine("Contents of BUILD_SOURCESDIRECTORY:");
        var sourceDirs = Directory.GetDirectories(buildSourcesDirectory)
            .Select(path => new DirectoryInfo(path))
            .ToArray();
        foreach (var dir in sourceDirs)
        {
            Console.WriteLine($"  - {dir.Name}");
        }

        string? sourceRepoPath = null;
        if (!string.IsNullOrWhiteSpace(sourceRepoName))
        {
            sourceRepoPath = FindGitRepoPathFromName(buildSourcesDirectory, sourceRepoName);
            if (sourceRepoPath is null)
            {
                Console.WriteLine($"WARNING: Could not resolve SourceRepoName '{sourceRepoName}' to a checked-out git repo.");
            }
        }

        var buildRepositoryName = Environment.GetEnvironmentVariable("BUILD_REPOSITORY_NAME");
        if (sourceRepoPath is null && !string.IsNullOrWhiteSpace(buildRepositoryName))
        {
            Console.WriteLine($"Trying BUILD_REPOSITORY_NAME fallback: {buildRepositoryName}");
            sourceRepoPath = FindGitRepoPathFromName(buildSourcesDirectory, buildRepositoryName);
        }

        // checkout: self with path: s places the source repo at BUILD_SOURCESDIRECTORY root
        // (not under a subdirectory named after the repo). Prefer that layout next.
        if (sourceRepoPath is null && IsGitRepository(buildSourcesDirectory))
        {
            sourceRepoPath = buildSourcesDirectory;
            Console.WriteLine($"Using BUILD_SOURCESDIRECTORY as source repository (self checked out at sources root): {sourceRepoPath}");
        }

        if (sourceRepoPath is null)
        {
            var gitRepoDirs = sourceDirs
                .Where(dir => IsGitRepository(dir.FullName))
                .Where(dir => string.IsNullOrWhiteSpace(templateRepo) || !dir.Name.Equals(templateRepo, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (gitRepoDirs.Length == 1)
            {
                sourceRepoPath = gitRepoDirs[0].FullName;
                Console.WriteLine($"Auto-detected source repository path: {sourceRepoPath}");
            }
            else if (gitRepoDirs.Length > 1)
            {
                sourceRepoPath = gitRepoDirs[0].FullName;
                Console.WriteLine($"WARNING: Multiple git repos found. Defaulting to: {sourceRepoPath}");
            }
        }

        if (sourceRepoPath is not null)
        {
            Console.WriteLine($"Using source repository path: {sourceRepoPath}");
        }

        return sourceRepoPath;
    }

    private static bool IsGitRepository(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && (Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git")));

    private static string? FindGitRepoPathFromName(string basePath, string nameHint)
    {
        if (string.IsNullOrWhiteSpace(basePath) || !Directory.Exists(basePath) || string.IsNullOrWhiteSpace(nameHint))
        {
            return null;
        }

        var dirs = Directory.GetDirectories(basePath)
            .Select(path => new DirectoryInfo(path))
            .ToArray();
        if (dirs.Length == 0)
        {
            return null;
        }

        var candidates = new List<string>();
        var name = nameHint.Trim();
        if (name.Length > 0)
        {
            candidates.Add(name);
            if (name.Contains('/') || name.Contains('\\'))
            {
                var leaf = Regex.Split(name, @"[\\/]").LastOrDefault();
                if (!string.IsNullOrWhiteSpace(leaf))
                {
                    candidates.Add(leaf);
                }
            }
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var exactPath = Path.Combine(basePath, candidate);
            if (IsGitRepository(exactPath))
            {
                return exactPath;
            }

            foreach (var match in dirs.Where(dir => dir.Name.Contains(candidate, StringComparison.OrdinalIgnoreCase) || candidate.Contains(dir.Name, StringComparison.OrdinalIgnoreCase)))
            {
                if (IsGitRepository(match.FullName))
                {
                    return match.FullName;
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ExtractImplementationTickets(IReadOnlyList<string> releaseNotesOutput, string ticketPrefix)
    {
        if (string.IsNullOrWhiteSpace(ticketPrefix))
        {
            return [];
        }

        var pattern = $@"(?i)\b{Regex.Escape(ticketPrefix)}-\d+\b";
        return releaseNotesOutput
            .SelectMany(line => Regex.Matches(line, pattern).Select(match => match.Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static DateTimeOffset GetDefaultPlannedDateTimeUtc()
    {
        var defaultPlannedUtc = DateTimeOffset.UtcNow.AddHours(24);
        if (defaultPlannedUtc.Minute >= 30)
        {
            defaultPlannedUtc = defaultPlannedUtc.AddHours(1);
        }

        return new DateTimeOffset(
            defaultPlannedUtc.Year,
            defaultPlannedUtc.Month,
            defaultPlannedUtc.Day,
            defaultPlannedUtc.Hour,
            0,
            0,
            TimeSpan.Zero);
    }

    internal static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) GetDefaultPlannedWindowUtc()
    {
        var startUtc = GetDefaultPlannedDateTimeUtc();
        return (startUtc, startUtc.AddHours(1));
    }

    private static DateTimeOffset ResolvePlannedDateTimeUtc(string value, DateTimeOffset defaultValueUtc, string parameterName)
    {
        return ResolvePlannedDateTimeUtcCore(value, defaultValueUtc, parameterName);
    }

    internal static DateTimeOffset ResolvePlannedDateTimeUtcCore(string value, DateTimeOffset defaultValueUtc, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValueUtc.ToUniversalTime();
        }

        var trimmedValue = value.Trim();
        if (Regex.IsMatch(trimmedValue, @"^\d{4}-\d{2}-\d{2}$"))
        {
            if (DateTimeOffset.TryParseExact(
                trimmedValue,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsedDate))
            {
                return parsedDate.ToUniversalTime();
            }

            throw new CliException($"Invalid {parameterName} '{value}'. Use yyyy-MM-dd or an ISO 8601 datetime.");
        }

        if (DateTimeOffset.TryParse(
            trimmedValue,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsedDateTime))
        {
            return parsedDateTime.ToUniversalTime();
        }

        throw new CliException($"Invalid {parameterName} '{value}'. Use yyyy-MM-dd or an ISO 8601 datetime.");
    }

    internal static string ResolveChangeTypeJiraValue(string value) =>
        NormalizeRequiredChoice(
            value,
            "Normal",
            "ChangeType",
            "Allowed values: Standard, Normal, Major, Emergency.",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["standard"] = "Standard - Routine change",
                ["standard - routine change"] = "Standard - Routine change",
                ["normal"] = "Normal - Controlled change with approval",
                ["normal - controlled change with approval"] = "Normal - Controlled change with approval",
                ["major"] = "Major - requires Bank CAB",
                ["major - requires bank cab"] = "Major - requires Bank CAB",
                ["emergency"] = "Emergency"
            });

    internal static string ResolveChangeReasonJiraValue(string value) =>
        NormalizeRequiredChoice(
            value,
            "Release",
            "ChangeReason",
            "Allowed values: Release, Maintenance, Upgrade, Fix via SQL script, 3rd Party release/maintenance, Other.",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["release"] = "Release",
                ["maintenance"] = "Maintenance",
                ["upgrade"] = "Upgrade",
                ["fix via sql script"] = "Fix via SQL script",
                ["3rd party release/maintenance"] = "3rd Party release/maintenance",
                ["other"] = "Other"
            });

    internal static string ResolveChangeRiskJiraValue(string value) =>
        NormalizeRequiredChoice(
            value,
            "Low",
            "ChangeRisk",
            "Allowed values: Critical, High, Medium, Low.",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["critical"] = "Critical",
                ["high"] = "High",
                ["medium"] = "Medium",
                ["low"] = "Low"
            });

    internal static string ResolveChangeImpactJiraValue(string value) =>
        NormalizeRequiredChoice(
            value,
            "Minor / Localized",
            "ChangeImpact",
            "Allowed values: Extensive / Widespread, Significant / Large, Moderate / Limited, Minor / Localized.",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["extensive / widespread"] = "Extensive / Widespread",
                ["significant / large"] = "Significant / Large",
                ["moderate / limited"] = "Moderate / Limited",
                ["minor / localized"] = "Minor / Localized"
            });

    private static string NormalizeRequiredChoice(
        string value,
        string defaultValue,
        string parameterName,
        string allowedMessage,
        IReadOnlyDictionary<string, string> choices)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
        if (choices.TryGetValue(normalized, out var mapped))
        {
            return mapped;
        }

        throw new CliException($"Unsupported {parameterName} '{value}'. {allowedMessage}");
    }

    private static string ResolveRequiredString(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CliException($"Missing required parameter '{parameterName}'.");
        }

        return value.Trim();
    }

    internal static JsonObject CreateJiraUserReference(string accountId) =>
        new() { ["id"] = accountId };

    internal static JsonArray CreateJiraUserReferenceArray(string accountId) =>
        [CreateJiraUserReference(accountId)];

    internal static void AddReporterField(JsonObject ticketFields, string reporter)
    {
        if (!string.IsNullOrWhiteSpace(reporter))
        {
            ticketFields["reporter"] = new JsonObject { ["id"] = reporter.Trim() };
        }
    }

    private static JsonArray ConvertToJiraFieldObjectArray(string value, string propertyName, string parameterName)
    {
        var resolved = ResolveRequiredString(value, parameterName);
        var items = resolved.Split(',')
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .ToArray();

        if (items.Length == 0)
        {
            throw new CliException($"Parameter '{parameterName}' must contain at least one value.");
        }

        var result = new JsonArray();
        foreach (var item in items)
        {
            result.Add(new JsonObject
            {
                [propertyName] = item
            });
        }

        return result;
    }

    private static async Task LogLatestBuildAsync(string adoProjectBaseUrl, string pipelineName, string adoBuildTag)
    {
        var buildTagLabel = string.IsNullOrWhiteSpace(adoBuildTag) ? "<none>" : adoBuildTag;
        Console.WriteLine($"Getting latest build from '{pipelineName}' with tag filter '{buildTagLabel}'...");

        try
        {
            var systemAccessToken = Environment.GetEnvironmentVariable("SYSTEM_ACCESSTOKEN");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = $"Bearer {systemAccessToken}",
                ["Content-Type"] = "application/json"
            };

            var pipelineNameEncoded = Uri.EscapeDataString(pipelineName);
            var pipelineUrl = $"{adoProjectBaseUrl}/_apis/build/definitions?name={pipelineNameEncoded}&api-version=7.0";
            var pipelineResponse = await HttpSupport.GetJsonAsync(pipelineUrl, headers);
            var definitions = JsonSupport.GetProperty(pipelineResponse.Json, "value") as JsonArray;
            if (pipelineResponse.Success && definitions is not null && definitions.Count > 0)
            {
                var definitionId = JsonSupport.GetString(JsonSupport.GetProperty(definitions[0], "id"));
                if (!string.IsNullOrWhiteSpace(definitionId))
                {
                    var apiUrl = string.IsNullOrWhiteSpace(adoBuildTag)
                        ? $"{adoProjectBaseUrl}/_apis/build/builds?definitions={definitionId}&$top=1&api-version=7.0"
                        : $"{adoProjectBaseUrl}/_apis/build/builds?definitions={definitionId}&tagFilters={Uri.EscapeDataString(adoBuildTag)}&$top=1&api-version=7.0";

                    var buildsResponse = await HttpSupport.GetJsonAsync(apiUrl, headers);
                    var builds = JsonSupport.GetProperty(buildsResponse.Json, "value") as JsonArray;
                    if (buildsResponse.Success && builds is not null && builds.Count > 0)
                    {
                        var buildId = JsonSupport.GetString(JsonSupport.GetProperty(builds[0], "id"));
                        Console.WriteLine($"Found latest build: {buildId}");
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WARNING: Failed to get latest build: {ex.Message}");
            return;
        }

        Console.WriteLine("WARNING: Failed to get latest build: notFound");
    }
}
