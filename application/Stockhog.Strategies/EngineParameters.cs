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

    /// <summary>Optional path of the file the strategy writes its end state to (see <see cref="StrategyState"/>).</summary>
    public const string StateFile = "state-file";

    /// <summary>
    /// "true" for a run that asks what the strategy wants to do next: at the end, bars still open (e.g. the last
    /// session's daily bar) are closed so the strategy decides on them, and orders it places then count as its intent.
    /// </summary>
    public const string SignalMode = "signal-mode";
}
