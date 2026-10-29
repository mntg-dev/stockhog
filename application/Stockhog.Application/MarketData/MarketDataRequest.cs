using Stockhog.Domain.Common;
using Stockhog.Domain.MarketData;

namespace Stockhog.Application.MarketData;

public sealed record MarketDataRequest(Symbol Symbol, Timeframe Timeframe, DateRange Period);

/// <param name="BarCount">0 when the provider has no data for the period.</param>
public sealed record MarketDataDownloadResult(string Symbol, int BarCount, int FilesWritten);
