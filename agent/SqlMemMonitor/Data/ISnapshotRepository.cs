using SqlMemDiag.Core.Reporting;

namespace SqlMemMonitor.Data;

public interface ISnapshotRepository
{
    Task StoreSnapshotAsync(DateTimeOffset timestamp, SqlMemoryReport report, CancellationToken cancellationToken);
    Task PurgeExpiredAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
    Task<IReadOnlyList<SqlSeriesPoint>> GetSqlSeriesAsync(TimeSpan window, CancellationToken cancellationToken);
    Task<SnapshotOverview?> GetLatestSnapshotAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<FindingRecord>> GetRecentFindingsAsync(int limit, CancellationToken cancellationToken);
}
