using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Backtesting.RunBacktest;
using Stockhog.Application.Backtesting.Strategies;
using Stockhog.Application.Common;
using Stockhog.Application.MarketData.DownloadMarketData;
using Stockhog.Domain.Backtesting;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Signals;
using FluentValidation;

namespace Stockhog.Application.Signals.EvaluateSignal;

/// <summary>
/// What a strategy wants to do with a symbol today: brings the minute data up to the latest completed session, runs
/// the strategy over the last <paramref name="LookbackDays"/> up to today, and reads the position it ended with.
/// The same strategy code as every backtest decides, so a signal never disagrees with the backtest behind it.
/// </summary>
/// <param name="Parameters">Strategy inputs; missing ones use their defaults.</param>
/// <param name="FreshSessions">An entry or exit this many sessions old or newer counts as a fresh signal.</param>
public sealed record EvaluateSignalCommand(
    string StrategyName,
    string Symbol,
    IReadOnlyDictionary<string, string>? Parameters = null,
    int LookbackDays = EvaluateSignalCommand.DefaultLookbackDays,
    int FreshSessions = StrategySignal.DefaultFreshSessions) : ICommand<StrategySignal>
{
    /// <summary>A year: long enough to know since when a position has been held.</summary>
    public const int DefaultLookbackDays = 365;

    /// <summary>Data before the period for the strategies' warm-up (the longest uses a 200-day average).</summary>
    public const int WarmUpDays = 400;

    /// <summary>Only the position matters, not the account size.</summary>
    public const decimal Capital = 100_000m;
}

internal sealed class EvaluateSignalCommandValidator : AbstractValidator<EvaluateSignalCommand>
{
    public EvaluateSignalCommandValidator(IStrategyCatalog strategies)
    {
        RuleFor(c => c.StrategyName)
            .MustBeStrategyName()
            .Must(name => strategies.Find(name) is not null).WithMessage("Unknown strategy '{PropertyValue}'.");
        RuleFor(c => c.Symbol).MustBeSymbol();
        RuleFor(c => c.LookbackDays).InclusiveBetween(30, 1_000);
        RuleFor(c => c.FreshSessions).InclusiveBetween(0, 10);
    }
}

internal sealed class EvaluateSignalCommandHandler(IDispatcher dispatcher, IStrategyStateReader states, TimeProvider clock)
    : ICommandHandler<EvaluateSignalCommand, StrategySignal>
{
    public async Task<Result<StrategySignal>> HandleAsync(EvaluateSignalCommand command, CancellationToken cancellationToken)
    {
        var lastSession = ExchangeCalendar.LastCompletedSession(clock);
        var start = lastSession.AddDays(-command.LookbackDays);

        var data = await dispatcher.SendAsync(
            new DownloadMarketDataCommand(command.Symbol, Timeframe.Minute, start.AddDays(-EvaluateSignalCommand.WarmUpDays), lastSession),
            cancellationToken);
        if (data.IsFailure)
            return data.Error;

        // In signal mode the strategy also decides on the last session's close, as it would before the next open.
        var backtest = await dispatcher.SendAsync(
            new RunBacktestCommand(command.StrategyName, command.Symbol, start, lastSession.AddDays(1), EvaluateSignalCommand.Capital,
                command.Parameters ?? new Dictionary<string, string>()) { ForSignal = true },
            cancellationToken);
        if (backtest.IsFailure)
            return backtest.Error;

        var runId = BacktestRunId.From(backtest.Value.RunId);
        if (await states.ReadAsync(Symbol.Create(command.Symbol), runId, cancellationToken) is not { } state)
            return SignalErrors.NoState(runId.Value);

        var asOf = DateOnly.FromDateTime(state.LastBarTime ?? state.AsOf);
        return StrategySignal.Classify(
            asOf > lastSession ? lastSession : asOf,
            state.LastPrice,
            state.Quantity,
            state.AveragePrice,
            state.EntryTime,
            state.EntryTag,
            state.StopPrice,
            state.LastExitTime,
            state.LastExitTag,
            runId.Value,
            command.FreshSessions);
    }
}
