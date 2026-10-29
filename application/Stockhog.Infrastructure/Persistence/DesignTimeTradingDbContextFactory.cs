using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Stockhog.Infrastructure.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c> to create migrations, e.g.:
/// <c>dotnet ef migrations add Name --project Stockhog.Infrastructure --output-dir Persistence/Migrations</c>
/// </summary>
internal sealed class DesignTimeTradingDbContextFactory : IDesignTimeDbContextFactory<TradingDbContext>
{
    public TradingDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
