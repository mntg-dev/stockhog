using System.Diagnostics.CodeAnalysis;

namespace Stockhog.Application.Abstractions.Results;

public enum ErrorType
{
    Failure,
    Validation,
    NotFound,
    Conflict
}

/// <summary>
/// An expected failure, e.g. invalid input or missing data. Unexpected failures stay exceptions.
/// </summary>
public sealed record Error(string Code, string Message, ErrorType Type = ErrorType.Failure)
{
    /// <summary>Individual messages, e.g. one per failed validation rule.</summary>
    public IReadOnlyList<string> Details { get; init; } = [];

    public static Error Failure(string code, string message) => new(code, message);

    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    public static Error Validation(IReadOnlyList<string> messages) =>
        new("Validation", string.Join(" ", messages), ErrorType.Validation) { Details = messages };
}

/// <summary>
/// Outcome of a command or query: either a value or an <see cref="Error"/>.
/// </summary>
public sealed class Result<T>
{
    private readonly T? _value;

    private Result(T? value, Error? error)
    {
        _value = value;
        Error = error;
    }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsFailure => !IsSuccess;

    public Error? Error { get; }

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException($"Result is a failure: {Error.Code}.");

    public static Result<T> Success(T value) => new(value, null);

    public static Result<T> Failure(Error error) => new(default, error);

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => Failure(error);

    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<Error, TOut> onFailure) =>
        IsSuccess ? onSuccess(_value!) : onFailure(Error);
}
