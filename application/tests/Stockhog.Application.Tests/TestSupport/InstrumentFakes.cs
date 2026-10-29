using Stockhog.Application.Instruments;

namespace Stockhog.Application.Tests.TestSupport;

internal sealed class FakeInstrumentSource : IInstrumentSource
{
    public bool IsConfigured { get; set; } = true;

    public List<InstrumentInfo> Instruments { get; } = [];

    public int Calls { get; private set; }

    public Task<IReadOnlyList<InstrumentInfo>> GetTradableInstrumentsAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<InstrumentInfo>>(Instruments.ToList());
    }
}

internal sealed class InMemoryInstrumentStore : IInstrumentStore
{
    public List<InstrumentInfo> Instruments { get; private set; } = [];

    public DateTimeOffset? RefreshedAt { get; set; }

    public Task<InstrumentCatalogStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new InstrumentCatalogStatus(Instruments.Count, Instruments.Count == 0 ? null : RefreshedAt));

    public Task<IReadOnlyList<InstrumentInfo>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<InstrumentInfo>>(Instruments.OrderBy(i => i.Symbol, StringComparer.Ordinal).ToList());

    public Task ReplaceAllAsync(IReadOnlyList<InstrumentInfo> instruments, DateTimeOffset refreshedAt, CancellationToken cancellationToken = default)
    {
        Instruments = instruments.ToList();
        RefreshedAt = refreshedAt;
        return Task.CompletedTask;
    }
}
