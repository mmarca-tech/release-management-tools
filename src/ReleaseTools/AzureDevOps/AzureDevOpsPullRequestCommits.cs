using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace PipelineTools.ReleaseTools;

/// <summary>
/// Loads commit messages from the Azure DevOps pull request that produced a merge/squash commit.
/// Used so squash merges still contribute the original PR commit subjects for semver bump detection.
/// </summary>
public static class AzureDevOpsPullRequestCommits
{
    public static async Task<IReadOnlyList<string>> TryGetAssociatedCommitMessagesAsync(string? commitId = null)
    {
        var accessToken = Environment.GetEnvironmentVariable("SYSTEM_ACCESSTOKEN");
        var collectionUri = Environment.GetEnvironmentVariable("SYSTEM_TEAMFOUNDATIONCOLLECTIONURI");
        var project = Environment.GetEnvironmentVariable("SYSTEM_TEAMPROJECT");
        var repositoryId = Environment.GetEnvironmentVariable("BUILD_REPOSITORY_ID")
            ?? Environment.GetEnvironmentVariable("BUILD_REPOSITORY_NAME");
        commitId ??= Environment.GetEnvironmentVariable("BUILD_SOURCEVERSION");

        if (string.IsNullOrWhiteSpace(accessToken) ||
            string.IsNullOrWhiteSpace(collectionUri) ||
            string.IsNullOrWhiteSpace(project) ||
            string.IsNullOrWhiteSpace(repositoryId) ||
            string.IsNullOrWhiteSpace(commitId))
        {
            return [];
        }

        try
        {
            var baseUrl = collectionUri.TrimEnd('/');
            var pullRequestsUrl =
                $"{baseUrl}/{Uri.EscapeDataString(project)}/_apis/git/repositories/{Uri.EscapeDataString(repositoryId)}/commits/{Uri.EscapeDataString(commitId)}/pullRequests?api-version=7.1";

            using var client = CreateClient(accessToken);
            using var prResponse = await client.GetAsync(pullRequestsUrl);
            if (!prResponse.IsSuccessStatusCode)
            {
                Console.WriteLine($"WARNING: Could not resolve pull requests for commit {commitId}: {(int)prResponse.StatusCode} {prResponse.ReasonPhrase}");
                return [];
            }

            var prJson = JsonNode.Parse(await prResponse.Content.ReadAsStringAsync());
            var pullRequests = prJson is JsonArray array
                ? array
                : prJson?["value"] as JsonArray;

            if (pullRequests is null || pullRequests.Count == 0)
            {
                Console.WriteLine($"No pull requests associated with commit {commitId}.");
                return [];
            }

            var messages = new List<string>();
            foreach (var pullRequest in pullRequests)
            {
                var prId = pullRequest?["pullRequestId"]?.GetValue<int?>()
                    ?? pullRequest?["pullRequestId"]?.GetValue<long?>();
                var title = pullRequest?["title"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(title))
                {
                    messages.Add(title);
                }

                if (prId is null)
                {
                    continue;
                }

                var commitsUrl =
                    $"{baseUrl}/{Uri.EscapeDataString(project)}/_apis/git/repositories/{Uri.EscapeDataString(repositoryId)}/pullRequests/{prId}/commits?api-version=7.1";
                using var commitsResponse = await client.GetAsync(commitsUrl);
                if (!commitsResponse.IsSuccessStatusCode)
                {
                    Console.WriteLine($"WARNING: Could not load commits for PR {prId}: {(int)commitsResponse.StatusCode} {commitsResponse.ReasonPhrase}");
                    continue;
                }

                var commitsJson = JsonNode.Parse(await commitsResponse.Content.ReadAsStringAsync());
                var commits = commitsJson?["value"] as JsonArray ?? commitsJson as JsonArray;
                if (commits is null)
                {
                    continue;
                }

                Console.WriteLine($"Loaded {commits.Count} commit(s) from associated PR {prId} for bump detection.");
                foreach (var commit in commits)
                {
                    var comment = commit?["comment"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(comment))
                    {
                        messages.Add(comment);
                    }
                }
            }

            return messages;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WARNING: Failed to load associated pull request commits: {ex.Message}");
            return [];
        }
    }

    private static HttpClient CreateClient(string accessToken)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($":{accessToken}")));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
