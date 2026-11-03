using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using Stockhog.Application.Trading.Safety;
using FluentValidation;

namespace Stockhog.Application.Brokers;

/// <summary>
/// The broker keys for market data and the paper account. Live keys are deliberately not part of this: they stay in
/// configuration, behind the live opt-in.
/// </summary>
/// <param name="PaperApiKey">Keys of the paper trading account when they differ from the market data keys.</param>
public sealed record BrokerKeys(string ApiKey, string ApiSecret, string? PaperApiKey = null, string? PaperApiSecret = null)
{
    /// <summary>Never print the secrets, e.g. in logs or exceptions.</summary>
    public override string ToString() => $"BrokerKeys({BrokerKeyStatus.Mask(ApiKey)})";
}

public enum BrokerKeySource
{
    None,

    /// <summary>appsettings, user secrets or environment variables.</summary>
    Configuration,

    /// <summary>Entered on the Settings page and stored encrypted for this Windows user.</summary>
    Settings
}

/// <summary>Keeps the keys entered on the Settings page, encrypted for the current Windows user.</summary>
public interface IBrokerKeyStore
{
    /// <summary>Where the keys the app runs with came from; they are read once at startup.</summary>
    BrokerKeySource SourceInUse { get; }

    /// <summary>The market data key the app runs with, or null.</summary>
    string? KeyInUse { get; }

    BrokerKeys? Stored { get; }

    /// <summary>Stores the keys, or deletes them when null. They are used from the next start.</summary>
    void Save(BrokerKeys? keys);
}

/// <summary>Tries keys against the broker without storing them. Only the market data and paper APIs are called.</summary>
public interface IBrokerKeyTester
{
    Task<BrokerKeyCheck> TestAsync(BrokerKeys keys, CancellationToken cancellationToken = default);
}

/// <param name="DataError">Why market data could not be read; null when it worked.</param>
/// <param name="TradingError">Why the paper account could not be read; null when it worked.</param>
public sealed record BrokerKeyCheck(string? DataError, string? TradingError, string? AccountNumber)
{
    public bool IsOk => DataError is null && TradingError is null;
}

/// <param name="RestartNeeded">Stored keys differ from the ones the app runs with.</param>
public sealed record BrokerKeyStatus(BrokerKeySource Source, string? MaskedKey, bool HasStoredKeys, bool RestartNeeded)
{
    /// <summary>The first 4 and last 4 characters, e.g. <c>PKAB…WXYZ</c>.</summary>
    public static string? Mask(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : key.Length <= 8 ? "…" : $"{key[..4]}…{key[^4..]}";
}

public sealed record GetBrokerKeyStatusQuery : IQuery<BrokerKeyStatus>;

/// <summary>Tries the keys first and stores them only if both market data and the paper account accept them.</summary>
public sealed record SaveBrokerKeysCommand(string ApiKey, string ApiSecret, string? PaperApiKey = null, string? PaperApiSecret = null)
    : ICommand<BrokerKeyStatus>
{
    public override string ToString() => $"{nameof(SaveBrokerKeysCommand)}({BrokerKeyStatus.Mask(ApiKey)})";
}

public sealed record TestBrokerKeysCommand(string ApiKey, string ApiSecret, string? PaperApiKey = null, string? PaperApiSecret = null)
    : ICommand<BrokerKeyCheck>
{
    public override string ToString() => $"{nameof(TestBrokerKeysCommand)}({BrokerKeyStatus.Mask(ApiKey)})";
}

public sealed record RemoveBrokerKeysCommand : ICommand<BrokerKeyStatus>;

public static class BrokerKeyErrors
{
    public static Error Rejected(BrokerKeyCheck check) => Error.Validation(
        new[] { check.DataError is { } data ? "Market data: " + data : null, check.TradingError is { } trading ? "Paper account: " + trading : null }
            .OfType<string>()
            .ToArray());
}

internal static class BrokerKeyRules
{
    /// <summary>Alpaca keys are 20 characters and secrets 40; anything far off is a copy-paste mistake.</summary>
    public static bool LooksLikeKey(string? value) => value is { Length: >= 16 and <= 128 } && !value.Any(char.IsWhiteSpace);

    public static BrokerKeys ToKeys(string apiKey, string apiSecret, string? paperKey, string? paperSecret) => new(
        apiKey.Trim(),
        apiSecret.Trim(),
        string.IsNullOrWhiteSpace(paperKey) ? null : paperKey.Trim(),
        string.IsNullOrWhiteSpace(paperSecret) ? null : paperSecret.Trim());
}

internal abstract class BrokerKeysValidator<T> : AbstractValidator<T>
{
    protected void Keys(Func<T, string> apiKey, Func<T, string> apiSecret, Func<T, string?> paperKey, Func<T, string?> paperSecret)
    {
        RuleFor(c => apiKey(c)).Must(BrokerKeyRules.LooksLikeKey).WithMessage("The API key does not look like an Alpaca key.");
        RuleFor(c => apiSecret(c)).Must(BrokerKeyRules.LooksLikeKey).WithMessage("The secret does not look like an Alpaca secret.");
        RuleFor(c => c)
            .Must(c => string.IsNullOrWhiteSpace(paperKey(c)) == string.IsNullOrWhiteSpace(paperSecret(c)))
            .WithMessage("Enter both the paper key and its secret, or neither.");
        RuleFor(c => paperKey(c))
            .Must(key => string.IsNullOrWhiteSpace(key) || BrokerKeyRules.LooksLikeKey(key.Trim()))
            .WithMessage("The paper key does not look like an Alpaca key.");
    }
}

internal sealed class SaveBrokerKeysCommandValidator : BrokerKeysValidator<SaveBrokerKeysCommand>
{
    public SaveBrokerKeysCommandValidator() => Keys(c => c.ApiKey?.Trim()!, c => c.ApiSecret?.Trim()!, c => c.PaperApiKey, c => c.PaperApiSecret);
}

internal sealed class TestBrokerKeysCommandValidator : BrokerKeysValidator<TestBrokerKeysCommand>
{
    public TestBrokerKeysCommandValidator() => Keys(c => c.ApiKey?.Trim()!, c => c.ApiSecret?.Trim()!, c => c.PaperApiKey, c => c.PaperApiSecret);
}

internal sealed class GetBrokerKeyStatusQueryHandler(IBrokerKeyStore store) : IQueryHandler<GetBrokerKeyStatusQuery, BrokerKeyStatus>
{
    public Task<Result<BrokerKeyStatus>> HandleAsync(GetBrokerKeyStatusQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result<BrokerKeyStatus>.Success(Status(store)));

    internal static BrokerKeyStatus Status(IBrokerKeyStore store)
    {
        var stored = store.Stored;
        var restartNeeded = stored is null
            ? store.SourceInUse == BrokerKeySource.Settings
            : stored.ApiKey != store.KeyInUse;
        return new BrokerKeyStatus(store.SourceInUse, BrokerKeyStatus.Mask(store.KeyInUse), stored is not null, restartNeeded);
    }
}

internal sealed class TestBrokerKeysCommandHandler(IBrokerKeyTester tester) : ICommandHandler<TestBrokerKeysCommand, BrokerKeyCheck>
{
    public async Task<Result<BrokerKeyCheck>> HandleAsync(TestBrokerKeysCommand command, CancellationToken cancellationToken) =>
        await tester.TestAsync(BrokerKeyRules.ToKeys(command.ApiKey, command.ApiSecret, command.PaperApiKey, command.PaperApiSecret), cancellationToken);
}

internal sealed class SaveBrokerKeysCommandHandler(IBrokerKeyStore store, IBrokerKeyTester tester, Audit audit)
    : ICommandHandler<SaveBrokerKeysCommand, BrokerKeyStatus>
{
    public async Task<Result<BrokerKeyStatus>> HandleAsync(SaveBrokerKeysCommand command, CancellationToken cancellationToken)
    {
        var keys = BrokerKeyRules.ToKeys(command.ApiKey, command.ApiSecret, command.PaperApiKey, command.PaperApiSecret);
        var check = await tester.TestAsync(keys, cancellationToken);
        if (!check.IsOk)
            return BrokerKeyErrors.Rejected(check);

        store.Save(keys);
        await audit.RecordAsync(AuditCategory.Session, "KeysSaved",
            $"Broker keys {BrokerKeyStatus.Mask(keys.ApiKey)} saved for paper account {check.AccountNumber}; used from the next start.");
        return GetBrokerKeyStatusQueryHandler.Status(store);
    }
}

internal sealed class RemoveBrokerKeysCommandHandler(IBrokerKeyStore store, Audit audit) : ICommandHandler<RemoveBrokerKeysCommand, BrokerKeyStatus>
{
    public async Task<Result<BrokerKeyStatus>> HandleAsync(RemoveBrokerKeysCommand command, CancellationToken cancellationToken)
    {
        if (store.Stored is not null)
        {
            store.Save(null);
            await audit.RecordAsync(AuditCategory.Session, "KeysRemoved", "Broker keys removed from this computer; the app stops using them at the next start.");
        }

        return GetBrokerKeyStatusQueryHandler.Status(store);
    }
}
