namespace Stockhog.Domain.Common;

/// <summary>
/// Consistency boundary. State changes go through its methods, which record domain events for the application layer to publish.
/// </summary>
public abstract class AggregateRoot<TId>(TId id) : Entity<TId>(id), IHasDomainEvents
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];

    /// <summary>A copy: events may be raised on one thread while a unit of work dequeues them on another.</summary>
    public IReadOnlyCollection<IDomainEvent> DomainEvents
    {
        get
        {
            lock (_domainEvents)
                return _domainEvents.ToList();
        }
    }

    public IReadOnlyList<IDomainEvent> DequeueDomainEvents()
    {
        lock (_domainEvents)
        {
            var events = _domainEvents.ToList();
            _domainEvents.Clear();
            return events;
        }
    }

    protected void Raise(IDomainEvent domainEvent)
    {
        lock (_domainEvents)
            _domainEvents.Add(domainEvent);
    }
}

public interface IHasDomainEvents
{
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }

    IReadOnlyList<IDomainEvent> DequeueDomainEvents();
}
