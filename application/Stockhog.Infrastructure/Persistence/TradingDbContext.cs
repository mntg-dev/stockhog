using Stockhog.Domain.Backtesting;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Optimization;
using Stockhog.Domain.Trading;
using Stockhog.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;

namespace Stockhog.Infrastructure.Persistence;

internal sealed class TradingDbContext(DbContextOptions<TradingDbContext> options) : DbContext(options)
{
    public DbSet<BacktestRun> BacktestRuns => Set<BacktestRun>();

    public DbSet<MarketDataSet> MarketDataSets => Set<MarketDataSet>();

    public DbSet<OptimizationRun> OptimizationRuns => Set<OptimizationRun>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<AuditRecord> AuditLog => Set<AuditRecord>();

    public DbSet<InstrumentRecord> Instruments => Set<InstrumentRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TradingDbContext).Assembly);

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite cannot sort or compare DateTimeOffset; all timestamps are UTC, so store them as UTC DateTime.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
    }
}
