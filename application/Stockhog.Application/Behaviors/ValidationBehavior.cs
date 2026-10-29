using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Results;
using FluentValidation;

namespace Stockhog.Application.Behaviors;

/// <summary>
/// Runs every FluentValidation validator for the request; any failure short-circuits with a validation <see cref="Error"/>.
/// </summary>
internal sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<Result<TResponse>> HandleAsync(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(request, cancellationToken).ConfigureAwait(false);
            failures.AddRange(result.Errors.Select(e => e.ErrorMessage));
        }

        return failures.Count == 0
            ? await next().ConfigureAwait(false)
            : Error.Validation(failures.Distinct().ToList());
    }
}
