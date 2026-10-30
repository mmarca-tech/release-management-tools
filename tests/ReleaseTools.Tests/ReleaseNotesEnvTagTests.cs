using PipelineTools.ReleaseTools;

namespace PipelineTools.ReleaseTools.Tests;

public class ReleaseNotesEnvTagTests
{
    [Theory]
    [InlineData("live-2026.03.23.1", "live", true)]
    [InlineData("LIVE-2026.1", "live", true)]
    [InlineData("release-live-1", "live", true)]
    [InlineData("opt-1", "pt", false)]
    [InlineData("optimization", "pt", false)]
    [InlineData("pt-2026.01.01", "pt", true)]
    [InlineData("preprod-1", "live", false)]
    [InlineData("", "live", false)]
    [InlineData("live-1", "", false)]
    public void TagMatchesEnvironment_uses_alphanumeric_segments(string tag, string env, bool expected)
    {
        Assert.Equal(expected, ReleaseNotesCommand.TagMatchesEnvironment(tag, env));
    }
}
