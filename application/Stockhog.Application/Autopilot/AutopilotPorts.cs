using Stockhog.Application.Abstractions.Results;
using Stockhog.Domain.Autopilot;

namespace Stockhog.Application.Autopilot;

/// <summary>Loads the one autopilot for changes, creating it on first use; the unit of work saves it.</summary>
public interface IAutopilotRepository
{
    Task<Domain.Autopilot.Autopilot> GetOrCreateAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Append-only record of the autopilot's decisions. Written in its own scope, like the audit trail, so the decisions of
/// a cycle are kept even when the cycle fails afterwards.
/// </summary>
public interface IAutopilotDecisionLog
{
    Task AppendAsync(IReadOnlyList<AutopilotDecision> decisions, CancellationToken cancellationToken = default);

    /// <returns>Newest first.</returns>
    Task<IReadOnlyList<AutopilotDecision>> ListAsync(int take, CancellationToken cancellationToken = default);
}

public static class AutopilotErrors
{
    public static Error ModeNotAvailable(AutopilotMode mode) => new(
        "Autopilot.ModeNotAvailable",
        $"{mode} mode is not available yet: run the autopilot in shadow mode and review its decisions first.",
        ErrorType.Conflict);

    public static Error Off() => new("Autopilot.Off", "The autopilot is off. Switch it to shadow mode first.", ErrorType.Conflict);

    public static Error NothingToFollow() =>
        new("Autopilot.NothingToFollow", "The autopilot follows no pairs. Add the passed pairs of a scan first.", ErrorType.Conflict);

    public static Error Invalid(string message) => Error.Validation([message]);
}
