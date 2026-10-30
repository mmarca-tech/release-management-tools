using PipelineTools.ReleaseTools;

namespace PipelineTools.ReleaseTools.Tests;

public class UpdateReleasedTicketsExitCodeTests
{
    [Theory]
    [InlineData(true, true, 0)]
    [InlineData(true, false, 1)]
    [InlineData(false, false, 0)]
    public void ResolveExitCode_fails_only_on_release_ticket_failure(
        bool releaseAttempted,
        bool releaseSucceeded,
        int expected)
    {
        Assert.Equal(
            expected,
            UpdateReleasedTicketsCommand.ResolveExitCode(releaseAttempted, releaseSucceeded));
    }
}
