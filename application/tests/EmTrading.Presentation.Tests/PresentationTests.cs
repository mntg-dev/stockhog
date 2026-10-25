using EmTrading.Presentation.Localization;
using EmTrading.Presentation.Trading;
using EmTrading.Domain.Trading;
using EmTrading.Application.Trading.Orders;
using EmTrading.Application.Backtesting.Strategies;
using EmTrading.Application.Optimization.StartOptimization;
using EmTrading.Presentation.Backtesting;
using EmTrading.Presentation.Common;
using EmTrading.Presentation.Logging;
using EmTrading.Presentation.Optimization;
using EmTrading.Presentation.Strategies;

namespace EmTrading.Presentation.Tests;

public class ThrottledActionTests
{
    [Fact]
    public async Task A_burst_of_requests_from_many_threads_runs_the_action_once()
    {
        var runs = 0;
        var throttle = new ThrottledAction(TimeSpan.FromMilliseconds(100), () => Interlocked.Increment(ref runs), uiContext: null);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
                throttle.Request();
        })));
        await Task.Delay(400, TestContext.Current.CancellationToken);

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task A_request_after_the_interval_runs_again()
    {
        var runs = 0;
        var throttle = new ThrottledAction(TimeSpan.FromMilliseconds(50), () => Interlocked.Increment(ref runs), uiContext: null);

        throttle.Request();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        throttle.Request();
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.Equal(2, runs);
    }
}

public class LogFeedTests
{
    [Fact]
    public void Keeps_only_the_newest_entries()
    {
        var feed = new LogFeed();
        for (var i = 0; i < LogFeed.Capacity + 10; i++)
            feed.Append(new LogEntry(DateTimeOffset.UtcNow, LogLevel.Information, "Test", $"line {i}"));

        var entries = feed.Snapshot();

        Assert.Equal(LogFeed.Capacity, entries.Count);
        Assert.Equal("line 10", entries[0].Message);
        Assert.Equal($"line {LogFeed.Capacity + 9}", entries[^1].Message);
    }

    [Fact]
    public async Task Appending_from_many_threads_loses_nothing()
    {
        var feed = new LogFeed();
        var notifications = 0;
        feed.Appended += (_, _) => Interlocked.Increment(ref notifications);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 250; i++)
                feed.Append(new LogEntry(DateTimeOffset.UtcNow, LogLevel.Debug, $"T{t}", "x"));
        })));

        Assert.Equal(1000, feed.Snapshot().Count);
        Assert.Equal(1000, notifications);
    }
}

[Collection(nameof(CultureCollection))]
public class StrategyParameterInputTests
{
    /// <summary>Has no resources, so its texts are the attribute texts in every language.</summary>
    private const string TestStrategy = "TestStrategy";

    private static readonly StrategyParameterDescriptor Period =
        new("fast-period", "Fast EMA period", StrategyParameterKind.Integer, 26, 2, 500, null, "In 15-minute bars.");

    [Fact]
    public void Starts_with_the_default_and_no_error()
    {
        var input = new StrategyParameterInput(TestStrategy, Period);

        Assert.Equal("26", input.Value);
        Assert.Null(input.Error);
    }

    [Theory]
    [InlineData("1", "Fast EMA period must be at least 2.")]
    [InlineData("2.5", "Fast EMA period must be a whole number.")]
    [InlineData("x", "Fast EMA period: 'x' is not a number.")]
    public void Shows_the_validation_message_as_soon_as_the_value_changes(string value, string expected)
    {
        var input = new StrategyParameterInput(TestStrategy, Period);
        var errorChanged = false;
        input.PropertyChanged += (_, e) => errorChanged |= e.PropertyName == nameof(StrategyParameterInput.Error);

        input.Value = value;

        Assert.Equal(expected, input.Error);
        Assert.True(errorChanged);
    }

    [Fact]
    public void Hint_combines_description_and_range()
    {
        Assert.Contains("In 15-minute bars.", new StrategyParameterInput(TestStrategy, Period).Hint);
        Assert.Contains("2 – 500", new StrategyParameterInput(TestStrategy, Period).Hint);
    }
}

[Collection(nameof(CultureCollection))]
public sealed class StrategyTextsTests : IDisposable
{
    private static readonly StrategyParameterDescriptor FastPeriod =
        new("fast-period", "Fast EMA period", StrategyParameterKind.Integer, 26, 2, 500, null, "In 15-minute bars.");

    private static readonly StrategyDescriptor SmaCross =
        new("SmaCrossStrategy", "EMA Cross + ADX trend", "English description.", [FastPeriod]);

    private readonly System.Globalization.CultureInfo _original = Loc.Instance.Culture;

    [Fact]
    public void Parameter_labels_follow_the_language()
    {
        var input = new StrategyParameterInput("SmaCrossStrategy", FastPeriod);
        var optimization = new OptimizationParameterInput("SmaCrossStrategy", FastPeriod);

        Loc.Instance.SetCulture(Loc.Polish);
        Assert.Equal("Okres szybkiej EMA", input.DisplayName);
        Assert.Contains("W świecach 15-minutowych.", input.Hint);
        Assert.Equal(("Okres szybkiej EMA", "Fast EMA period"), (optimization.DisplayName, optimization.AutomationName));

        Loc.Instance.SetCulture(Loc.English);
        Assert.Equal("Fast EMA period", input.DisplayName);
        Assert.Contains("In 15-minute bars.", input.Hint);
    }

    [Fact]
    public void Strategy_names_are_translated_by_descriptor_or_class_name()
    {
        var converter = new StrategyTextConverter();
        Loc.Instance.SetCulture(Loc.Polish);

        Assert.Equal("Przecięcie EMA + trend ADX", converter.Convert(SmaCross, typeof(string), null, Loc.Polish));
        Assert.Equal("Przecięcie EMA + trend ADX", converter.Convert("SmaCrossStrategy", typeof(string), null, Loc.Polish));
        Assert.StartsWith("Przecięcie EMA na 15-minutowych", (string?)converter.Convert(SmaCross, typeof(string), "Description", Loc.Polish));
    }

    [Fact]
    public void Test_results_are_shown_only_for_strategies_that_have_them()
    {
        Loc.Instance.SetCulture(Loc.Polish);

        Assert.Equal("TestResults", StrategiesViewModel.DocSectionsOf("RsiPullbackStrategy")[^1].Section);
        Assert.Equal("Wyniki testów", Loc.Get(StrategiesViewModel.DocSectionsOf("RsiPullbackStrategy")[^1].TitleKey));
        Assert.Equal(StrategiesViewModel.DocSections, StrategiesViewModel.DocSectionsOf("RsiMeanReversionStrategy").Select(s => s.Section));
    }

    [Fact]
    public void A_strategy_without_resources_shows_its_attribute_texts()
    {
        Loc.Instance.SetCulture(Loc.Polish);
        var unknown = SmaCross with { Name = "UnknownStrategy" };

        Assert.Equal("EMA Cross + ADX trend", StrategyTexts.Name(unknown));
        Assert.Equal("English description.", StrategyTexts.Description(unknown));
        Assert.Equal("UnknownStrategy", StrategyTexts.Name("UnknownStrategy"));
        Assert.Equal("Fast EMA period", StrategyTexts.ParameterName("UnknownStrategy", FastPeriod));
    }

    public void Dispose() => Loc.Instance.SetCulture(_original);
}

[Collection(nameof(CultureCollection))]
public class OptimizationParameterInputTests
{
    private const string TestStrategy = "TestStrategy";

    private static readonly StrategyParameterDescriptor Period =
        new("fast-period", "Fast EMA period", StrategyParameterKind.Integer, 26, 2, 500, null, null);

    private static readonly StrategyParameterDescriptor Risk =
        new("risk-percent", "Risk per trade (%)", StrategyParameterKind.Decimal, 1, 0.1m, 10, null, null);

    [Fact]
    public void Starts_fixed_at_the_default_with_a_five_value_range_ready()
    {
        var input = new OptimizationParameterInput(TestStrategy, Period);

        Assert.False(input.IsVaried);
        Assert.Equal("26", input.Value);
        Assert.Equal(1, input.Count);
        Assert.False(input.TryGetRange(out _));

        input.IsVaried = true;

        Assert.Null(input.Error);
        Assert.Equal(("26", "46", "5"), (input.From, input.To, input.Step));
        Assert.Equal(5, input.Count);
        Assert.True(input.TryGetRange(out var range));
        Assert.Equal(new ParameterRangeInput("fast-period", 26, 46, 5), range);
    }

    [Fact]
    public void Decimal_parameters_get_a_decimal_step()
    {
        var input = new OptimizationParameterInput(TestStrategy, Risk) { IsVaried = true };

        Assert.Equal(("1", "1.8", "0.2"), (input.From, input.To, input.Step));
        Assert.Equal(5, input.Count);
    }

    [Theory]
    [InlineData("10", "20", "0")]
    [InlineData("20", "10", "1")]
    [InlineData("10", "20", "2.5")]
    [InlineData("2", "500", "1")]
    [InlineData("1", "20", "1")]
    [InlineData("x", "20", "1")]
    public void Invalid_ranges_show_an_error_and_count_zero(string from, string to, string step)
    {
        var input = new OptimizationParameterInput(TestStrategy, Period) { IsVaried = true, From = from, To = to, Step = step };

        Assert.NotNull(input.Error);
        Assert.Equal(0, input.Count);
        Assert.False(input.TryGetRange(out _));
    }

    [Fact]
    public void Changing_a_bound_notifies_count_and_error()
    {
        var input = new OptimizationParameterInput(TestStrategy, Period) { IsVaried = true };
        var changed = new List<string?>();
        input.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        input.To = "76";

        Assert.Contains(nameof(OptimizationParameterInput.Count), changed);
        Assert.Contains(nameof(OptimizationParameterInput.Error), changed);
        Assert.Equal(11, input.Count);
    }
}

[Collection(nameof(CultureCollection))]
public class OrderTicketTests
{
    [Fact]
    public void Shows_only_the_prices_the_order_type_uses()
    {
        var ticket = new OrderTicket();

        Assert.False(ticket.HasLimitPrice || ticket.HasStopPrice);
        ticket.Type = OrderType.StopLimit;
        Assert.True(ticket.HasLimitPrice && ticket.HasStopPrice);
        ticket.Type = OrderType.Stop;
        Assert.Equal((false, true), (ticket.HasLimitPrice, ticket.HasStopPrice));
    }

    [Fact]
    public void Creates_a_command_with_only_the_relevant_prices()
    {
        var ticket = new OrderTicket { Symbol = " msft ", Side = OrderSide.Sell, Type = OrderType.Limit, Quantity = "5", LimitPrice = "410,5", StopPrice = "999", TimeInForce = TimeInForce.Gtc };

        Assert.True(ticket.TryCreateCommand(out var command));
        Assert.Equal(new PlaceOrderCommand("MSFT", OrderSide.Sell, OrderType.Limit, 5, 410.5m, null, TimeInForce.Gtc), command);
    }

    [Theory]
    [InlineData("AAPL", "0", OrderType.Market, "", TimeInForce.Day)]
    [InlineData("AAPL", "x", OrderType.Market, "", TimeInForce.Day)]
    [InlineData("AAPL", "1.5", OrderType.Limit, "100", TimeInForce.Day)]
    [InlineData("AAPL", "1.5", OrderType.Market, "", TimeInForce.Gtc)]
    [InlineData("AAPL", "1", OrderType.Limit, "", TimeInForce.Day)]
    [InlineData("??", "1", OrderType.Market, "", TimeInForce.Day)]
    public void Invalid_inputs_are_reported_and_create_no_command(string symbol, string quantity, OrderType type, string limit, TimeInForce timeInForce)
    {
        var ticket = new OrderTicket { Symbol = symbol, Quantity = quantity, Type = type, LimitPrice = limit, TimeInForce = timeInForce };

        Assert.NotNull(ticket.Error);
        Assert.False(ticket.TryCreateCommand(out _));
    }

    [Fact]
    public void Estimates_the_value_from_the_limit_or_the_live_price()
    {
        var ticket = new OrderTicket { Quantity = "0.5" };
        Assert.Null(ticket.EstimatedValue);

        ticket.LivePrice = 200.333m;
        Assert.Equal(100.17m, ticket.EstimatedValue);

        ticket.Type = OrderType.Limit;
        ticket.Quantity = "3";
        ticket.LimitPrice = "150";
        Assert.Equal(450m, ticket.EstimatedValue);
    }

    [Fact]
    public void Describes_the_order_for_the_confirmation()
    {
        var previous = Loc.Instance.Culture;
        Loc.Instance.SetCulture(Loc.English);
        try
        {
            var ticket = new OrderTicket { Symbol = "AAPL", Type = OrderType.Limit, Quantity = "10", LimitPrice = "180" };
            ticket.TryCreateCommand(out var command);

            var text = ticket.Describe(command!);

            Assert.StartsWith("Buy 10 AAPL, Limit (limit 180.00), Day.", text);
            Assert.Contains("1,800.00 USD", text);
        }
        finally
        {
            Loc.Instance.SetCulture(previous);
        }
    }
}
