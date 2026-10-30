using PipelineTools.ReleaseTools;

namespace PipelineTools.ReleaseTools.Tests;

public class AzurePipelinesEscapeTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("100%", "100%25")]
    [InlineData("line\r\nbreak", "line%0D%0Abreak")]
    [InlineData("msg]", "msg%5D")]
    [InlineData("a;b", "a%3Bb")]
    [InlineData("approvals]; secret", "approvals%5D%3B secret")]
    public void EscapeValue_encodes_logging_command_specials(string input, string expected)
    {
        Assert.Equal(expected, AzurePipelines.EscapeValue(input));
    }
}
