namespace Stockhog.Domain.Common;

/// <summary>
/// An object defined by its identity rather than its attributes.
/// </summary>
public abstract class Entity<TId> : IEquatable<Entity<TId>>
    where TId : notnull
{
    protected Entity(TId id)
    {
        Id = id;
    }

    public TId Id { get; }

    public bool Equals(Entity<TId>? other) =>
        other is not null && other.GetType() == GetType() && EqualityComparer<TId>.Default.Equals(Id, other.Id);

    public override bool Equals(object? obj) => obj is Entity<TId> other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}
