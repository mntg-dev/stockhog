namespace Stockhog.Domain.Optimization;

/// <summary>What an optimization maximizes.</summary>
public enum OptimizationObjective
{
    SharpeRatio,
    NetProfit,

    /// <summary>Net profit divided by maximum drawdown: rewards return earned with shallow drawdowns.</summary>
    ReturnOverDrawdown
}
