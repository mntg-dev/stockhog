using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Common;
using Stockhog.Domain.MarketData;
using FluentValidation;

namespace Stockhog.Application.Trading;

/// <summary>The latest trade price of each symbol, from live quotes when streamed, otherwise from the broker.</summary>
public sealed record GetLatestPricesQuery(IReadOnlyList<string> Symbols) : IQuery<IReadOnlyList<LatestPrice>>
{
    public const int MaxSymbols = 30;
}

/// <param name="Price">The last trade price, or <c>null</c> if none is available (e.g. unknown symbol).</param>
public sealed record LatestPrice(string Symbol, decimal? Price);

internal sealed class GetLatestPricesQueryValidator : AbstractValidator<GetLatestPricesQuery>
{
    public GetLatestPricesQueryValidator()
    {
        RuleFor(q => q.Symbols)
            .NotEmpty().WithMessage("Enter at least one symbol.")
            .Must(s => s.Count <= GetLatestPricesQuery.MaxSymbols)
            .WithMessage($"At most {GetLatestPricesQuery.MaxSymbols} symbols at once.");
        RuleForEach(q => q.Symbols).MustBeSymbol();
    }
}

internal sealed class GetLatestPricesQueryHandler(IQuoteCache quotes, ILatestPriceSource prices)
    : IQueryHandler<GetLatestPricesQuery, IReadOnlyList<LatestPrice>>
{
    public async Task<Result<IReadOnlyList<LatestPrice>>> HandleAsync(GetLatestPricesQuery query, CancellationToken cancellationToken)
    {
        var symbols = query.Symbols.Select(Symbol.Create).Distinct().ToList();
        var results = await Task.WhenAll(symbols.Select(async symbol =>
            new LatestPrice(
                symbol.Value,
                quotes.TryGet(symbol, out var quote) && quote.Price is { } streamed
                    ? streamed
                    : await prices.GetLatestPriceAsync(symbol, cancellationToken))));

        return results;
    }
}
