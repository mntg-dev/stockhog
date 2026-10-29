namespace Stockhog.Domain.Backtesting;

public enum BacktestStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
}
