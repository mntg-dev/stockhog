namespace Stockhog.Domain.Common;

public sealed record Money
{
    public const string DefaultCurrency = "USD";

    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    /// <summary>ISO 4217 code, e.g. USD.</summary>
    public string Currency { get; }

    public static Money Create(decimal amount, string currency = DefaultCurrency)
    {
        var code = currency.Trim().ToUpperInvariant();
        if (code.Length != 3 || !code.All(char.IsAsciiLetterUpper))
            throw new DomainException($"'{currency}' is not a valid currency code.");

        return new Money(amount, code);
    }

    public static Money Usd(decimal amount) => Create(amount);

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, Currency);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount - other.Amount, Currency);
    }

    public override string ToString() => $"{Amount:0.##} {Currency}";

    private void EnsureSameCurrency(Money other)
    {
        if (other.Currency != Currency)
            throw new DomainException($"Cannot combine {Currency} with {other.Currency}.");
    }
}
