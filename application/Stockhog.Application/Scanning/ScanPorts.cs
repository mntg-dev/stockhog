using Stockhog.Application.Abstractions.Results;
using Stockhog.Domain.Scanning;

namespace Stockhog.Application.Scanning;

/// <summary>Write side: loads scans for changes, which the unit of work saves.</summary>
public interface IScanRunRepository
{
    void Add(ScanRun run);

    Task<ScanRun?> GetAsync(ScanRunId id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScanRun>> ListUnfinishedAsync(CancellationToken cancellationToken = default);
}

/// <summary>Read side: untracked scans with their candidates. Changes to them are never saved.</summary>
public interface IScanReader
{
    /// <returns>Newest first.</returns>
    Task<IReadOnlyList<ScanRun>> ListAsync(int take, CancellationToken cancellationToken = default);

    Task<ScanRun?> FindAsync(ScanRunId id, CancellationToken cancellationToken = default);
}

public static class ScanKeys
{
    /// <summary>Prefix of a scan's job key, followed by the scan id.</summary>
    public const string JobKeyPrefix = "scan:";
}

public static class ScanErrors
{
    public static Error NotFound(string id) => Error.NotFound("Scan.NotFound", $"Scan {id} does not exist.");

    public static Error AlreadyFinished(string id, ScanStatus status) =>
        new("Scan.AlreadyFinished", $"Scan {id} is already {status}.", ErrorType.Conflict);
}
