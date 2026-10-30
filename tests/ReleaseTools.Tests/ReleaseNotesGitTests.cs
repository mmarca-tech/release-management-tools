using PipelineTools.ReleaseTools;

namespace PipelineTools.ReleaseTools.Tests;

[Collection("Release notes Git environment")]
public sealed class ReleaseNotesGitTests
{
    [Theory]
    [InlineData("main", false)]
    [InlineData("master", false)]
    [InlineData("master", true)]
    public async Task Notes_use_pinned_detached_commit_and_fetch_tags_without_main(string defaultBranch, bool shallow)
    {
        using var repo = await GitRepository.CreateAsync(defaultBranch);
        await repo.CommitAsync("baseline", "2026-01-01T00:00:00Z");
        await repo.TagAsync("live-1");
        await repo.GitAsync("switch", "-c", "release/1.0");
        await repo.CommitAsync("APP-122 first release change", "2026-01-02T00:00:00Z");
        var target = await repo.CommitAsync("APP-123 release change", "2026-01-02T12:00:00Z");
        await repo.CommitAsync("APP-456 later release change", "2026-01-03T00:00:00Z");
        await repo.GitAsync("switch", defaultBranch);
        await repo.CommitAsync("APP-789 unrelated default change", "2026-01-04T00:00:00Z");
        await repo.PublishAsync();
        var checkout = await repo.CloneAsync(shallow);
        await repo.GitAtAsync(checkout, "checkout", "--detach", target);

        var notes = await ReleaseNotesCommand.GenerateAsync("live", checkout, 0, "APP", true, target);

        Assert.Equal("Chosen env tag: live-1", notes.OutputLines[0]);
        var text = string.Join("\n", notes.OutputLines);
        Assert.Contains("[APP-122](https://your-domain.atlassian.net/browse/APP-122)", text);
        Assert.Contains("[APP-123](https://your-domain.atlassian.net/browse/APP-123)", text);
        Assert.DoesNotContain("APP-456", text);
        Assert.DoesNotContain("APP-789", text);
        Assert.DoesNotContain("- baseline", text);
        Assert.Equal(target, await repo.GitAtAsync(checkout, "rev-parse", "HEAD"));
        Assert.Equal("false", await repo.GitAtAsync(checkout, "rev-parse", "--is-shallow-repository"));
    }

    [Fact]
    public async Task Latest_annotated_tag_uses_deployment_date_not_commit_date()
    {
        using var repo = await GitRepository.CreateAsync("master");
        var first = await repo.CommitAsync("baseline one", "2026-01-01T00:00:00Z");
        await repo.CommitAsync("baseline two", "2026-01-02T00:00:00Z");
        await repo.TagAsync("live-older-deployment", "2026-01-05T00:00:00Z");
        await repo.TagAsync("live-latest-deployment", "2026-01-06T00:00:00Z", first);
        await repo.CommitAsync("release change", "2026-01-03T00:00:00Z");
        await repo.PublishAsync();
        var checkout = await repo.CloneAsync(false);

        var notes = await ReleaseNotesCommand.GenerateAsync("live", checkout, 0, "", false);

        Assert.Equal("Chosen env tag: live-latest-deployment", notes.OutputLines[0]);
        Assert.Contains("- baseline two", notes.OutputLines);
        Assert.Contains("- release change", notes.OutputLines);
    }

    [Fact]
    public async Task Latest_lightweight_tag_uses_commit_date()
    {
        using var repo = await GitRepository.CreateAsync("master");
        await repo.CommitAsync("old baseline", "2026-01-01T00:00:00Z");
        await repo.TagAsync("live-old");
        await repo.CommitAsync("new baseline", "2026-01-02T00:00:00Z");
        await repo.TagAsync("live-new");
        await repo.CommitAsync("release change", "2026-01-03T00:00:00Z");
        await repo.PublishAsync();
        var checkout = await repo.CloneAsync(false);

        var notes = await ReleaseNotesCommand.GenerateAsync("live", checkout, 0, "", false);

        Assert.Equal("Chosen env tag: live-new", notes.OutputLines[0]);
        Assert.Contains("- release change", notes.OutputLines);
        Assert.DoesNotContain("- new baseline", notes.OutputLines);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Latest_divergent_baseline_fails_instead_of_using_older_reachable_tag(bool annotated)
    {
        using var repo = await GitRepository.CreateAsync("master");
        await repo.CommitAsync("baseline", "2026-01-01T00:00:00Z");
        await repo.TagAsync("live-older", annotated ? "2026-01-02T00:00:00Z" : null);
        await repo.GitAsync("switch", "-c", "release/1.0");
        var target = await repo.CommitAsync("release change", "2026-01-03T00:00:00Z");
        await repo.GitAsync("switch", "master");
        var divergent = await repo.CommitAsync("deployed hotfix", "2026-01-04T00:00:00Z");
        await repo.TagAsync("live-latest", annotated ? "2026-01-05T00:00:00Z" : null);
        await repo.PublishAsync();
        var checkout = await repo.CloneAsync(false);
        await repo.GitAtAsync(checkout, "checkout", "--detach", target);

        var error = await Assert.ThrowsAsync<CliException>(
            () => ReleaseNotesCommand.GenerateAsync("live", checkout, 0, "", false, target));

        Assert.Contains("live-latest", error.Message);
        Assert.Contains(divergent, error.Message);
        Assert.Contains(target, error.Message);
        Assert.Contains("not an ancestor", error.Message);
    }

    [Fact]
    public async Task Equal_tag_timestamps_are_ordered_by_tag_name()
    {
        using var repo = await GitRepository.CreateAsync("master");
        await repo.CommitAsync("baseline", "2026-01-01T00:00:00Z");
        await repo.TagAsync("live-b");
        await repo.TagAsync("live-a");
        await repo.CommitAsync("release change", "2026-01-02T00:00:00Z");
        await repo.PublishAsync();
        var checkout = await repo.CloneAsync(false);

        var notes = await ReleaseNotesCommand.GenerateAsync("live", checkout, 0, "", false);

        Assert.Equal("Chosen env tag: live-a", notes.OutputLines[0]);
    }

    [Theory]
    [InlineData(null, "No tags found in repository")]
    [InlineData("pt-1", "No tags found containing 'live'")]
    public async Task Missing_environment_baseline_fails(string? tag, string message)
    {
        using var repo = await GitRepository.CreateAsync("master");
        await repo.CommitAsync("baseline", "2026-01-01T00:00:00Z");
        if (tag is not null)
        {
            await repo.TagAsync(tag);
        }
        await repo.PublishAsync();
        var checkout = await repo.CloneAsync(false);

        var error = await Assert.ThrowsAsync<CliException>(
            () => ReleaseNotesCommand.GenerateAsync("live", checkout, 0, "", false));

        Assert.Contains(message, error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_or_mismatched_target_fails(bool mismatch)
    {
        using var repo = await GitRepository.CreateAsync("master");
        var baseline = await repo.CommitAsync("baseline", "2026-01-01T00:00:00Z");
        await repo.CommitAsync("release change", "2026-01-02T00:00:00Z");
        await repo.PublishAsync();
        var checkout = await repo.CloneAsync(false);

        var error = await Assert.ThrowsAsync<CliException>(
            () => ReleaseNotesCommand.GenerateAsync("live", checkout, 0, "", false, mismatch ? baseline : "missing-target"));

        Assert.Contains(mismatch ? "does not match deployment target" : "missing-target", error.Message);
    }

    [Fact]
    public async Task Fetch_failure_blocks_even_when_local_baseline_exists()
    {
        using var repo = await GitRepository.CreateAsync("master");
        await repo.CommitAsync("baseline", "2026-01-01T00:00:00Z");
        await repo.TagAsync("live-1");
        await repo.CommitAsync("release change", "2026-01-02T00:00:00Z");
        await repo.PublishAsync();
        var checkout = await repo.CloneAsync(false);
        await repo.GitAtAsync(checkout, "fetch", "--tags", "origin");
        await repo.GitAtAsync(checkout, "remote", "set-url", "origin", Path.Combine(checkout, "missing-remote"));

        var error = await Assert.ThrowsAsync<CliException>(
            () => ReleaseNotesCommand.GenerateAsync("live", checkout, 0, "", false));

        Assert.Contains("fetch --prune origin", error.Message);
    }

    [Fact]
    public async Task No_changes_preserves_existing_output_contract()
    {
        using var repo = await GitRepository.CreateAsync("master");
        var target = await repo.CommitAsync("baseline", "2026-01-01T00:00:00Z");
        await repo.TagAsync("live-1");
        await repo.PublishAsync();
        var checkout = await repo.CloneAsync(false);

        var notes = await ReleaseNotesCommand.GenerateAsync("live", checkout, 0, "", false, target);

        Assert.Equal(0, notes.ExitCode);
        Assert.Equal($"No commits found between live-1 and {target}", Assert.Single(notes.OutputLines));
    }

    [Fact]
    public async Task Ticket_creation_validates_pinned_source_before_build_lookup_or_Jira()
    {
        using var repo = await GitRepository.CreateAsync("master");
        var baseline = await repo.CommitAsync("baseline", "2026-01-01T00:00:00Z");
        await repo.TagAsync("live-1");
        await repo.CommitAsync("release change", "2026-01-02T00:00:00Z");
        await repo.PublishAsync();
        var checkout = await repo.CloneAsync(false);
        var previousSources = Environment.GetEnvironmentVariable("BUILD_SOURCESDIRECTORY");
        try
        {
            Environment.SetEnvironmentVariable("BUILD_SOURCESDIRECTORY", checkout);
            var options = CliOptions.Parse([
                "-Environment", "live", "-JiraCloudId", "test-only", "-JiraUser", "test-only",
                "-JiraToken", "test-only", "-TargetCommit", baseline
            ]);

            var error = await Assert.ThrowsAsync<CliException>(
                () => CreateReleaseTicketCommand.RunAsync(options));

            Assert.Contains("does not match deployment target", error.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BUILD_SOURCESDIRECTORY", previousSources);
        }
    }

    private sealed class GitRepository : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"release-notes-tests-{Guid.NewGuid():N}");
        private string Source => Path.Combine(root, "source");
        private string Remote => Path.Combine(root, "remote.git");

        public static async Task<GitRepository> CreateAsync(string defaultBranch)
        {
            var repo = new GitRepository();
            Directory.CreateDirectory(repo.Source);
            await repo.GitAsync("init", "--initial-branch", defaultBranch);
            await repo.GitAsync("config", "user.name", "Release Notes Tests");
            await repo.GitAsync("config", "user.email", "release-tests@example.invalid");
            await repo.GitAsync("config", "commit.gpgsign", "false");
            await repo.GitAsync("config", "tag.gpgsign", "false");
            await repo.GitAsync("init", "--bare", "--initial-branch", defaultBranch, repo.Remote);
            await repo.GitAsync("remote", "add", "origin", repo.Remote);
            return repo;
        }

        public async Task<string> CommitAsync(string message, string date)
        {
            await ProcessRunner.RunAsync("git", ["commit", "--allow-empty", "-m", message],
                workingDirectory: Source, throwOnError: true,
                environment: new Dictionary<string, string?> { ["GIT_AUTHOR_DATE"] = date, ["GIT_COMMITTER_DATE"] = date });
            return await GitAsync("rev-parse", "HEAD");
        }

        public async Task TagAsync(string name, string? date = null, string target = "HEAD")
        {
            var args = date is null
                ? new[] { "tag", name, target }
                : new[] { "tag", "-a", name, "-m", name, target };
            await ProcessRunner.RunAsync("git", args, workingDirectory: Source, throwOnError: true,
                environment: date is null ? null : new Dictionary<string, string?> { ["GIT_COMMITTER_DATE"] = date });
        }

        public async Task PublishAsync()
        {
            await GitAsync("push", "origin", "--all");
            await GitAsync("push", "origin", "--tags");
        }

        public async Task<string> CloneAsync(bool shallow)
        {
            var checkout = Path.Combine(root, "checkout");
            var args = new List<string> { "clone", "--no-tags" };
            if (shallow)
            {
                args.AddRange(["--depth", "3", "--branch", "release/1.0"]);
            }

            args.AddRange([new Uri(Remote).AbsoluteUri, checkout]);
            await GitAsync(args.ToArray());
            return checkout;
        }

        public Task<string> GitAsync(params string[] args) => GitAtAsync(Source, args);

        public async Task<string> GitAtAsync(string directory, params string[] args)
        {
            var result = await ProcessRunner.RunAsync("git", args, workingDirectory: directory, throwOnError: true);
            return result.StandardOutput.Trim();
        }

        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(root, recursive: true);
        }
    }
}

[CollectionDefinition("Release notes Git environment", DisableParallelization = true)]
public sealed class ReleaseNotesGitEnvironmentCollection;
