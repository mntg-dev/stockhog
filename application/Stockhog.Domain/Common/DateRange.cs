namespace Stockhog.Domain.Common;

/// <summary>
/// Inclusive range of calendar days.
/// </summary>
public sealed record DateRange
{
    private DateRange(DateOnly start, DateOnly end)
    {
        Start = start;
        End = end;
    }

    public DateOnly Start { get; }

    public DateOnly End { get; }

    public int Days => End.DayNumber - Start.DayNumber + 1;

    public static DateRange Create(DateOnly start, DateOnly end) =>
        start <= end ? new DateRange(start, end) : throw new DomainException($"Range start {start} is after its end {end}.");

    public bool Contains(DateOnly day) => day >= Start && day <= End;

    public override string ToString() => $"{Start:yyyy-MM-dd}..{End:yyyy-MM-dd}";
}
