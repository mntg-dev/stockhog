namespace Stockhog.Domain.MarketData;

/// <summary>
/// Bar resolution that can be stored in the local Lean data folder.
/// </summary>
public enum Timeframe
{
    Minute,
    Hour,
    Daily
}
