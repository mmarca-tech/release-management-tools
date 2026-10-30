using PipelineTools.ReleaseTools;

namespace PipelineTools.ReleaseTools.Tests;

public class SemVerBumpTests
{
    [Theory]
    [InlineData("fix: correct exit code", "patch")]
    [InlineData("patch: bump docs only", "patch")]
    [InlineData("#patch release notes", "patch")]
    [InlineData("[patch] small fix", "patch")]
    [InlineData("semver:patch", "patch")]
    [InlineData("minor: add optional parameter", "minor")]
    [InlineData("#minor feature", "minor")]
    [InlineData("[minor] enhancement", "minor")]
    [InlineData("major: rename stage outputs", "major")]
    [InlineData("#major breaking", "major")]
    [InlineData("[major] break consumers", "major")]
    [InlineData("Merged PR 123: major: rename outputs", "major")]
    [InlineData("- minor: add SemVerBump detection", "minor")]
    [InlineData("* fix: typo in docs", "patch")]
    [InlineData("1. minor: listed squash subject", "minor")]
    [InlineData("docs: clarify major versioning policy", "")]
    [InlineData("chore: tooling update", "")]
    public void DetectInMessage_recognizes_prefixes_and_markers(string message, string expected)
    {
        Assert.Equal(expected, SemVerBump.DetectInMessage(message));
    }

    [Fact]
    public void Resolve_reads_minor_from_squash_commit_body()
    {
        var squashMessage = """
            Merged PR 146407: Harden release process and add docs-driven tests

            - minor: add SemVerBump detection from fix/patch/minor/major markers
            - minor: detect version bumps from commits since last tag
            - docs: document PR tests and manual version release flow
            """;

        Assert.Equal("minor", SemVerBump.Resolve([squashMessage], fallback: "patch"));
    }

    [Fact]
    public void Resolve_picks_highest_bump_across_commits()
    {
        var bump = SemVerBump.Resolve(
            [
                "fix: typo",
                "docs: wording",
                "minor: add parameter",
                "chore: cleanup"
            ],
            fallback: "patch");

        Assert.Equal("minor", bump);
    }

    [Fact]
    public void Resolve_prefers_major_over_minor_and_fix()
    {
        var bump = SemVerBump.Resolve(
            [
                "fix: typo",
                "minor: add parameter",
                "major: rename stage"
            ],
            fallback: "patch");

        Assert.Equal("major", bump);
    }

    [Fact]
    public void Resolve_uses_fallback_when_no_markers()
    {
        Assert.Equal("patch", SemVerBump.Resolve(["docs: update", "chore: ci"], "patch"));
        Assert.Equal("minor", SemVerBump.Resolve(["docs: update"], "minor"));
    }
}
