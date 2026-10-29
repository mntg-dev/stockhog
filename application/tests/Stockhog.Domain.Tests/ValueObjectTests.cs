using Stockhog.Domain.Backtesting;
using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;

namespace Stockhog.Domain.Tests;

public class SymbolTests
{
    [Theory]
    [InlineData("aapl", "AAPL")]
    [InlineData(" msft ", "MSFT")]
    [InlineData("BRK.B", "BRK.B")]
    public void Is_normalized_to_upper_case(string input, string expected)
    {
        Assert.Equal(expected, Symbol.Create(input).Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("1ABC")]
    [InlineData("AA PL")]
    [InlineData("../etc")]
    [InlineData("TOOLONGSYMB")]
    public void Rejects_invalid_values(string? input)
    {
        Assert.False(Symbol.IsValid(input));
        Assert.Throws<DomainException>(() => Symbol.Create(input!));
    }

    [Fact]
    public void Has_value_equality()
    {
        Assert.Equal(Symbol.Create("aapl"), Symbol.Create("AAPL"));
    }
}

public class DateRangeTests
{
    [Fact]
    public void Counts_both_ends()
    {
        var range = DateRange.Create(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        Assert.Equal(31, range.Days);
        Assert.True(range.Contains(new DateOnly(2026, 1, 31)));
        Assert.False(range.Contains(new DateOnly(2026, 2, 1)));
    }

    [Fact]
    public void Single_day_is_allowed()
    {
        Assert.Equal(1, DateRange.Create(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1)).Days);
    }

    [Fact]
    public void Start_after_end_is_rejected()
    {
        Assert.Throws<DomainException>(() => DateRange.Create(new DateOnly(2026, 2, 1), new DateOnly(2026, 1, 1)));
    }
}

public class MoneyTests
{
    [Fact]
    public void Adds_and_subtracts_in_the_same_currency()
    {
        var total = Money.Usd(100).Add(Money.Usd(50.5m)).Subtract(Money.Usd(0.5m));

        Assert.Equal(Money.Usd(150), total);
    }

    [Fact]
    public void Mixing_currencies_is_rejected()
    {
        Assert.Throws<DomainException>(() => Money.Usd(1).Add(Money.Create(1, "EUR")));
    }

    [Theory]
    [InlineData("US")]
    [InlineData("US1")]
    [InlineData("")]
    public void Invalid_currency_code_is_rejected(string currency)
    {
        Assert.Throws<DomainException>(() => Money.Create(1, currency));
    }
}

public class StrategyParametersTests
{
    [Fact]
    public void Equality_ignores_insertion_order()
    {
        var a = StrategyParameters.Create([new("slow-period", "130"), new("fast-period", "26")]);
        var b = StrategyParameters.Create([new("fast-period", "26"), new("slow-period", "130")]);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal(["fast-period", "slow-period"], a.Values.Keys);
    }

    [Theory]
    [InlineData("FastPeriod")]
    [InlineData("fast period")]
    [InlineData("1fast")]
    public void Rejects_names_that_are_not_kebab_case(string name)
    {
        Assert.Throws<DomainException>(() => StrategyParameters.Create([new(name, "1")]));
    }

    [Fact]
    public void Rejects_empty_values()
    {
        Assert.Throws<DomainException>(() => StrategyParameters.Create([new("fast-period", " ")]));
    }
}

public class BacktestRunIdTests
{
    [Fact]
    public void New_id_is_readable_and_sortable()
    {
        var id = BacktestRunId.New(Symbol.Create("AAPL"), StrategyName.Create("SmaCrossStrategy"),
            new DateTimeOffset(2026, 10, 15, 23, 34, 3, 29, TimeSpan.FromHours(2)));

        Assert.Equal("20261015-233403029_AAPL_SmaCrossStrategy", id.Value);
    }

    [Theory]
    [InlineData("..\\secret")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Ids_that_are_unsafe_as_file_names_are_rejected(string value)
    {
        Assert.Throws<DomainException>(() => BacktestRunId.From(value));
    }
}
