namespace Stockhog.Domain.Common;

/// <summary>
/// Consistency boundary. State changes go through its methods, which record domain events for the application layer to publish.
/// </summary>
public abstract class AggregateRoot<TId>(TId id) : Entity<TId>(id), IHasDomainEvents
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    public IReadOnlyList<IDomainEvent> DequeueDomainEvents()
    {
        var events = _domainEvents.ToList();
        _domainEvents.Clear();
        return events;
    }

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);
}

public interface IHasDomainEvents
{
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }

    IReadOnlyList<IDomainEvent> DequeueDomainEvents();
}
