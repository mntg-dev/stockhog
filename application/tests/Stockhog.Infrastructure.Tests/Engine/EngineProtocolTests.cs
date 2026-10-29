using Stockhog.Infrastructure.Lean.Engine.Contracts;

namespace Stockhog.Infrastructure.Tests.Engine;

public class EngineProtocolTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    [InlineData(100)]
    public void Progress_round_trips(int percent)
    {
        var message = EngineProtocol.Parse(EngineProtocol.Progress(percent));

        Assert.Equal(EngineMessageKind.Progress, message.Kind);
        Assert.Equal(percent, message.Percent);
    }

    [Fact]
    public void Progress_is_clamped_to_0_100()
    {
        Assert.Equal(100, EngineProtocol.Parse(EngineProtocol.Progress(250)).Percent);
        Assert.Equal(0, EngineProtocol.Parse(EngineProtocol.Progress(-5)).Percent);
    }

    [Fact]
    public void Multi_line_messages_are_sent_as_one_line()
    {
        var line = EngineProtocol.Error("first\r\nsecond\nthird");

        Assert.DoesNotContain('\n', line);
        Assert.Equal("first second third", EngineProtocol.Parse(line).Text);
    }

    [Theory]
    [InlineData("@@emt log hello world", EngineMessageKind.Log, "hello world")]
    [InlineData("@@emt error boom", EngineMessageKind.Error, "boom")]
    [InlineData("@@emt completed 20261015_AAPL_SmaCrossStrategy", EngineMessageKind.Completed, "20261015_AAPL_SmaCrossStrategy")]
    public void Parses_protocol_commands(string line, EngineMessageKind kind, string text)
    {
        var message = EngineProtocol.Parse(line);

        Assert.Equal(kind, message.Kind);
        Assert.Equal(text, message.Text);
    }

    [Theory]
    [InlineData("20261015 21:24:22.787 TRACE:: FileSystemDataFeed.Exit(): Start.")]
    [InlineData("@@emt progress not-a-number")]
    [InlineData("@@emt unknown-command x")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_lines_are_plain_output(string? line)
    {
        Assert.Equal(EngineMessageKind.Output, EngineProtocol.Parse(line).Kind);
    }
}
