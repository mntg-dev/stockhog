using Stockhog.Domain.Backtesting;
using Stockhog.Domain.MarketData;
using Stockhog.Domain.Optimization;
using Stockhog.Domain.Proposals;
using Stockhog.Domain.Scanning;
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

    public DbSet<ScanRun> ScanRuns => Set<ScanRun>();

    public DbSet<TradeProposal> TradeProposals => Set<TradeProposal>();

    public DbSet<EquityPeakRecord> EquityPeaks => Set<EquityPeakRecord>();

    public DbSet<Domain.Autopilot.Autopilot> Autopilots => Set<Domain.Autopilot.Autopilot>();

    public DbSet<AutopilotDecisionRecord> AutopilotDecisions => Set<AutopilotDecisionRecord>();

    public DbSet<ScreenerRunRecord> ScreenerRuns => Set<ScreenerRunRecord>();

    public DbSet<ScreenerRowRecord> ScreenerRows => Set<ScreenerRowRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TradingDbContext).Assembly);

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite cannot sort or compare DateTimeOffset; all timestamps are UTC, so store them as UTC DateTime.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
    }
}
