using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;

namespace Stockhog.Domain.Backtesting;

public sealed record BacktestRunCompleted(BacktestRunId RunId, Symbol Symbol, StrategyName Strategy, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record BacktestRunFailed(BacktestRunId RunId, string Reason, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record BacktestRunCancelled(BacktestRunId RunId, int Progress, DateTimeOffset OccurredAt) : IDomainEvent;
