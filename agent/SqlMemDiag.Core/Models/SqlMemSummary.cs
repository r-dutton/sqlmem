namespace SqlMemDiag.Core.Models;

public sealed record SqlMemSummary
{
    public SqlMemSummary(SqlMemSummaryNative header, IReadOnlyList<SqlMemProcessEntry> processes)
    {
        Version = header.Version;
        Processes = processes;
        TotalPhysBytes = header.TotalPhysBytes;
        AvailPhysBytes = header.AvailPhysBytes;
        KernelNonPagedBytes = header.KernelNonPagedBytes;
        KernelPagedBytes = header.KernelPagedBytes;
        SystemCacheBytes = header.SystemCacheBytes;
        UsesForensicPfns = header.UsesForensicPfns;
    }

    public uint Version { get; }
    public IReadOnlyList<SqlMemProcessEntry> Processes { get; }
    public ulong TotalPhysBytes { get; }
    public ulong AvailPhysBytes { get; }
    public ulong KernelNonPagedBytes { get; }
    public ulong KernelPagedBytes { get; }
    public ulong SystemCacheBytes { get; }
    public bool UsesForensicPfns { get; }

    public double TotalPhysicalGiB => BytesToGiB(TotalPhysBytes);
    public double AvailablePhysicalGiB => BytesToGiB(AvailPhysBytes);
    public double KernelNonPagedGiB => BytesToGiB(KernelNonPagedBytes);
    public double KernelPagedGiB => BytesToGiB(KernelPagedBytes);
    public double SystemCacheGiB => BytesToGiB(SystemCacheBytes);

    private static double BytesToGiB(ulong value) => value / 1024d / 1024d / 1024d;
}
