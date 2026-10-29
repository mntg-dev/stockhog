namespace Stockhog.Strategies;

/// <summary>
/// Parameters every backtest receives from the app, in addition to the strategy's own
/// (see <see cref="Metadata.StrategyParameterAttribute"/>). Values are invariant-culture strings.
/// </summary>
public static class EngineParameters
{
    public const string DateFormat = "yyyy-MM-dd";

    public const string Symbol = "symbol";
    public const string StartDate = "start-date";
    public const string EndDate = "end-date";
    public const string Cash = "cash";
}
