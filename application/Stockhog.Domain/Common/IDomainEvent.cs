namespace Stockhog.Domain.Common;

/// <summary>
/// Something that happened in the domain that other parts of the system may react to.
/// </summary>
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}
