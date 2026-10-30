using PipelineTools.ReleaseTools;

namespace PipelineTools.ReleaseTools.Tests;

public class CliOptionsTests
{
    [Fact]
    public void Parse_reads_name_value_pairs()
    {
        var options = CliOptions.Parse(["-Environment", "live", "-ProjectKey", "ITSM"]);
        Assert.Equal("live", options.Get("Environment"));
        Assert.Equal("ITSM", options.Require("ProjectKey"));
    }

    [Fact]
    public void Get_with_default_when_switch_has_no_value_returns_default()
    {
        var options = CliOptions.Parse(["-Environment"]);
        Assert.True(options.HasSwitch("Environment"));
        Assert.Equal("", options.Get("Environment", ""));
    }

    [Fact]
    public void Get_missing_option_returns_default()
    {
        var options = CliOptions.Parse(["-Foo", "bar"]);
        Assert.Equal("fallback", options.Get("Missing", "fallback"));
    }

    [Fact]
    public void Require_throws_when_missing()
    {
        var options = CliOptions.Parse(["-Foo", "bar"]);
        Assert.Throws<CliException>(() => options.Require("JiraCloudId"));
    }

    [Fact]
    public void GetInt_parses_and_defaults()
    {
        var options = CliOptions.Parse(["-MaxCommits", "5"]);
        Assert.Equal(5, options.GetInt("MaxCommits", 0));
        Assert.Equal(0, options.GetInt("Other", 0));
    }

    [Fact]
    public void Unquote_strips_surrounding_quotes()
    {
        var options = CliOptions.Parse(["-Name", "'quoted'"]);
        Assert.Equal("quoted", options.Get("Name"));
    }
}
