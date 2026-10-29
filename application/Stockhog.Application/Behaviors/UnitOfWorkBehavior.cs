using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Abstractions.Persistence;
using Stockhog.Application.Abstractions.Results;

namespace Stockhog.Application.Behaviors;

/// <summary>
/// Saves the changes of a successful command. Failed results and exceptions leave nothing behind.
/// Commands with several stages (e.g. a long backtest) also save explicitly between stages; this final save is then a no-op.
/// </summary>
internal sealed class UnitOfWorkBehavior<TRequest, TResponse>(IUnitOfWork unitOfWork) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<Result<TResponse>> HandleAsync(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var result = await next().ConfigureAwait(false);

        if (request is ICommand<TResponse> && result.IsSuccess)
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return result;
    }
}
