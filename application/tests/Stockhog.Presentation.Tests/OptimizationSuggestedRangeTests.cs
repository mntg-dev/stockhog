using Stockhog.Application.Backtesting.Strategies;
using Stockhog.Presentation.Optimization;

namespace Stockhog.Presentation.Tests;

public class OptimizationSuggestedRangeTests
{
    [Fact]
    public void Suggested_range_fills_the_form()
    {
        var input = new OptimizationParameterInput("TestStrategy", Parameter(new SuggestedRange(10, 55, 5)));

        Assert.Equal(("20", "10", "55", "5"), (input.Value, input.From, input.To, input.Step));
        input.IsVaried = true;
        Assert.Equal(10, input.Count);
    }

    [Fact]
    public void Without_a_suggestion_the_range_starts_at_the_default()
    {
        var input = new OptimizationParameterInput("TestStrategy", Parameter(null));

        Assert.Equal(("20", "20", "36", "4"), (input.Value, input.From, input.To, input.Step));
    }

    private static StrategyParameterDescriptor Parameter(SuggestedRange? suggested) =>
        new("entry-period", "Entry channel", StrategyParameterKind.Integer, 20, 5, 250, null, null, suggested);
}
