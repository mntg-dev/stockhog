using Stockhog.Application.Abstractions.Messaging;
using Stockhog.Application.Backtesting.FailInterruptedBacktests;
using Stockhog.Application.Optimization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Stockhog.Infrastructure.Persistence;

/// <summary>
/// At startup: applies pending migrations, switches SQLite to WAL (readers don't block the writer),
/// and marks runs and optimizations left unfinished by a previous session as failed.
/// </summary>
internal sealed class DatabaseInitializer(
    IServiceScopeFactory scopeFactory,
    IDispatcher dispatcher,
    ILogger<DatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            await db.Database.MigrateAsync(cancellationToken);
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
            logger.LogInformation("Database ready: {DataSource}", db.Database.GetDbConnection().DataSource);
        }

        var recovered = await dispatcher.SendAsync(new FailInterruptedBacktestsCommand(), cancellationToken);
        if (recovered is { IsSuccess: true, Value: > 0 })
            logger.LogWarning("Marked {Count} interrupted backtest run(s) as failed", recovered.Value);

        var optimizations = await dispatcher.SendAsync(new FailInterruptedOptimizationsCommand(), cancellationToken);
        if (optimizations is { IsSuccess: true, Value: > 0 })
            logger.LogWarning("Marked {Count} interrupted optimization(s) as failed", optimizations.Value);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
