namespace SqlMemMonitor.Data;

public sealed record SqlSeriesPoint(DateTimeOffset Timestamp, long WorkingSetBytes, long PrivateBytes, long LockedBytes, long LargePageBytes);

public sealed record SnapshotOverview(
    DateTimeOffset Timestamp,
    long TotalPhysicalBytes,
    long AvailablePhysicalBytes,
    long KernelNonPagedBytes,
    long KernelPagedBytes,
    long SystemCacheBytes,
    IReadOnlyList<ProcessSampleRecord> TopProcesses);

public sealed record ProcessSampleRecord(
    uint Pid,
    string ImageName,
    long WorkingSetBytes,
    long PrivateBytes,
    long LockedBytes,
    long LargePageBytes,
    bool IsSqlServer,
    bool IsVmmemOrVm);

public sealed record FindingRecord(
    DateTimeOffset Timestamp,
    string Id,
    string Title,
    string Description,
    double Severity);
