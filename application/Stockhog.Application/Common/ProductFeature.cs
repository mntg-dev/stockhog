namespace Stockhog.Application.Common;

/// <summary>
/// The features of the Pro edition. Everything else (market data, backtests, strategies, history, compare, manual paper
/// trading) is the free core. Tagging a feature here is how the app knows what a Pro licence unlocks.
/// </summary>
public enum ProductFeature
{
    Screener,
    Scanner,
    Proposals,
    Autopilot,
    Optimization,
    RiskAnalysis,
    Assistant,
    ChatAlerts
}
