using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PipelineTools.ReleaseTools;

static class ReleaseNotesCommand
{
    public static async Task<int> RunAsync(CliOptions options)
    {
        var result = await GenerateAsync(
            options.Require("Env"),
            options.Get("RepoPath"),
            options.GetInt("MaxCommits", 0),
            options.Get("TicketPrefix", "")!,
            options.HasSwitch("DebugOutput"),
            options.Get("TargetCommit"));

        foreach (var line in result.OutputLines)
        {
            Console.WriteLine(line);
        }

        return result.ExitCode;
    }

    public static async Task<ReleaseNotesResult> GenerateAsync(
        string env,
        string? repoPath,
        int maxCommits,
        string ticketPrefix,
        bool debugOutput,
        string? targetCommit = null)
    {
        var gitBaseArgs = new List<string>();
        if (!string.IsNullOrWhiteSpace(repoPath))
        {
            if (!Directory.Exists(repoPath))
            {
                throw new CliException($"Repository path does not exist: {repoPath}");
            }

            Console.WriteLine($"Using repository path: {repoPath}");
            gitBaseArgs.AddRange(["-C", repoPath]);
        }

        if (debugOutput)
        {
            Console.WriteLine($"Generating release notes for env={env} at the pinned deployment commit");
        }

        var headCommit = (await RunGitAsync(gitBaseArgs, ["rev-parse", "--verify", "HEAD^{commit}"], throwOnError: true)).StandardOutput.Trim();
        var resolvedTarget = string.IsNullOrWhiteSpace(targetCommit)
            ? headCommit
            : (await RunGitAsync(gitBaseArgs, ["rev-parse", "--verify", "--end-of-options", $"{targetCommit}^{{commit}}"], throwOnError: true)).StandardOutput.Trim();
        if (!string.Equals(headCommit, resolvedTarget, StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException($"Source checkout HEAD {headCommit} does not match deployment target {resolvedTarget}. Check out the exact deployed source commit before generating release notes.");
        }

        var shallow = (await RunGitAsync(gitBaseArgs, ["rev-parse", "--is-shallow-repository"], throwOnError: true)).StandardOutput.Trim();
        var fetchArgs = new List<string> { "fetch", "--prune", "origin", resolvedTarget, "+refs/tags/*:refs/tags/*" };
        if (shallow == "true")
        {
            if (debugOutput)
            {
                Console.WriteLine("Repository is shallow; unshallowing...");
            }

            fetchArgs.Add("--unshallow");
        }
        Console.WriteLine($"Fetching deployment history and tags from origin in '{repoPath ?? Directory.GetCurrentDirectory()}' for {resolvedTarget}.");
        await RunGitAsync(gitBaseArgs, fetchArgs, throwOnError: true);

        if (debugOutput)
        {
            Console.WriteLine($"Deployment target: {resolvedTarget}");
        }

        var tagInfo = (await RunGitAsync(gitBaseArgs, ["for-each-ref", "--format=%(refname:short)|%(objectname)|%(objecttype)|%(*objectname)|%(taggerdate:unix)|%(committerdate:unix)", "refs/tags/*"], throwOnError: true)).StandardOutput;
        if (string.IsNullOrWhiteSpace(tagInfo))
        {
            throw new CliException(
                $"No tags found in repository{(string.IsNullOrWhiteSpace(repoPath) ? "" : $" at '{repoPath}'")}. " +
                $"Release notes require at least one Git tag containing the environment name '{env}'. " +
                "Seed a baseline tag on the commit currently deployed to that environment, then ensure post-live tagging pushes the next env tag.");
        }

        var candidates = new List<TagCandidate>();
        foreach (var line in Regex.Split(tagInfo, @"\r?\n"))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split('|', 6);
            if (parts.Length < 6)
            {
                continue;
            }

            var tagName = parts[0].Trim();
            if (!TagMatchesEnvironment(tagName, env))
            {
                continue;
            }

            var obj = parts[1].Trim();
            var objType = parts[2].Trim();
            var derefObj = parts[3].Trim();
            var tagDate = parts[4].Trim();
            var objDate = parts[5].Trim();

            var commitId = objType == "tag" && derefObj.Length > 0 ? derefObj : obj;
            var epochText = objType == "tag" && derefObj.Length > 0 ? tagDate : objDate;
            if (string.IsNullOrWhiteSpace(commitId))
            {
                continue;
            }

            if (!long.TryParse(epochText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
            {
                throw new CliException($"Environment tag '{tagName}' has no valid deployment/commit timestamp. Use an annotated tag pointing to a commit.");
            }
            candidates.Add(new TagCandidate(tagName, commitId, epoch));
        }

        if (candidates.Count == 0)
        {
            throw new CliException($"No tags found containing '{env}'.");
        }

        var baseline = candidates.OrderByDescending(candidate => candidate.CommitEpoch)
            .ThenBy(candidate => candidate.TagName, StringComparer.Ordinal)
            .First();
        var ancestry = await RunGitAsync(gitBaseArgs, ["merge-base", "--is-ancestor", baseline.Commit, resolvedTarget], throwOnError: false);
        if (ancestry.ExitCode == 1)
        {
            throw new CliException($"Latest environment baseline '{baseline.TagName}' ({baseline.Commit}) is not an ancestor of deployment target {resolvedTarget}. Incorporate the deployed baseline into the release branch or correct deployment tag history; an older reachable tag will not be used.");
        }
        if (ancestry.ExitCode != 0)
        {
            throw new CliException($"Could not validate environment baseline '{baseline.TagName}' against deployment target {resolvedTarget}: {ancestry.StandardError}");
        }

        if (debugOutput)
        {
            var baselineDate = DateTimeOffset.FromUnixTimeSeconds(baseline.CommitEpoch).UtcDateTime;
            Console.WriteLine($"Chosen env tag: {baseline.TagName}");
            Console.WriteLine($"Tag commit: {baseline.Commit}");
            Console.WriteLine($"Tag ordering date (UTC): {baselineDate}");
        }

        var log = await RunGitAsync(gitBaseArgs, ["log", "--no-decorate", "--pretty=format:%H|%s", $"{baseline.Commit}..{resolvedTarget}"], throwOnError: true);
        if (string.IsNullOrWhiteSpace(log.StandardOutput))
        {
            return new ReleaseNotesResult(0, [$"No commits found between {baseline.TagName} and {resolvedTarget}"]);
        }

        var commitLines = Regex.Split(log.StandardOutput, @"\r?\n")
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (debugOutput)
        {
            Console.WriteLine($"Found {commitLines.Count} commits to process");
        }

        if (maxCommits > 0 && commitLines.Count > maxCommits)
        {
            commitLines = commitLines.Skip(commitLines.Count - maxCommits).ToList();
            if (debugOutput)
            {
                Console.WriteLine($"Capped to {commitLines.Count} commits due to MaxCommits limit");
            }
        }

        var markdown = BuildMarkdown(commitLines, ticketPrefix);
        return new ReleaseNotesResult(0, [$"Chosen env tag: {baseline.TagName}", .. markdown]);
    }

    private static List<string> BuildMarkdown(IReadOnlyCollection<string> commitLines, string ticketPrefix)
    {
        var features = new List<string>();
        var bugfixes = new List<string>();
        var nugets = new List<string>();
        var terraform = new List<string>();
        var uncategorized = new List<string>();
        const string jiraBrowseBaseUrl = "https://your-domain.atlassian.net";

        foreach (var line in commitLines)
        {
            var parts = line.Split('|', 2);
            if (parts.Length < 2)
            {
                continue;
            }

            var message = parts[1].Trim();
            message = Regex.Replace(message, @"^(?i)Merged PR\s?#?\d+:\s*", "");

            if (Regex.IsMatch(message, @"^(?i)Update (dependency )?Terraform"))
            {
                terraform.Add($"- {message}");
                continue;
            }

            if (Regex.IsMatch(message, @"^(?i)Update dependency"))
            {
                nugets.Add($"- {message}");
                continue;
            }

            if (Regex.IsMatch(message, @"^(?i)Update\b"))
            {
                var words = message.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length >= 2 && !new[] { "dependency", "terraform" }.Contains(words[1].ToLowerInvariant()))
                {
                    nugets.Add($"- {message}");
                }
                else
                {
                    uncategorized.Add($"- {message}");
                }

                continue;
            }

            Match? ticketMatch = null;
            if (!string.IsNullOrWhiteSpace(ticketPrefix))
            {
                var pattern = $@"(?i)\b(({Regex.Escape(ticketPrefix)})-\d+)\b";
                ticketMatch = Regex.Match(message, pattern);
            }

            if (ticketMatch?.Success == true)
            {
                var ticket = ticketMatch.Groups[1].Value.ToUpperInvariant();
                var clean = Regex.Replace(message, Regex.Escape(ticket), "", RegexOptions.IgnoreCase).Trim();
                clean = clean.Trim(' ', ':', '-', '\u2013', '\u2014');
                var ticketReference = $"[{ticket}]({jiraBrowseBaseUrl}/browse/{ticket})";
                var entry = $"- {ticketReference}";
                if (!string.IsNullOrWhiteSpace(clean))
                {
                    entry += $" {clean}";
                }

                if (Regex.IsMatch(message, @"(?i)\b(fix|bug|repair|resolve[ds]?|invalid)\b"))
                {
                    bugfixes.Add(entry);
                }
                else
                {
                    features.Add(entry);
                }
            }
            else
            {
                uncategorized.Add($"- {message}");
            }
        }

        var lines = new List<string>();
        AddSection(lines, "**New features**", features);
        AddSection(lines, "**Bug fixes**", bugfixes);
        AddSection(lines, "**Nuget Updates**", nugets);
        AddSection(lines, "**Terraform Updates**", terraform);
        AddSection(lines, "**Uncategorized**", uncategorized);
        return lines;
    }

    private static void AddSection(List<string> lines, string heading, IReadOnlyCollection<string> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        lines.Add(heading);
        lines.AddRange(entries);
        lines.Add("");
    }

    /// <summary>
    /// Matches environment tags by whole alphanumeric segments (split on non-alphanumeric).
    /// Example: <c>live-2026.03.23.1</c> matches env <c>live</c>; <c>opt-1</c> does not match <c>pt</c>.
    /// </summary>
    internal static bool TagMatchesEnvironment(string tagName, string environment)
    {
        if (string.IsNullOrWhiteSpace(tagName) || string.IsNullOrWhiteSpace(environment))
        {
            return false;
        }

        var env = environment.Trim();
        var segments = Regex.Split(tagName, @"[^A-Za-z0-9]+")
            .Where(segment => segment.Length > 0);
        return segments.Any(segment => string.Equals(segment, env, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<ProcessResult> RunGitAsync(List<string> baseArgs, IEnumerable<string> args, bool throwOnError)
    {
        var allArgs = baseArgs.Concat(args).ToArray();
        var environment = new Dictionary<string, string?>
        {
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "safe.directory",
            ["GIT_CONFIG_VALUE_0"] = "*",
            ["GIT_TERMINAL_PROMPT"] = "0"
        };
        var systemAccessToken = Environment.GetEnvironmentVariable("SYSTEM_ACCESSTOKEN");
        if (!string.IsNullOrWhiteSpace(systemAccessToken))
        {
            environment["GIT_CONFIG_COUNT"] = "2";
            environment["GIT_CONFIG_KEY_1"] = "http.https://dev.azure.com/.extraheader";
            environment["GIT_CONFIG_VALUE_1"] = $"AUTHORIZATION: bearer {systemAccessToken}";
        }

        return await ProcessRunner.RunAsync("git", allArgs, throwOnError: throwOnError, environment: environment);
    }
}
