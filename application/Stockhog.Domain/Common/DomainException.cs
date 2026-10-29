namespace Stockhog.Domain.Common;

/// <summary>
/// A domain rule was broken. Input is validated before it reaches the domain, so this signals a programming error.
/// </summary>
public sealed class DomainException(string message) : Exception(message);
