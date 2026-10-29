namespace Stockhog.Application.Backtesting;

public interface IBacktestEngine
{
    /// <summary>
    /// Runs a backtest to completion and stores its result under <see cref="BacktestRequest.RunId"/>.
    /// Cancelling the token stops the engine and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <exception cref="BacktestEngineException">The engine reported an error.</exception>
    Task RunAsync(
        BacktestRequest request,
        IProgress<BacktestProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class BacktestEngineException(string message) : Exception(message);
