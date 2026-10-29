namespace Stockhog.Application.Abstractions.Persistence;

/// <summary>
/// Commits every change made in the current scope as one transaction, then publishes the domain events
/// raised by the saved aggregates. Events are published only after the commit succeeds, so their handlers
/// always see the persisted state.
/// </summary>
public interface IUnitOfWork
{
    /// <exception cref="ConcurrencyConflictException">Another scope saved one of the changed aggregates first.</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The save was based on an outdated copy of an aggregate: someone else changed it in the meantime. Nothing was saved;
/// load the aggregate again and redo the change.
/// </summary>
public sealed class ConcurrencyConflictException(string message, Exception? innerException = null) : Exception(message, innerException);
